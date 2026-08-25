namespace Migrator.Core;

using Npgsql;

/// <summary>Hedef veritabanını hazırlar ve collation'ını doğrular.</summary>
public static class TargetDatabase
{
    /// <summary>
    /// Hedef veritabanını, yoksa, istenen ICU collation ile oluşturur. Var olana dokunmaz:
    /// PostgreSQL kurulmuş bir veritabanının collation'ını değiştirmeye izin vermez.
    /// </summary>
    public static async Task<bool> EnsureCreatedAsync(
        string connectionString, string? icuLocale, IProgress<ProgressMessage> progress, CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = target.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            progress.Report(new(ProgressKind.Error, "Hedef bağlantı dizgisinde veritabanı adı yok."));
            return false;
        }

        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync(ct);

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", connection))
        {
            exists.Parameters.AddWithValue("n", databaseName);
            if (await exists.ExecuteScalarAsync(ct) is not null)
            {
                progress.Report(new(ProgressKind.Info, $"Hedef veritabanı '{databaseName}' zaten var — dokunulmadı."));
                return true;
            }
        }

        var locale = string.IsNullOrWhiteSpace(icuLocale)
            ? ""
            : $" LOCALE_PROVIDER icu ICU_LOCALE '{icuLocale.Replace("'", "''")}'";
        var sql = $"CREATE DATABASE {Quote(databaseName)} ENCODING 'UTF8'{locale} TEMPLATE template0";

        await using (var create = new NpgsqlCommand(sql, connection))
            await create.ExecuteNonQueryAsync(ct);

        progress.Report(new(ProgressKind.Success,
            $"Hedef veritabanı '{databaseName}' oluşturuldu{(locale == "" ? "" : $" (collation: {icuLocale})")}."));
        return true;
    }

    /// <summary>
    /// Collation'ı doğrular. Yanlış collation sessizdir: veritabanı hatasız çalışır, yalnız
    /// arama ve sıralama fark edilmeden yanlış davranır — ve veri girdikten sonra düzeltmek
    /// veritabanını yeniden yaratmayı gerektirir.
    /// </summary>
    public static async Task<bool> CheckCollationAsync(
        NpgsqlConnection pg, string? expectedIcuLocale, bool allowMismatch,
        IProgress<ProgressMessage> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedIcuLocale))
            return true;

        var actual = await ReadLocaleAsync(pg, ct);
        if (string.Equals(actual, expectedIcuLocale, StringComparison.Ordinal))
        {
            progress.Report(new(ProgressKind.Info, $"Collation doğrulandı: {actual}"));
            return true;
        }

        var message = $"Hedef collation '{actual}' — beklenen ICU '{expectedIcuLocale}'.";
        if (allowMismatch)
        {
            progress.Report(new(ProgressKind.Warning, message + " İzin verildiği için devam ediliyor."));
            return true;
        }
        progress.Report(new(ProgressKind.Error, message +
            " Yanlış collation sessizdir: arama ve sıralama fark edilmeden yanlış davranır."));
        return false;
    }

    private static async Task<string> ReadLocaleAsync(NpgsqlConnection pg, CancellationToken ct)
    {
        // PG 17 daticulocale'i datlocale olarak yeniden adlandırdı.
        foreach (var column in new[] { "daticulocale", "datlocale" })
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT coalesce({column}, datcollate) FROM pg_database WHERE datname = current_database()", pg);
                return await command.ExecuteScalarAsync(ct) as string ?? "(bilinmiyor)";
            }
            catch (PostgresException ex) when (ex.SqlState == "42703")
            {
                // sonraki kolon adını dene
            }
        }
        return "(okunamadı)";
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
