namespace Migrator.Core;

using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// SQL Server -> PostgreSQL data migration engine.
///
/// Design decision: the copy AND its verification run inside one transaction; nothing is
/// committed until verification passes. A successful exit therefore means the data really
/// moved, and a failure leaves no half-filled target. The schema must already EXIST in
/// the target — except in mirror mode, which first creates source-only tables from the
/// source schema (see <see cref="SchemaMirror"/>).
/// </summary>
public sealed class MigrationEngine
{
    private readonly IProgress<ProgressMessage> _progress;

    public MigrationEngine(IProgress<ProgressMessage> progress) => _progress = progress;

    public async Task<MigrationResult> RunAsync(
        string sourceConnectionString,
        string targetConnectionString,
        MigrationOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var sql = new SqlConnection(sourceConnectionString);
        await sql.OpenAsync(ct);
        await using var pg = new NpgsqlConnection(targetConnectionString);
        await pg.OpenAsync(ct);
        // TRUNCATE CASCADE emits a separate notice for every dependent table; on a large
        // schema that is hundreds of lines burying the real progress. They are counted and
        // summarized in one line, but not swallowed entirely: which tables were emptied is
        // something the operator needs to know.
        var cascaded = new List<string>();
        pg.Notice += (_, e) =>
        {
            var text = e.Notice.MessageText;
            const string marker = "truncate cascades to table ";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) cascaded.Add(text[(index + marker.Length)..].Trim('"', ' ', '.'));
            else _progress.Report(new(ProgressKind.Info, $"(pg) {text}",
                MessageCode.InfoPostgresNotice, new object?[] { text }));
        };

        if (!await TargetDatabase.CheckCollationAsync(pg, options.ExpectedIcuLocale, options.AllowCollationMismatch, _progress, ct))
            return Fail(stopwatch, "Collation mismatch.", MessageCode.FailCollationMismatch);

        _progress.Report(new(ProgressKind.Step, "Reading schemas", MessageCode.StepReadingSchemas));
        var sourceSchema = await SchemaReader.ReadSqlServerAsync(sql, ct);
        var targetSchema = await SchemaReader.ReadPostgresAsync(pg, ct);

