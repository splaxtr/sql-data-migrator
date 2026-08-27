namespace Migrator.App;

using Migrator.App.Reporting;
using Migrator.Core;

/// <summary>One source database and the name it should get in the target.</summary>
internal sealed record BatchDatabase(string SourceDatabase, string TargetDatabase);

/// <summary>
/// Runs a list of databases through the engine one after another.
///
/// <para>Sequential on purpose: both ends are the same two servers, so running four
/// migrations at once would not finish sooner — it would only make the log unreadable and
/// the failure modes harder to reason about. One database failing does not stop the rest;
/// a batch that gives up halfway is worse than no batch at all.</para>
/// </summary>
internal static class BatchRunner
{
    public static async Task RunAsync(
        ConnectionStore store, Job job, MigrateRequest request, CancellationToken ct = default)
    {
        var progress = new InlineProgress<ProgressMessage>(job.Add);
        var servers = await store.ListAsync();
        var outcomes = new List<DatabaseOutcome>();

        var options = new MigrationOptions
        {
            AllowSourceOnlyTables = request.AllowSourceOnly,
            MirrorMissingTables = request.MirrorMissingTables,
            AllowSchemaRisk = request.AllowSchemaRisk,
            AllowCollationMismatch = request.AllowCollationMismatch,
            VerifyOnly = request.Mode == RunMode.VerifyOnly,
            MigrateHistoryTables = request.MigrateHistoryTables,
            ExpectedIcuLocale = string.IsNullOrWhiteSpace(request.TargetIcuLocale) ? null : request.TargetIcuLocale,
        };

        var total = request.Databases.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var database = request.Databases[i];

            // A single database has no batch to keep track of; the banner would be noise.
            if (total > 1)
                progress.Report(new(ProgressKind.Step,
                    $"[{i + 1}/{total}] {database.SourceDatabase} → {database.TargetDatabase}",
                    AppMessageCode.StepBatchDatabase,
                    new object?[] { i + 1, total, database.SourceDatabase, database.TargetDatabase }));

            // Watches the same stream the browser sees and keeps the lines that belong in
            // the report. Collecting by code rather than changing the engine's result type:
            // these are observations about a run, and the engine already reports them.
            var notes = new List<string>();
            var watched = new InlineProgress<ProgressMessage>(m =>
            {
                if (TurkishNote(m) is { } note) notes.Add(note);
                progress.Report(m);
            });
            var outcome = await RunOneAsync(store, request, options, database, watched, ct);
            outcomes.Add(notes.Count > 0 ? outcome with { SchemaNotes = notes } : outcome);
        }

        var report = new MigrationReport(
            DateTimeOffset.Now,
            Describe(servers, request.SourceServerId),
            Describe(servers, request.TargetServerId),
            request.Mode,
            outcomes);

        var succeeded = report.SucceededCount;
        var failed = report.FailedCount;

        if (total > 1)
        {
            // A row count is the measure of a migration and of nothing else: the other two
            // modes copy nothing, and printing "0 rows" next to a clean run reads as a
            // failure rather than as a mode.
            if (request.Mode == RunMode.Migrate)
                progress.Report(new(ProgressKind.Info,
                    $"Batch finished: {succeeded} succeeded, {failed} failed, {report.TotalRows} rows.",
                    AppMessageCode.InfoBatchSummary, new object?[] { succeeded, failed, report.TotalRows }));
            else
                progress.Report(new(ProgressKind.Info,
                    $"Batch finished: {succeeded} succeeded, {failed} failed.",
                    AppMessageCode.InfoBatchSummaryNoRows, new object?[] { succeeded, failed }));
        }

        AttachReport(job, report, progress);

        var (verb, okCode, partialCode) = request.Mode switch
        {
            RunMode.VerifyOnly => ("verified", AppMessageCode.SuccessBatchVerified, AppMessageCode.FailBatchVerifyPartial),
            RunMode.ProvisionOnly => ("provisioned", AppMessageCode.SuccessBatchProvisioned, AppMessageCode.FailBatchProvisionPartial),
            _ => ("migrated", AppMessageCode.SuccessBatchAll, AppMessageCode.FailBatchPartial),
        };

