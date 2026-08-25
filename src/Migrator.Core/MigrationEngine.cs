namespace Migrator.Core;

using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// SQL Server -> PostgreSQL veri taşıma motoru.
///
/// Tasarım kararı: taşıma VE doğrulama tek transaction içinde koşar; doğrulama geçmeden
/// commit yapılmaz. Böylece "başarılı" çıkışı verinin gerçekten taşındığı anlamına gelir ve
/// başarısızlık hedefi yarı-dolu bırakmaz. Şema hedefte HAZIR olmalıdır — bu araç yalnız
/// veri taşır, tablo yaratmaz.
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
        // TRUNCATE CASCADE, bağlı her tablo için ayrı bir notice basar; büyük bir şemada bu
        // yüzlerce satır demek ve gerçek ilerlemeyi görünmez kılar. Sayılıp tek satırda özetlenir,
        // ama tamamen yutulmaz: hangi tabloların boşaldığı operatörün bilmesi gereken bir şey.
        var cascaded = new List<string>();
        pg.Notice += (_, e) =>
        {
            var text = e.Notice.MessageText;
            const string marker = "truncate cascades to table ";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) cascaded.Add(text[(index + marker.Length)..].Trim('"', ' ', '.'));
            else _progress.Report(new(ProgressKind.Info, $"(pg) {text}"));
        };

        if (!await TargetDatabase.CheckCollationAsync(pg, options.ExpectedIcuLocale, options.AllowCollationMismatch, _progress, ct))
            return Fail(stopwatch, "Collation uyuşmazlığı.");

        _progress.Report(new(ProgressKind.Step, "Şemalar okunuyor"));
        var sourceSchema = await SchemaReader.ReadSqlServerAsync(sql, ct);
        var targetSchema = await SchemaReader.ReadPostgresAsync(pg, ct);
        var foreignKeys = await SchemaReader.ReadForeignKeysAsync(pg, ct);

        var plan = BuildPlan(sourceSchema, targetSchema, options.AllowSourceOnlyTables);
        if (plan is null)
            return Fail(stopwatch, "Şema uyuşmazlığı — ayrıntılar yukarıda.");

        var copyTables = plan.Where(p => p.CopyColumns.Count > 0).Select(p => p.Table).ToHashSet(StringComparer.Ordinal);
        if (copyTables.Count == 0)
        {
            _progress.Report(new(ProgressKind.Error,
                "Kopyalanacak tablo bulunamadı — kaynak/hedef yanlış olabilir ya da hedefte şema yok."));
            return Fail(stopwatch, "Boş kesişim.");
        }
        _progress.Report(new(ProgressKind.Info, $"{copyTables.Count} tablo taşınacak."));

        if (options.VerifyOnly)
        {
            var okVerify = await VerifyRowCountsAsync(sql, pg, plan, null, ct)
                         & await ValidateForeignKeysAsync(pg, foreignKeys, copyTables, null, ct);
            return okVerify
                ? Succeed(stopwatch, 0, "Doğrulama başarılı.")
                : Fail(stopwatch, "Doğrulama başarısız.");
        }

        if (!await PreflightAsync(sql, plan, options.AllowSchemaRisk, ct))
            return Fail(stopwatch, "Ön kontrol uyumsuzlukları giderilmedi.");

        await using var transaction = await pg.BeginTransactionAsync(ct);
        var rows = await CopyAsync(sql, pg, plan, transaction, ct);
        _progress.Report(new(ProgressKind.Info, $"Kopyalama bitti — {rows} satır."));

        if (cascaded.Count > 0)
        {
            var sample = string.Join(", ", cascaded.Take(8));
            var rest = cascaded.Count > 8 ? $" (+{cascaded.Count - 8} tablo daha)" : "";
            _progress.Report(new(ProgressKind.Warning,
                $"TRUNCATE CASCADE {cascaded.Count} bağlı tabloyu da boşalttı: {sample}{rest}. " +
                "Kaynakta karşılığı olmayanlar boş kalır."));
        }

        if (rows == 0)
        {
            _progress.Report(new(ProgressKind.Error, "Hiç satır kopyalanmadı; kaynak boş ya da yanlış. Commit edilmedi."));
            await transaction.RollbackAsync(ct);
            return Fail(stopwatch, "Sıfır satır.");
        }

        var ok = await VerifyRowCountsAsync(sql, pg, plan, transaction, ct)
               & await ValidateForeignKeysAsync(pg, foreignKeys, copyTables, transaction, ct);

        if (!ok)
        {
            _progress.Report(new(ProgressKind.Error, "Doğrulama başarısız — geri alınıyor, hedefe yazılmadı."));
            await transaction.RollbackAsync(ct);
            return Fail(stopwatch, "Doğrulama başarısız.");
        }

        await transaction.CommitAsync(ct);
        await ExecAsync(pg, "ANALYZE;", null, ct);
        return Succeed(stopwatch, rows, $"{rows} satır taşındı ve doğrulandı.");
    }

    private MigrationResult Succeed(Stopwatch sw, long rows, string summary)
    {
        _progress.Report(new(ProgressKind.Success, summary));
        return new MigrationResult(true, rows, sw.Elapsed, summary);
    }

    private MigrationResult Fail(Stopwatch sw, string summary) => new(false, 0, sw.Elapsed, summary);

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
                    $"{table}.{column.Name}: kaynakta yok, NOT NULL ve güvenli varsayılan üretilemiyor ({column.StoreType})."));
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
            var text = $"Kaynak tablosu '{table}' hedefte yok — verisi taşınmayacak.";
            if (allowSourceOnly) _progress.Report(new(ProgressKind.Warning, text));
            else { _progress.Report(new(ProgressKind.Error, text + " Bilinçliyse 'hedefte olmayan tablolara izin ver' seçeneğini işaretleyin.")); fatal = true; }
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
        _ => throw new InvalidOperationException($"Varsayılan üretilemez: {udt}"),
    };

    // ── Ön kontrol ────────────────────────────────────────────────────────────

    private async Task<bool> PreflightAsync(SqlConnection sql, List<TablePlan> plan, bool allowRisk, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Ön kontrol"));
        var violations = new List<string>();

        foreach (var table in plan)
        {
            // Tablo başına TEK geçiş: kolon başına ayrı sorgu, büyük tablolarda ayrı tam tarama demek.
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
                    violations.Add($"{table.Table}.{nullChecks[i].Target.Name}: hedef NOT NULL ama kaynakta {nulls} NULL var.");
            }
            for (var i = 0; i < lengthChecks.Count; i++)
            {
                var ordinal = nullChecks.Count + i;
                var longest = reader.IsDBNull(ordinal) ? 0L : Convert.ToInt64(reader.GetValue(ordinal));
                var limit = lengthChecks[i].Target.MaxLength!.Value;
                if (longest > limit)
                    violations.Add($"{table.Table}.{lengthChecks[i].Target.Name}: hedef varchar({limit}) ama kaynakta en uzun değer {longest} karakter.");
            }
        }

        if (violations.Count == 0)
        {
            _progress.Report(new(ProgressKind.Info, "Ön kontrol temiz: NULL/uzunluk uyumsuzluğu yok."));
            return true;
        }

        foreach (var violation in violations)
            _progress.Report(new(allowRisk ? ProgressKind.Warning : ProgressKind.Error, violation));

        if (allowRisk)
            _progress.Report(new(ProgressKind.Warning, "İzin verildiği için devam ediliyor — kopyalama sırasında patlayabilir."));
        return allowRisk;
    }

    // ── Kopyalama ─────────────────────────────────────────────────────────────

    private async Task<long> CopyAsync(
        SqlConnection sql, NpgsqlConnection pg, List<TablePlan> plan, NpgsqlTransaction transaction, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Veri taşınıyor"));

        // FK tetikleyicilerini transaction boyunca askıya al (superuser ister) — kopyalama sırası önemsizleşir.
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
                importer.Timeout = TimeSpan.Zero; // sıfır = sınırsız; 30 sn'lik varsayılan büyük tabloları keser
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
                                $"{table.Table}.{column.Name} (satır {rows + 1}, tip {column.StoreType}): {ex.Message}", ex);
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
                _progress.Report(new(ProgressKind.Info, $"  {table.Table}: {rows} satır"));
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
                // datetime2 Kind=Unspecified döner; birebir taşınır, UTC dönüşümü YOK.
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
                throw new NotSupportedException($"Desteklenmeyen hedef tipi: {udt} (CLR tipi {value.GetType().Name})");
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
            _progress.Report(new(ProgressKind.Info, $"{identities.Count} identity sequence hizalandı."));
    }

    // ── Doğrulama ─────────────────────────────────────────────────────────────

    private async Task<bool> VerifyRowCountsAsync(
        SqlConnection sql, NpgsqlConnection pg, List<TablePlan> plan, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Satır sayıları doğrulanıyor"));
        var ok = true;
        foreach (var table in plan)
        {
            if (table.CopyColumns.Count == 0) continue;
            var source = Convert.ToInt64(await ScalarSqlAsync(sql, $"SELECT COUNT_BIG(*) FROM [dbo].[{table.Table}]", ct));
            var target = Convert.ToInt64(await ScalarPgAsync(pg, $"SELECT COUNT(*) FROM {TargetDatabase.Quote(table.Table)}", transaction, ct));
            if (source != target)
            {
                _progress.Report(new(ProgressKind.Error, $"{table.Table}: kaynak {source}, hedef {target} satır."));
                ok = false;
            }
        }
        if (ok) _progress.Report(new(ProgressKind.Info, "Tüm satır sayıları eşit."));
        return ok;
    }

    /// <summary>
    /// Yetim satır denetimi. Kopyalama boyunca FK'lar askıda olduğu için kaynaktaki tutarsızlık
    /// sessizce girebilir; tek güvence bu kontroldür ve commit'ten ÖNCE koşar.
    /// </summary>
    private async Task<bool> ValidateForeignKeysAsync(
        NpgsqlConnection pg, List<ForeignKey> foreignKeys, HashSet<string> copyTables,
        NpgsqlTransaction? transaction, CancellationToken ct)
    {
        _progress.Report(new(ProgressKind.Step, "Yabancı anahtar bütünlüğü doğrulanıyor"));
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
                _progress.Report(new(ProgressKind.Error,
                    $"Yetim satır: {fk.ChildTable} ({string.Join(", ", fk.Columns.Select(c => c.Child))}) → {fk.ParentTable}: {orphans} satır ({fk.Name})."));
                ok = false;
            }
        }

        if (ok) _progress.Report(new(ProgressKind.Info, $"{checkedCount} yabancı anahtar denetlendi, yetim satır yok."));
        return ok;
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

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
