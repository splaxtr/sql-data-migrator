using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Migrator.App;
using Migrator.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDataProtection().SetApplicationName("SqlToSqlMigrator");
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton<JobRegistry>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Localhost only: the app holds credentials for production databases, and staying
// local is a security feature.
builder.WebHost.UseUrls("http://localhost:5099");

var app = builder.Build();

// In production the UI is served from the assembly: a single-file exe has no wwwroot
// next to it. Development keeps the physical files so editing JS/CSS needs no rebuild.
IFileProvider ui = app.Environment.IsDevelopment()
    ? app.Environment.WebRootFileProvider
    : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });

// ── Saved servers ────────────────────────────────────────────────────────────

app.MapGet("/api/servers", async (ConnectionStore store) => Results.Ok(await store.ListAsync()));

app.MapPost("/api/servers", async (ConnectionStore store, SaveServerRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Host))
        return Results.BadRequest(new { error = "Ad ve sunucu adresi zorunludur." });

    var saved = await store.SaveAsync(
        request.Id, request.Name.Trim(), request.Kind, request.Host.Trim(),
        request.Port, request.User?.Trim() ?? "", request.Password);
    return Results.Ok(saved);
});

app.MapDelete("/api/servers/{id}", async (ConnectionStore store, string id) =>
    await store.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

// ── Database lists (live, read from the server) ──────────────────────────────

app.MapGet("/api/servers/{id}/databases", async (ConnectionStore store, string id) =>
{
    var servers = await store.ListAsync();
    var server = servers.FirstOrDefault(s => s.Id == id);
    if (server is null) return Results.NotFound();

    var connectionString = await store.BuildConnectionStringAsync(id, null);
    if (connectionString is null) return Results.NotFound();

    try
    {
        var databases = server.Kind == ServerKind.SqlServer
            ? await SchemaReader.ListSqlServerDatabasesAsync(connectionString)
            : await SchemaReader.ListPostgresDatabasesAsync(connectionString);
        return Results.Ok(databases);
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Sunucuya bağlanılamadı", detail: ex.Message, statusCode: 502);
    }
});

// ── Migration ───────────────────────────────────────────────────────────────

app.MapPost("/api/migrate", (ConnectionStore store, JobRegistry jobs, MigrateRequest request) =>
{
    if (request.Databases is not { Count: > 0 })
        return Results.BadRequest(new { error = "Taşınacak veritabanı seçilmedi." });
    if (request.Databases.Any(d => string.IsNullOrWhiteSpace(d.SourceDatabase) || string.IsNullOrWhiteSpace(d.TargetDatabase)))
        return Results.BadRequest(new { error = "Her satırda kaynak ve hedef veritabanı adı dolu olmalıdır." });

    var duplicate = request.Databases
        .GroupBy(d => d.TargetDatabase, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(g => g.Count() > 1);
    if (duplicate is not null)
        return Results.BadRequest(new { error = $"'{duplicate.Key}' hedef adı birden fazla kez kullanılmış." });

    var job = jobs.Create();

    // Fire and forget: progress is read via /api/jobs/{id}. Holding the request open
    // would hit browser timeouts on long migrations.
    _ = Task.Run(async () =>
    {
        try
        {
            await BatchRunner.RunAsync(store, job, request);
        }
        catch (Exception ex)
        {
            job.Add(new ProgressMessage(ProgressKind.Error, ex.Message));
            job.Finish(false, "The migration stopped with an exception.", MessageCode.FailException);
        }
    });

    return Results.Ok(new { jobId = job.Id });
});

app.MapGet("/api/jobs/{id}", (JobRegistry jobs, string id, int from) =>
{
    var job = jobs.Get(id);
    return job is null ? Results.NotFound() : Results.Ok(job.Read(from));
});

// The report exists only in this process's memory: it carries plain-text passwords, and
// writing it to disk would leave them behind long after the browser was done with them.
app.MapGet("/api/jobs/{id}/report.pdf", (JobRegistry jobs, string id) =>
{
    var job = jobs.Get(id);
    return job?.Report is null
        ? Results.NotFound()
        : Results.File(job.Report, "application/pdf", job.ReportFileName);
});

// Answers a second copy's question: "is the thing on 5099 really me?"
app.MapGet("/api/ping", () => Results.Ok(new { app = "SqlDataMigrator" }));

// A second double-click opens the running copy's UI instead of crashing.
if (await IsAlreadyRunningAsync())
{
    Console.WriteLine("Uygulama zaten çalışıyor — tarayıcıda http://localhost:5099 açılıyor.");
    OpenBrowser();
    return;
}

// Double-clicking the exe opens the UI by itself; production only, so development
// restarts don't keep launching browsers.
if (!app.Environment.IsDevelopment())
    app.Lifetime.ApplicationStarted.Register(OpenBrowser);

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    // Whoever holds the port is not us (the check above passed): tell the user what to
    // do instead of crashing, and keep the window from vanishing on double-click.
    Console.WriteLine();
    Console.WriteLine("HATA: 5099 portu başka bir uygulama tarafından kullanılıyor.");
    Console.WriteLine("O uygulamayı kapatıp bunu yeniden başlatın.");
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Kapatmak için bir tuşa basın...");
        Console.ReadKey(intercept: true);
    }
    Environment.Exit(1);
}