        // VerifyOnly promises to not touch the target, and that promise outranks the mirror.
        if (options.MirrorMissingTables && !options.VerifyOnly)
        {
            var missing = sourceSchema.Keys
                .Except(targetSchema.Keys, StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            // An ORM-managed source says so by carrying a migration history, and its schema
            // is the ORM's to define. Warned here, at the point the mirror is about to run,
            // and never used to override the choice: the detection is by table name and a
            // false positive must not cancel something the operator asked for.
            var sourceHistory = MigrationHistory.In(sourceSchema.Keys);
            if (sourceHistory.Count > 0)
                _progress.Report(new(ProgressKind.Warning,
                    $"The source is ORM-managed ({string.Join(", ", sourceHistory)}). Its schema belongs to that ORM's " +
                    "migrations; mirroring reproduces provider-specific columns the ORM does not map. " +
                    "Apply the ORM's own migrations to the target instead.",
                    MessageCode.WarnMirrorOrmManaged,
                    new object?[] { string.Join(", ", sourceHistory) }));

            if (!options.MigrateHistoryTables)
            {
                // Creating an empty one would produce the exact crash this tool now exists to
                // prevent: the mirror has just built the schema, the ORM reads a history with
                // nothing in it, concludes no migration was ever applied, re-runs the baseline
                // and hits tables that already exist. A target that has the schema but no
                // history was not built by an ORM in the first place — if an ORM built the
                // schema it wrote the history too.
                var skipped = MigrationHistory.In(missing);
                if (skipped.Count > 0)
                {
                    missing = missing.Except(skipped, StringComparer.Ordinal).ToList();
                    _progress.Report(new(ProgressKind.Warning,
                        $"Not creating {string.Join(", ", skipped)} in the target: an empty migration history " +
                        "makes an ORM re-apply its baseline over the schema the mirror just built. " +
                        "This mirrored target has no ORM migration history and the ORM will not recognise its schema.",
                        MessageCode.WarnMirrorNoHistory,
                        new object?[] { string.Join(", ", skipped) }));
                }
            }

            if (missing.Count > 0)
            {
                if (!await SchemaMirror.CreateMissingTablesAsync(sql, pg, sourceSchema, missing, targetSchema.Keys, _progress, ct))
                    return Fail(stopwatch, "Schema mirroring failed — details above.", MessageCode.FailMirrorFailed);
                targetSchema = await SchemaReader.ReadPostgresAsync(pg, ct);
            }
        }

        var foreignKeys = await SchemaReader.ReadForeignKeysAsync(pg, ct);

        // Two sets, because they answer different questions: everything the plan must leave
        // alone, and — of those — the target tables whose rows the run has promised to keep.
        var preservedInTarget = new HashSet<string>(StringComparer.Ordinal);
        var preserved = new HashSet<string>(StringComparer.Ordinal);
        if (options.MigrateHistoryTables)
        {
            foreach (var table in MigrationHistory.In(targetSchema.Keys).Intersect(sourceSchema.Keys, StringComparer.Ordinal))
                _progress.Report(new(ProgressKind.Warning,
                    $"'{table}': the target's own migration history is being replaced by the source's. " +
                    "An ORM reading it afterwards will not recognise this database.",
                    MessageCode.WarnHistoryCopied, new object?[] { table }));
        }
        else
        {
            preservedInTarget = new HashSet<string>(
                MigrationHistory.In(targetSchema.Keys), StringComparer.Ordinal);
            preserved = await ReportHistoryAsync(pg, sourceSchema.Keys, targetSchema.Keys, ct);
        }

        var plan = BuildPlan(sourceSchema, targetSchema, options.AllowSourceOnlyTables, preserved);
        var schemasOk = await ReportOtherSchemasAsync(sql, options.AllowSourceOnlyTables, ct);
        if (plan is null || !schemasOk)
            return Fail(stopwatch, "Schema mismatch — details above.", MessageCode.FailSchemaMismatch);

        var copyTables = plan.Where(p => p.CopyColumns.Count > 0).Select(p => p.Table).ToHashSet(StringComparer.Ordinal);
        if (copyTables.Count == 0)
        {
            _progress.Report(new(ProgressKind.Error,
                "No tables to copy — the source/target may be wrong, or the target has no schema.",
                MessageCode.ErrorNoTablesToCopy));
            return Fail(stopwatch, "Empty intersection.", MessageCode.FailEmptyIntersection);
        }
        _progress.Report(new(ProgressKind.Info, $"{copyTables.Count} tables to migrate.",
            MessageCode.InfoTablesToMigrate, new object?[] { copyTables.Count }));

        if (options.VerifyOnly)
        {
            var okVerify = await VerifyRowCountsAsync(sql, pg, plan, null, ct)
                         & await ValidateForeignKeysAsync(pg, foreignKeys, copyTables, null, ct);
            return okVerify
                ? Succeed(stopwatch, 0, "Verification passed.", MessageCode.SuccessVerifyPassed)
                : Fail(stopwatch, "Verification failed.", MessageCode.FailVerifyFailed);
        }

        if (!ReportCascadePreview(foreignKeys, copyTables, preservedInTarget))
            return Fail(stopwatch, "A preserved migration history is inside the TRUNCATE CASCADE closure.",
                MessageCode.FailHistoryCascade);

        if (!await PreflightAsync(sql, plan, options.AllowSchemaRisk, ct))
            return Fail(stopwatch, "Preflight mismatches were not resolved.", MessageCode.FailPreflightUnresolved);

        await using var transaction = await pg.BeginTransactionAsync(ct);
        var rows = await CopyAsync(sql, pg, plan, transaction, ct);
        _progress.Report(new(ProgressKind.Info, $"Copy finished — {rows} rows.",
            MessageCode.InfoCopyFinished, new object?[] { rows }));

        if (cascaded.Count > 0)
        {
            var sample = string.Join(", ", cascaded.Take(8));
            if (cascaded.Count > 8)
                _progress.Report(new(ProgressKind.Warning,
                    $"TRUNCATE CASCADE also emptied {cascaded.Count} dependent tables: {sample} (+{cascaded.Count - 8} more). " +
                    "Tables with no source counterpart stay empty.",
                    MessageCode.WarnTruncateCascadeMore, new object?[] { cascaded.Count, sample, cascaded.Count - 8 }));
            else
                _progress.Report(new(ProgressKind.Warning,
                    $"TRUNCATE CASCADE also emptied {cascaded.Count} dependent tables: {sample}. " +
                    "Tables with no source counterpart stay empty.",
                    MessageCode.WarnTruncateCascade, new object?[] { cascaded.Count, sample }));
        }

        if (rows == 0)
        {
            _progress.Report(new(ProgressKind.Error,
                "No rows were copied; the source is empty or wrong. Nothing was committed.",
                MessageCode.ErrorZeroRows));
            await transaction.RollbackAsync(ct);
            return Fail(stopwatch, "Zero rows.", MessageCode.FailZeroRows);
        }

        var ok = await VerifyRowCountsAsync(sql, pg, plan, transaction, ct)
               & await ValidateForeignKeysAsync(pg, foreignKeys, copyTables, transaction, ct);

        if (!ok)
        {
            _progress.Report(new(ProgressKind.Error,
                "Verification failed — rolling back, the target was not written.",
                MessageCode.ErrorVerifyFailedRollback));
            await transaction.RollbackAsync(ct);
            return Fail(stopwatch, "Verification failed.", MessageCode.FailVerifyFailed);
        }

        await transaction.CommitAsync(ct);
        await ExecAsync(pg, "ANALYZE;", null, ct);
        return Succeed(stopwatch, rows, $"{rows} rows migrated and verified.",
            MessageCode.SuccessMigrated, new object?[] { rows });
    }