        job.Finish(failed == 0,
            failed == 0
                ? $"{succeeded} databases {verb}."
                : $"{succeeded} databases {verb}, {failed} failed.",
            failed == 0 ? okCode : partialCode,
            new object?[] { succeeded, failed });
    }

    private static async Task<DatabaseOutcome> RunOneAsync(
        ConnectionStore store, MigrateRequest request, MigrationOptions options,
        BatchDatabase database, IProgress<ProgressMessage> progress, CancellationToken ct)
    {
        var source = await store.BuildConnectionStringAsync(request.SourceServerId, database.SourceDatabase);
        var target = await store.BuildConnectionStringAsync(request.TargetServerId, database.TargetDatabase);
        if (source is null || target is null)
        {
            progress.Report(new(ProgressKind.Error, "The saved server was not found.",
                AppMessageCode.ErrorServerNotFound));
            return Failed(database, "Kayıtlı sunucu bulunamadı.");
        }

        try
        {
            // Read before you write: creating the target first would leave an empty
            // database behind for every source the batch cannot open. In ProvisionOnly this
            // probe is the only thing the source is used for, and it is worth keeping — a
            // target named after a source that is not there is a typo, not a plan.
            await SchemaReader.ProbeSqlServerAsync(source, ct);

            var state = TargetDatabaseState.AlreadyExisted;
            if (request.Mode != RunMode.VerifyOnly)
            {
                state = await TargetDatabase.EnsureCreatedAsync(target, request.TargetIcuLocale, progress, ct);
                if (state == TargetDatabaseState.Failed)
                    return Failed(database, "Hedef veritabanı hazırlanamadı.");
            }

            // ProvisionOnly stops here: the database exists, and the engine has nothing to
            // do that would not mean touching tables this mode promised to leave alone.
            MigrationResult? result = null;
            if (request.Mode != RunMode.ProvisionOnly)
            {
                result = await new MigrationEngine(progress).RunAsync(source, target, options, ct);
                if (!result.Succeeded)
                    return Failed(database, TurkishOutcome(result), result.Duration);
            }

            ProvisionedUser? user = null;
            string? userNote = null;
            if (request.CreateUsers && request.Mode != RunMode.VerifyOnly)
            {
                var maintenance = await store.BuildConnectionStringAsync(request.TargetServerId, null);
                var role = UserProvisioner.BuildRoleName(request.UserNamePattern, database.TargetDatabase);
                user = await UserProvisioner.EnsureAsync(maintenance!, target, database.TargetDatabase, role,
                    state == TargetDatabaseState.Created, progress, ct);
                // An existing role needs no note: the report already prints "Zaten vardı"
                // next to it and says so again where the password would be.
                if (user is null)
                {
                    // In ProvisionOnly the role is half of what was asked for, and there is
                    // no migrated data whose success could carry the run on its own. Calling
                    // that database "hazırlandı" would be reporting success it did not earn.
                    if (request.Mode == RunMode.ProvisionOnly)
                        return Failed(database,
                            "Veritabanı hazır ama kullanıcı oluşturulamadı — ayrıntı için günlüğe bakın.");
                    userNote = "Kullanıcı oluşturulamadı — ayrıntı için taşıma günlüğüne bakın.";
                }
            }

            return new DatabaseOutcome(
                database.SourceDatabase, database.TargetDatabase, true,
                result?.RowsCopied ?? 0, result?.Duration ?? TimeSpan.Zero,
                // A provisioning run has no row count to speak for it, so the row it puts in
                // the report says which of the two things happened instead.
                request.Mode == RunMode.ProvisionOnly
                    ? (state == TargetDatabaseState.Created ? "Oluşturuldu" : "Zaten vardı — dokunulmadı")
                    : "",
                user?.Role, user?.Password, user?.Created ?? false, userNote,
                state == TargetDatabaseState.Created);
        }
        catch (Exception ex)
        {
            progress.Report(new(ProgressKind.Error, ex.Message));
            return Failed(database, ex.Message);
        }
    }

    private static void AttachReport(Job job, MigrationReport report, IProgress<ProgressMessage> progress)
    {
        try
        {
            var stamp = report.CompletedAt.ToLocalTime().ToString("yyyyMMdd-HHmm");
            job.AttachReport(MigrationReportPdf.Build(report),
                $"{MigrationReportPdf.FileNameFor(report.Mode)}-{stamp}.pdf");
            progress.Report(new(ProgressKind.Info, "The PDF report is ready to download.",
                AppMessageCode.InfoReportReady));
        }
        catch (Exception ex)
        {
            // A report that cannot be drawn must not turn a finished migration into a
            // failure: the data has already moved, and the log holds everything the PDF would.
            progress.Report(new(ProgressKind.Warning, $"The PDF report could not be produced: {ex.Message}",
                AppMessageCode.WarnReportFailed, new object?[] { ex.Message }));
        }
    }

    private static DatabaseOutcome Failed(BatchDatabase database, string note, TimeSpan duration = default)
        => new(database.SourceDatabase, database.TargetDatabase, false, 0, duration, note);

    private static string Describe(IEnumerable<ServerProfile> servers, string id)
    {
        var server = servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return "—";
        var kind = server.Kind == ServerKind.SqlServer ? "SQL Server" : "PostgreSQL";
        return $"{server.Name} · {kind} · {server.Host}:{server.Port} · {server.User}";
    }

    /// <summary>
    /// The handful of progress lines that outlive the run, worded for the PDF.
    ///
    /// <para>A row count says the data arrived; it cannot say that four of those columns
    /// were invented, or that a schema was left behind. Those facts scroll past in the log
    /// and are gone, so the ones that change what the target actually contains are kept.</para>
    /// </summary>
    private static string? TurkishNote(ProgressMessage message) => message.Code switch
    {
        MessageCode.WarnColumnSynthesized =>
            $"{message.Args?[0]}.{message.Args?[1]}: kaynakta yok, NOT NULL — her satıra {message.Args?[3]} yazıldı ({message.Args?[2]}).",
        MessageCode.WarnSourceColumnDropped =>
            $"{message.Args?[0]}.{message.Args?[1]}: hedefte karşılığı yok ({message.Args?[2]}) — verisi taşınmadı.",
        MessageCode.WarnSourceOnlyTable =>
            $"Kaynak tablosu '{message.Args?[0]}' hedefte yok — verisi taşınmadı.",
        MessageCode.WarnSourceSchemaSkipped =>
            $"dbo dışında {message.Args?[0]} tablo var ({message.Args?[1]}) — bu araç yalnızca dbo okur, taşınmadılar.",
        MessageCode.InfoHistoryPreserved =>
            $"'{message.Args?[0]}' hedefin kendi migration geçmişi ({message.Args?[1]} satır) — dokunulmadı, kopyalanmadı.",
        MessageCode.WarnHistoryCopied =>
            $"'{message.Args?[0]}': hedefin migration geçmişi kaynaktakiyle DEĞİŞTİRİLDİ. ORM bu hedefi tanımayabilir.",
        MessageCode.WarnMirrorNoHistory =>
            $"{message.Args?[0]} hedefte oluşturulmadı — aynalanan şemanın ORM migration geçmişi yok, ORM bu şemayı tanımaz.",
        MessageCode.WarnMirrorSkippedRowVersion =>
            $"{message.Args?[0]}.{message.Args?[1]}: rowversion aynalanmadı — kaynak sunucunun ürettiği bir değer, hedefte karşılığı yok.",
        MessageCode.WarnCascadePreview =>
            $"TRUNCATE CASCADE, kopyalanmayan {message.Args?[0]} hedef tabloyu da boşalttı: {message.Args?[1]}. Boş kaldılar.",
        _ => null,
    };

    /// <summary>
    /// The engine reports in English and leaves translation to whoever displays the message.
    /// The PDF has no such layer after it, so the handful of outcomes that can reach a report
    /// row are worded here. The browser's own dictionary in app.js covers the rest.
    /// </summary>
    private static string TurkishOutcome(MigrationResult result) => result.Code switch
    {
        MessageCode.FailCollationMismatch => "Collation uyuşmazlığı — hedefin collation'ı beklenenden farklı.",
        MessageCode.FailSchemaMismatch => "Şema uyuşmazlığı — kaynaktaki bazı tablolar hedefte yok.",
        MessageCode.FailMirrorFailed => "Şema aynalanamadı — karşılığı olmayan bir kolon tipi var.",
        MessageCode.FailEmptyIntersection => "Kopyalanacak ortak tablo yok — hedefte şema bulunamadı.",
        MessageCode.FailPreflightUnresolved => "Ön kontrol uyumsuzlukları giderilmedi.",
        MessageCode.FailVerifyFailed => "Doğrulama başarısız — hedefe hiçbir şey yazılmadı.",
        MessageCode.FailZeroRows => "Hiç satır kopyalanmadı; commit edilmedi.",
        MessageCode.FailTargetDbNotReady => "Hedef veritabanı hazırlanamadı.",
        _ => result.Summary,
    };
}

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> hands each message to the thread
/// pool, which lets log lines overtake one another — and a log whose order is a suggestion
/// is not a log.
/// </summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}
