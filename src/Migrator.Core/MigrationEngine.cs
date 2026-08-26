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
/// the target — this tool only moves data, it creates no tables.
/// </summary>
public sealed class MigrationEngine
{
    private static readonly string[] SkipTables = { "__EFMigrationsHistory" };

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
        var foreignKeys = await SchemaReader.ReadForeignKeysAsync(pg, ct);

        var plan = BuildPlan(sourceSchema, targetSchema, options.AllowSourceOnlyTables);
        if (plan is null)
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
        bool allowSourceOnly)
    {
        var plan = new List<TablePlan>();
        var fatal = false;

        foreach (var (table, targetColumns) in targetSchema.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (SkipTables.Contains(table)) continue;
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
                if (CanSynthesize(column.StoreType)) { synthesized.Add(column); continue; }

                _progress.Report(new(ProgressKind.Error,
                    $"{table}.{column.Name}: missing from the source, NOT NULL, and no safe default can be synthesized ({column.StoreType}).",
                    MessageCode.ErrorColumnNotSynthesizable, new object?[] { table, column.Name, column.StoreType }));
                fatal = true;
            }

            plan.Add(new TablePlan(table, copy, synthesized));
        }

        var sourceOnly = sourceSchema.Keys
            .Except(targetSchema.Keys, StringComparer.Ordinal)
            .Where(t => !SkipTables.Contains(t))
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
                    text + " If this is intentional, enable the 'allow source-only tables' option.",
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

    private static object SynthesizeDefault(string udt) => udt switch
    {
        "bool" => false, "int2" => (short)0, "int4" => 0, "int8" => 0L,
        "numeric" => 0m, "float4" => 0f, "float8" => 0d, "text" or "varchar" => "",
        _ => throw new InvalidOperationException($"Cannot synthesize a default: {udt}"),
    };

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