    private MigrationResult Succeed(Stopwatch sw, long rows, string summary, string code, object?[]? args = null)
    {
        _progress.Report(new(ProgressKind.Success, summary, code, args));
        return new MigrationResult(true, rows, sw.Elapsed, summary, code, args);
    }

    private MigrationResult Fail(Stopwatch sw, string summary, string code) => new(false, 0, sw.Elapsed, summary, code);

    // ── Plan ──────────────────────────────────────────────────────────────────

    private List<TablePlan>? BuildPlan(
        Dictionary<string, List<ColumnInfo>> sourceSchema,
        Dictionary<string, List<ColumnInfo>> targetSchema,
        bool allowSourceOnly,
        IReadOnlySet<string> preserved)
    {
        var plan = new List<TablePlan>();
        var fatal = false;

        foreach (var (table, targetColumns) in targetSchema.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            // Not in the plan means not in the TRUNCATE set and not written to. The rows the
            // target already holds are the ones that belong there.
            if (preserved.Contains(table)) continue;
            if (!sourceSchema.TryGetValue(table, out var sourceColumns)) continue;

            var sourceByName = sourceColumns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var copy = new List<(ColumnInfo, ColumnInfo)>();
            var synthesized = new List<ColumnInfo>();

            foreach (var column in targetColumns)
            {
                if (sourceByName.TryGetValue(column.Name, out var sourceColumn))
                {
                    copy.Add((sourceColumn, column));
                    continue;
                }
                if (column.IsNullable || column.HasDefault) continue;
                if (CanSynthesize(column.StoreType))
                {
                    // The value is made up. Once it is in the table a fabricated 0 reads
                    // exactly like a measured one, so the only moment it can be pointed at
                    // is this one.
                    var value = Describe(SynthesizeDefault(column.StoreType));
                    _progress.Report(new(ProgressKind.Warning,
                        $"{table}.{column.Name}: missing from the source and NOT NULL — every row was given {value} ({column.StoreType}).",
                        MessageCode.WarnColumnSynthesized,
                        new object?[] { table, column.Name, column.StoreType, value }));
                    synthesized.Add(column);
                    continue;
                }

                _progress.Report(new(ProgressKind.Error,
                    $"{table}.{column.Name}: missing from the source, NOT NULL, and no safe default can be synthesized ({column.StoreType}).",
                    MessageCode.ErrorColumnNotSynthesizable, new object?[] { table, column.Name, column.StoreType }));
                fatal = true;
            }

            // SAFETY.md promises this one by name: "the missing column is reported, not
            // treated as failure". Only the target's columns were ever walked above, so
            // until now nothing looked at the source's, and a dropped column's data left
            // without a word.
            var targetByName = targetColumns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var column in sourceColumns.Where(c => !targetByName.Contains(c.Name)))
                _progress.Report(new(ProgressKind.Warning,
                    $"{table}.{column.Name}: no counterpart in the target ({column.StoreType}) — its data will not be migrated.",
                    MessageCode.WarnSourceColumnDropped,
                    new object?[] { table, column.Name, column.StoreType }));

            plan.Add(new TablePlan(table, copy, synthesized));
        }

