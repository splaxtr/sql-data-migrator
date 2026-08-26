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

// Yalnız localhost: uygulama üretim veritabanlarının kimlik bilgilerini tutuyor,
// yerel kalması bir güvenlik özelliğidir.
builder.WebHost.UseUrls("http://localhost:5099");

var app = builder.Build();

// Arayüz yayında assembly'den servis edilir: tek dosyalık exe'nin yanında wwwroot yoktur.
// Geliştirmede fiziksel dosyalar kalır ki JS/CSS düzenlemesi yeniden derleme istemesin.
IFileProvider ui = app.Environment.IsDevelopment()
    ? app.Environment.WebRootFileProvider
    : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });

// ── Kayıtlı sunucular ────────────────────────────────────────────────────────

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

// ── Veritabanı listeleri (canlı, sunucudan okunur) ───────────────────────────

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

// ── Taşıma ──────────────────────────────────────────────────────────────────

app.MapPost("/api/migrate", async (ConnectionStore store, JobRegistry jobs, MigrateRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.TargetDatabase))
        return Results.BadRequest(new { error = "Hedef veritabanı adı zorunludur." });

    var source = await store.BuildConnectionStringAsync(request.SourceServerId, request.SourceDatabase);
    var target = await store.BuildConnectionStringAsync(request.TargetServerId, request.TargetDatabase);
    if (source is null || target is null)
        return Results.BadRequest(new { error = "Kayıtlı sunucu bulunamadı." });

    var job = jobs.Create();

    // Ateşle ve bırak: ilerleme /api/jobs/{id} üzerinden okunur. İstek boyunca beklemek,
    // uzun taşımalarda tarayıcı zaman aşımına düşürür.
    _ = Task.Run(async () =>
    {
        try
        {
            var progress = new Progress<ProgressMessage>(m => job.Add(m));
            if (!request.VerifyOnly)
            {
                var created = await TargetDatabase.EnsureCreatedAsync(
                    target, request.TargetIcuLocale, progress);
                if (!created)
                {
                    job.Finish(false, "The target database could not be prepared.", MessageCode.FailTargetDbNotReady);
                    return;
                }
            }

            var engine = new MigrationEngine(progress);
            var result = await engine.RunAsync(source, target, new MigrationOptions
            {
                AllowSourceOnlyTables = request.AllowSourceOnly,
                AllowSchemaRisk = request.AllowSchemaRisk,
                AllowCollationMismatch = request.AllowCollationMismatch,
                VerifyOnly = request.VerifyOnly,
                ExpectedIcuLocale = string.IsNullOrWhiteSpace(request.TargetIcuLocale) ? null : request.TargetIcuLocale,
            });
            job.Finish(result.Succeeded, result.Summary, result.Code, result.Args);
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

// İkinci bir kopyanın "5099'daki gerçekten ben miyim?" sorusuna cevabı.
app.MapGet("/api/ping", () => Results.Ok(new { app = "SqlDataMigrator" }));

// Exe ikinci kez çift tıklanırsa çökmek yerine çalışan kopyanın arayüzü açılır.
if (await IsAlreadyRunningAsync())
{
    Console.WriteLine("Uygulama zaten çalışıyor — tarayıcıda http://localhost:5099 açılıyor.");
    OpenBrowser();
    return;
}

// Exe çift tıklanınca arayüz kendiliğinden açılır; geliştirmede her yeniden
// başlatmada tarayıcı fırlatmamak için yalnız yayında.
if (!app.Environment.IsDevelopment())
    app.Lifetime.ApplicationStarted.Register(OpenBrowser);

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    // Portu tutan biz değiliz (yukarıdaki kontrol geçildi): kullanıcıya çökme
    // yerine ne yapacağını söyle. Çift tıklamada pencere anında kapanmasın.
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
        // Bağlantı reddi = port boş; zaman aşımı/başka cevap = bizim değil.
        return false;
    }
}

static void OpenBrowser()
{
    try { Process.Start(new ProcessStartInfo("http://localhost:5099") { UseShellExecute = true }); }
    catch { /* tarayıcı açılamadıysa adres konsolda yazıyor */ }
}

// ── İstek tipleri ───────────────────────────────────────────────────────────

internal sealed record SaveServerRequest(
    string? Id, string Name, ServerKind Kind, string Host, int Port, string? User, string? Password);

internal sealed record MigrateRequest(
    string SourceServerId,
    string SourceDatabase,
    string TargetServerId,
    string TargetDatabase,
    string? TargetIcuLocale,
    bool AllowSourceOnly,
    bool AllowSchemaRisk,
    bool AllowCollationMismatch,
    bool VerifyOnly);

/// <summary>Koşan taşımaların ilerlemesini bellekte tutar; uygulama kapanınca kaybolur.</summary>
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

    public void Add(ProgressMessage message)
    {
        lock (_gate) _messages.Add(message);
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
                next = _messages.Count, messages = slice,
            };
        }
    }
}