static async Task<bool> IsAlreadyRunningAsync()
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var body = await http.GetStringAsync("http://localhost:5099/api/ping");
        return body.Contains("SqlDataMigrator", StringComparison.Ordinal);
    }
    catch
    {
        // Connection refused = port free; timeout or a different answer = not ours.
        return false;
    }
}

static void OpenBrowser()
{
    try { Process.Start(new ProcessStartInfo("http://localhost:5099") { UseShellExecute = true }); }
    catch { /* if no browser opened, the address is on the console */ }
}

// ── Request types ───────────────────────────────────────────────────────────

internal sealed record SaveServerRequest(
    string? Id, string Name, ServerKind Kind, string Host, int Port, string? User, string? Password);

/// <summary>
/// One run over one or more databases. A single migration is a batch of one — the shape
/// does not change, so neither does the code path behind it.
/// </summary>
internal sealed record MigrateRequest(
    string SourceServerId,
    string TargetServerId,
    List<BatchDatabase> Databases,
    string? TargetIcuLocale,
    bool CreateUsers,
    string? UserNamePattern,
    bool AllowSourceOnly,
    bool MirrorMissingTables,
    bool AllowSchemaRisk,
    bool AllowCollationMismatch,
    RunMode Mode);

/// <summary>Holds running migrations' progress in memory; gone when the app exits.</summary>
internal sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public Job Create()
    {
        var job = new Job(Guid.NewGuid().ToString("N"));
        _jobs[job.Id] = job;
        return job;
    }

    public Job? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;
}

internal sealed class Job
{
    private readonly List<ProgressMessage> _messages = new();
    private readonly object _gate = new();

    public Job(string id) => Id = id;

    public string Id { get; }
    public bool Done { get; private set; }
    public bool? Succeeded { get; private set; }
    public string? Summary { get; private set; }
    public string? SummaryCode { get; private set; }
    public object?[]? SummaryArgs { get; private set; }

    /// <summary>The finished PDF, held in memory only. Set before <see cref="Done"/>.</summary>
    public byte[]? Report { get; private set; }
    public string? ReportFileName { get; private set; }

    public void Add(ProgressMessage message)
    {
        lock (_gate) _messages.Add(message);
    }

    public void AttachReport(byte[] pdf, string fileName)
    {
        lock (_gate)
        {
            Report = pdf;
            ReportFileName = fileName;
        }
    }

    public void Finish(bool succeeded, string summary, string? code = null, object?[]? args = null)
    {
        lock (_gate)
        {
            Succeeded = succeeded;
            Summary = summary;
            SummaryCode = code;
            SummaryArgs = args;
            Done = true;
        }
    }

    public object Read(int from)
    {
        lock (_gate)
        {
            var slice = _messages.Skip(from)
                .Select(m => new { kind = m.Kind.ToString(), text = m.Text, code = m.Code, args = m.Args })
                .ToList();
            return new
            {
                done = Done, succeeded = Succeeded,
                summary = Summary, summaryCode = SummaryCode, summaryArgs = SummaryArgs,
                hasReport = Report is not null,
                next = _messages.Count, messages = slice,
            };
        }
    }
}