        // ORM migration-history tables are excluded above unless the operator asked for
        // them: their rows describe the target, not the data. Re-running the ORM afterwards
        // does not repair a copied history — the target then holds the source provider's
        // migration IDs and not its own baseline's, so the ORM re-applies the baseline and
        // fails on tables that already exist. See MigrationHistory.
        var sourceOnly = sourceSchema.Keys
            .Except(targetSchema.Keys, StringComparer.Ordinal)
            .Except(preserved, StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        foreach (var table in sourceOnly)
        {
            var text = $"Source table '{table}' does not exist in the target — its data will not be migrated.";
            if (allowSourceOnly)
                _progress.Report(new(ProgressKind.Warning, text,
                    MessageCode.WarnSourceOnlyTable, new object?[] { table }));
            else
            {
                _progress.Report(new(ProgressKind.Error,
                    text + " If this is intentional, enable the 'allow source-only tables' option;" +
                    " to create it in the target, enable the mirror option.",
                    MessageCode.ErrorSourceOnlyTable, new object?[] { table }));
                fatal = true;
            }
        }

        return fatal ? null : plan;
    }

    private static bool CanSynthesize(string udt) => udt switch
    {
        "bool" or "int2" or "int4" or "int8" or "numeric" or "float4" or "float8" or "text" or "varchar" => true,
        _ => false,
    };

    /// <summary>Renders a synthesized value the way it will look in the table.</summary>
    private static string Describe(object value) => value switch
    {
        string text => $"'{text}'",
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    private static object SynthesizeDefault(string udt) => udt switch
    {
        "bool" => false, "int2" => (short)0, "int4" => 0, "int8" => 0L,
        "numeric" => 0m, "float4" => 0f, "float8" => 0d, "text" or "varchar" => "",
        _ => throw new InvalidOperationException($"Cannot synthesize a default: {udt}"),
    };

    /// <summary>
    /// Decides which tables are the target's own to keep, and says so with numbers.
    ///
    /// <para>Returns the target tables that will be left out of the plan entirely. Silence
    /// is what made the original failure expensive to diagnose, so every history table found
    /// on either side produces a line — including the ones there is nothing to do about.</para>
    /// </summary>
    private async Task<HashSet<string>> ReportHistoryAsync(
        NpgsqlConnection pg,
        IEnumerable<string> sourceTables,
        IEnumerable<string> targetTables,
        CancellationToken ct)
    {
        var preserved = new HashSet<string>(MigrationHistory.In(targetTables), StringComparer.Ordinal);
        foreach (var table in preserved.OrderBy(t => t, StringComparer.Ordinal))
        {
            long rows;
            await using (var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {TargetDatabase.Quote(table)}", pg) { CommandTimeout = 0 })
                rows = (long)(await command.ExecuteScalarAsync(ct) ?? 0L);

            _progress.Report(new(ProgressKind.Info,
                $"'{table}' is the target's own migration history ({rows} rows) — left untouched, not copied.",
                MessageCode.InfoHistoryPreserved, new object?[] { table, rows }));
        }

        // A source history table with no target counterpart is deliberately not migrated, so
        // it must also leave the source-only gate alone. That gate asks the operator to
        // approve data being left behind; this data is not being left behind by accident,
        // and making it stop the run would be asking permission for a decision the tool has
        // already made on principle.
        var excluded = new HashSet<string>(preserved, StringComparer.Ordinal);
        foreach (var table in MigrationHistory.In(sourceTables).Except(preserved, StringComparer.Ordinal))
        {
            excluded.Add(table);
            _progress.Report(new(ProgressKind.Info,
                $"'{table}' exists in the source and not in the target — an ORM's history belongs to the database it describes, so it is not migrated.",
                MessageCode.InfoHistorySourceOnly, new object?[] { table }));
        }

        return excluded;
    }

    /// <summary>
    /// Names the source schemas this tool does not read.
    ///
    /// <para>Judged on the same gate as a source-only table, because that is what these
    /// are: source data with no target counterpart, differing only in that a whole schema
    /// goes at once. Reusing <see cref="MigrationOptions.AllowSourceOnlyTables"/> keeps one
    /// switch for one meaning — "I know data is being left behind" — rather than adding a
    /// second one an operator could have on while the first is off.</para>
    ///
    /// <para>The mirror is deliberately not offered as a remedy here. It creates missing
    /// tables in <c>public</c>, and a <c>reporting.Invoice</c> arriving beside a
    /// <c>dbo.Invoice</c> would collide by name — silently, since only the base name
    /// travels.</para>
    /// </summary>
    private async Task<bool> ReportOtherSchemasAsync(
        SqlConnection sql, bool allowSourceOnly, CancellationToken ct)
    {
        var schemas = await SchemaReader.ReadSqlServerOtherSchemasAsync(sql, ct);
        if (schemas.Count == 0) return true;

        var total = schemas.Sum(s => s.Tables);
        var listed = string.Join(", ", schemas.Select(s => $"{s.Schema} ({s.Tables})"));
        var text = $"The source has {total} base tables outside dbo, in: {listed}. " +
                   "This tool reads dbo only, so none of them will be migrated.";

        if (allowSourceOnly)
        {
            _progress.Report(new(ProgressKind.Warning, text,
                MessageCode.WarnSourceSchemaSkipped, new object?[] { total, listed }));
            return true;
        }

        _progress.Report(new(ProgressKind.Error,
            text + " If this is intentional, enable the 'allow source-only tables' option.",
            MessageCode.ErrorSourceSchemaSkipped, new object?[] { total, listed }));
        return false;
    }

    /// <summary>
    /// Works out which target tables TRUNCATE CASCADE will empty, before the transaction
    /// opens and the exclusive locks are taken.
    ///
    /// <para>CASCADE reaches every table that references one being truncated, transitively.
    /// The ones inside the copy set get refilled; the ones outside it do not, and those are
    /// the loss worth naming. PostgreSQL reports them afterwards through notices, which is
    /// too late to be a decision.</para>
    /// </summary>
    private bool ReportCascadePreview(
        List<ForeignKey> foreignKeys, HashSet<string> copyTables, IReadOnlySet<string> preserved)
    {
        // Each entry records the table whose truncation reaches this one, so a path can be
        // spelled out afterwards. Seeds map to null.
        var reachedBy = copyTables.ToDictionary(t => t, _ => (string?)null, StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var fk in foreignKeys)
                if (reachedBy.ContainsKey(fk.ParentTable) && !reachedBy.ContainsKey(fk.ChildTable))
                {
                    reachedBy[fk.ChildTable] = fk.ParentTable;
                    grew = true;
                }
        }

        // Leaving a table out of the plan keeps it out of the TRUNCATE list; it does not keep
        // CASCADE away from it. A preserved history table with a foreign key into a copied
        // one would be emptied anyway — and the run would have promised otherwise. Today no
        // ORM gives that table a foreign key, which makes this safe in practice and unproven
        // in principle, and unproven is not a thing to build a guarantee on.
        var doomed = preserved.Where(reachedBy.ContainsKey)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        if (doomed.Count > 0)
        {
            foreach (var table in doomed)
                _progress.Report(new(ProgressKind.Error,
                    $"'{table}' was to be preserved, but TRUNCATE CASCADE reaches it: {PathTo(reachedBy, table)}. " +
                    "Its rows would be emptied and not refilled, so the migration cannot keep the promise it made.",
                    MessageCode.ErrorHistoryCascade,
                    new object?[] { table, PathTo(reachedBy, table) }));
            return false;
        }

        var collateral = reachedBy.Keys.Except(copyTables, StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        if (collateral.Count == 0) return true;

        var sample = string.Join(", ", collateral.Take(8));
        var more = collateral.Count > 8 ? $" (+{collateral.Count - 8} more)" : "";
        _progress.Report(new(ProgressKind.Warning,
            $"TRUNCATE CASCADE will also empty {collateral.Count} target tables that are not being copied: " +
            $"{sample}{more}. They have no source counterpart, so they will stay empty.",
            MessageCode.WarnCascadePreview,
            new object?[] { collateral.Count, sample + more }));
        return true;
    }

    /// <summary>Walks the trail back to the copied table the cascade starts from.</summary>
    private static string PathTo(Dictionary<string, string?> reachedBy, string table)
    {
        var path = new List<string> { table };
        for (var at = reachedBy[table]; at is not null; at = reachedBy[at])
        {
            path.Add(at);
            if (path.Count > 32) break; // a cycle would otherwise walk forever
        }
        path.Reverse();
        return string.Join(" -> ", path);
    }

    // ── Preflight ─────────────────────────────────────────────────────────────

    private async Task<bool> PreflightAsync(SqlConnection sql, List<TablePlan> plan, bool allowRisk, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Preflight", MessageCode.StepPreflight));
        var violations = new List<ProgressMessage>();

        foreach (var table in plan)
        {
            // ONE pass per table: a query per column would mean a separate full scan on large tables.
            var nullChecks = table.CopyColumns.Where(c => !c.Target.IsNullable && c.Source.IsNullable).ToList();
            var lengthChecks = table.CopyColumns
                .Where(c => c.Target.StoreType is "varchar" or "bpchar" && c.Target.MaxLength > 0 && c.Source.MaxLength is int)
                .ToList();
            if (nullChecks.Count == 0 && lengthChecks.Count == 0) continue;

            var projections = nullChecks
                .Select(c => $"SUM(CASE WHEN [{c.Source.Name}] IS NULL THEN CAST(1 AS bigint) ELSE CAST(0 AS bigint) END)")
                .Concat(lengthChecks.Select(c => $"MAX(LEN([{c.Source.Name}]))"));

            await using var command = new SqlCommand(
                $"SELECT {string.Join(", ", projections)} FROM [dbo].[{table.Table}]", sql) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) continue;

            for (var i = 0; i < nullChecks.Count; i++)
            {
                var nulls = reader.IsDBNull(i) ? 0L : Convert.ToInt64(reader.GetValue(i));
                if (nulls > 0)
                    violations.Add(new(ProgressKind.Error,
                        $"{table.Table}.{nullChecks[i].Target.Name}: target is NOT NULL but the source has {nulls} NULLs.",
                        MessageCode.ErrorPreflightNulls, new object?[] { table.Table, nullChecks[i].Target.Name, nulls }));
            }
            for (var i = 0; i < lengthChecks.Count; i++)
            {
                var ordinal = nullChecks.Count + i;
                var longest = reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal));
                var limit = lengthChecks[i].Target.MaxLength!.Value;
                if (longest > limit)
                    violations.Add(new(ProgressKind.Error,
                        $"{table.Table}.{lengthChecks[i].Target.Name}: target is varchar({limit}) but the longest source value is {longest} characters.",
                        MessageCode.ErrorPreflightLength, new object?[] { table.Table, lengthChecks[i].Target.Name, limit, longest }));
            }
        }

        if (violations.Count == 0)
        {
            _progress.Report(new(ProgressKind.Info, "Preflight clean: no NULL/length mismatches.",
                MessageCode.InfoPreflightClean));
            return true;
        }

        foreach (var violation in violations)
            _progress.Report(violation with { Kind = allowRisk ? ProgressKind.Warning : ProgressKind.Error });

        if (allowRisk)
            _progress.Report(new(ProgressKind.Warning,
                "Proceeding because the risk was accepted — the copy may still fail midway.",
                MessageCode.WarnPreflightAllowed));
        return allowRisk;
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    private async Task<long> CopyAsync(
        SqlConnection sql, NpgsqlConnection pg, List<TablePlan> plan, NpgsqlTransaction transaction, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Copying data", MessageCode.StepCopying));

        // Suspend FK triggers for the duration of the transaction (requires superuser) —
        // copy order stops mattering.
        await ExecAsync(pg, "SET LOCAL session_replication_role = replica;", transaction, ct);

        var toTruncate = plan.Where(p => p.CopyColumns.Count > 0).Select(p => TargetDatabase.Quote(p.Table)).ToList();
        if (toTruncate.Count > 0)
            await ExecAsync(pg, $"TRUNCATE {string.Join(", ", toTruncate)} RESTART IDENTITY CASCADE;", transaction, ct);

        var total = 0L;
        foreach (var table in plan)
        {
            if (table.CopyColumns.Count == 0) continue;

            var sourceList = string.Join(", ", table.CopyColumns.Select(c => $"[{c.Source.Name}]"));
            var targetList = string.Join(", ", table.CopyColumns.Select(c => c.Target)
                .Concat(table.SynthesizedColumns).Select(c => TargetDatabase.Quote(c.Name)));

            await using var command = new SqlCommand($"SELECT {sourceList} FROM [dbo].[{table.Table}]", sql) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(ct);

            var rows = 0L;
            await using (var importer = await pg.BeginBinaryImportAsync(
                $"COPY {TargetDatabase.Quote(table.Table)} ({targetList}) FROM STDIN (FORMAT BINARY)", ct))
            {
                importer.Timeout = TimeSpan.Zero; // zero = unlimited; the 30-second default cuts off large tables
                while (await reader.ReadAsync(ct))
                {
                    await importer.StartRowAsync(ct);
                    for (var i = 0; i < table.CopyColumns.Count; i++)
                    {
                        var column = table.CopyColumns[i].Target;
                        try { await WriteValueAsync(importer, reader.GetValue(i), column.StoreType, ct); }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"{table.Table}.{column.Name} (row {rows + 1}, type {column.StoreType}): {ex.Message}", ex);
                        }
                    }
                    foreach (var column in table.SynthesizedColumns)
                        await WriteValueAsync(importer, SynthesizeDefault(column.StoreType), column.StoreType, ct);
                    rows++;
                }
                await importer.CompleteAsync(ct);
            }

            total += rows;
            if (rows > 0)
                _progress.Report(new(ProgressKind.Info, $"  {table.Table}: {rows} rows",
                    MessageCode.InfoTableCopied, new object?[] { table.Table, rows }));
        }

        await FixIdentitySequencesAsync(pg, transaction, ct);
        return total;
    }

    private static async Task WriteValueAsync(NpgsqlBinaryImporter importer, object value, string udt, CancellationToken ct)
    {
        if (value is DBNull) { await importer.WriteNullAsync(ct); return; }

        switch (udt)
        {
            case "uuid": await importer.WriteAsync((Guid)value, NpgsqlDbType.Uuid, ct); break;
            case "bool": await importer.WriteAsync(Convert.ToBoolean(value), NpgsqlDbType.Boolean, ct); break;
            case "int2": await importer.WriteAsync(Convert.ToInt16(value), NpgsqlDbType.Smallint, ct); break;
            case "int4": await importer.WriteAsync(Convert.ToInt32(value), NpgsqlDbType.Integer, ct); break;
            case "int8": await importer.WriteAsync(Convert.ToInt64(value), NpgsqlDbType.Bigint, ct); break;
            case "numeric": await importer.WriteAsync(Convert.ToDecimal(value), NpgsqlDbType.Numeric, ct); break;
            case "float4": await importer.WriteAsync(Convert.ToSingle(value), NpgsqlDbType.Real, ct); break;
            case "float8": await importer.WriteAsync(Convert.ToDouble(value), NpgsqlDbType.Double, ct); break;
            case "text": await importer.WriteAsync((string)value, NpgsqlDbType.Text, ct); break;
            case "varchar": await importer.WriteAsync((string)value, NpgsqlDbType.Varchar, ct); break;
            case "bpchar": await importer.WriteAsync((string)value, NpgsqlDbType.Char, ct); break;
            case "bytea": await importer.WriteAsync((byte[])value, NpgsqlDbType.Bytea, ct); break;
            case "timestamp":
                // datetime2 comes back Kind=Unspecified; moved verbatim, NO UTC conversion.
                await importer.WriteAsync(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Unspecified), NpgsqlDbType.Timestamp, ct);
                break;
            case "timestamptz":
                var utc = value switch
                {
                    DateTimeOffset dto => dto.UtcDateTime,
                    DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
                    DateTime dt => dt,
                    _ => Convert.ToDateTime(value),
                };
                await importer.WriteAsync(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, ct);
                break;
            case "date": await importer.WriteAsync(DateOnly.FromDateTime((DateTime)value), NpgsqlDbType.Date, ct); break;
            case "interval": await importer.WriteAsync((TimeSpan)value, NpgsqlDbType.Interval, ct); break;
            case "time": await importer.WriteAsync(TimeOnly.FromTimeSpan((TimeSpan)value), NpgsqlDbType.Time, ct); break;
            default:
                throw new NotSupportedException($"Unsupported target type: {udt} (CLR type {value.GetType().Name})");
        }
    }

    private async Task FixIdentitySequencesAsync(NpgsqlConnection pg, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string query = """
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND is_identity = 'YES'
            """;
        var identities = new List<(string Table, string Column)>();
        await using (var command = new NpgsqlCommand(query, pg, transaction) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                identities.Add((reader.GetString(0), reader.GetString(1)));

        foreach (var (table, column) in identities)
        {
            var quotedTable = TargetDatabase.Quote(table);
            var quotedColumn = TargetDatabase.Quote(column);
            await ExecAsync(pg, $"""
                SELECT setval(pg_get_serial_sequence('{quotedTable}', '{column}'),
                              COALESCE((SELECT MAX({quotedColumn}) FROM {quotedTable}), 1),
                              (SELECT MAX({quotedColumn}) FROM {quotedTable}) IS NOT NULL);
                """, transaction, ct);
        }
        if (identities.Count > 0)
            _progress.Report(new(ProgressKind.Info, $"{identities.Count} identity sequences aligned.",
                MessageCode.InfoSequencesAligned, new object?[] { identities.Count }));
    }

    // ── Verification ──────────────────────────────────────────────────────────

    private async Task<bool> VerifyRowCountsAsync(
        SqlConnection sql, NpgsqlConnection pg, List<TablePlan> plan, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Verifying row counts", MessageCode.StepVerifyRowCounts));
        var ok = true;
        foreach (var table in plan)
        {
            if (table.CopyColumns.Count == 0) continue;
            var source = Convert.ToInt64(await ScalarSqlAsync(sql, $"SELECT COUNT_BIG(*) FROM [dbo].[{table.Table}]", ct));
            var target = Convert.ToInt64(await ScalarPgAsync(pg, $"SELECT COUNT(*) FROM {TargetDatabase.Quote(table.Table)}", transaction, ct));
            if (source != target)
            {
                _progress.Report(new(ProgressKind.Error,
                    $"{table.Table}: source has {source} rows, target has {target}.",
                    MessageCode.ErrorRowCountMismatch, new object?[] { table.Table, source, target }));
                ok = false;
            }
        }
        if (ok) _progress.Report(new(ProgressKind.Info, "All row counts match.",
            MessageCode.InfoRowCountsMatch));
        return ok;
    }

    /// <summary>
    /// Orphan-row audit. With FKs suspended throughout the copy, an inconsistency in the
    /// source can enter silently; this check is the only guarantee, and it runs BEFORE
    /// the commit.
    /// </summary>
    private async Task<bool> ValidateForeignKeysAsync(
        NpgsqlConnection pg, List<ForeignKey> foreignKeys, HashSet<string> copyTables,
        NpgsqlTransaction? transaction, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Verifying foreign key integrity", MessageCode.StepVerifyForeignKeys));
        var ok = true;
        var checkedCount = 0;

        foreach (var fk in foreignKeys)
        {
            if (!copyTables.Contains(fk.ChildTable)) continue;

            var notNull = string.Join(" AND ", fk.Columns.Select(c => $"c.{TargetDatabase.Quote(c.Child)} IS NOT NULL"));
            var join = string.Join(" AND ", fk.Columns.Select(c => $"p.{TargetDatabase.Quote(c.Parent)} = c.{TargetDatabase.Quote(c.Child)}"));
            var orphans = Convert.ToInt64(await ScalarPgAsync(pg,
                $"SELECT COUNT(*) FROM {TargetDatabase.Quote(fk.ChildTable)} c WHERE ({notNull}) " +
                $"AND NOT EXISTS (SELECT 1 FROM {TargetDatabase.Quote(fk.ParentTable)} p WHERE {join})", transaction, ct));

            checkedCount++;
            if (orphans > 0)
            {
                var columns = string.Join(", ", fk.Columns.Select(c => c.Child));
                _progress.Report(new(ProgressKind.Error,
                    $"Orphan rows: {fk.ChildTable} ({columns}) → {fk.ParentTable}: {orphans} rows ({fk.Name}).",
                    MessageCode.ErrorOrphanRows, new object?[] { fk.ChildTable, columns, fk.ParentTable, orphans, fk.Name }));
                ok = false;
            }
        }

        if (ok) _progress.Report(new(ProgressKind.Info, $"{checkedCount} foreign keys checked, no orphan rows.",
            MessageCode.InfoForeignKeysClean, new object?[] { checkedCount }));
        return ok;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task ExecAsync(NpgsqlConnection pg, string sql, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, pg, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarPgAsync(NpgsqlConnection pg, string sql, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, pg, transaction) { CommandTimeout = 0 };
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<object?> ScalarSqlAsync(SqlConnection sql, string commandText, CancellationToken ct)
    {
        await using var command = new SqlCommand(commandText, sql) { CommandTimeout = 0 };
        return await command.ExecuteScalarAsync(ct);
    }
}
