namespace Migrator.Core;

using Npgsql;

/// <summary>Prepares the target database and verifies its collation.</summary>
public static class TargetDatabase
{
    /// <summary>
    /// Creates the target database with the requested ICU collation if it does not exist.
    /// An existing one is left untouched: PostgreSQL does not allow changing a database's
    /// collation once it is created.
    /// </summary>
    public static async Task<bool> EnsureCreatedAsync(
        string connectionString, string? icuLocale, IProgress<ProgressMessage> progress, CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = target.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            progress.Report(new(ProgressKind.Error, "The target connection string has no database name.",
                MessageCode.ErrorTargetDbNameMissing));
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
                progress.Report(new(ProgressKind.Info,
                    $"Target database '{databaseName}' already exists — left untouched.",
                    MessageCode.InfoTargetDbExists, new object?[] { databaseName }));
                return true;
            }
        }

        var locale = string.IsNullOrWhiteSpace(icuLocale)
            ? ""
            : $" LOCALE_PROVIDER icu ICU_LOCALE '{icuLocale.Replace("'", "''")}'";
        var sql = $"CREATE DATABASE {Quote(databaseName)} ENCODING 'UTF8'{locale} TEMPLATE template0";

        await using (var create = new NpgsqlCommand(sql, connection))
            await create.ExecuteNonQueryAsync(ct);

        if (locale == "")
            progress.Report(new(ProgressKind.Success, $"Target database '{databaseName}' created.",
                MessageCode.SuccessTargetDbCreated, new object?[] { databaseName }));
        else
            progress.Report(new(ProgressKind.Success,
                $"Target database '{databaseName}' created (collation: {icuLocale}).",
                MessageCode.SuccessTargetDbCreatedCollation, new object?[] { databaseName, icuLocale }));
        return true;
    }

    /// <summary>
    /// Verifies the collation. A wrong collation is silent: the database runs without
    /// errors, only search and sort quietly misbehave — and fixing it after data has
    /// arrived means recreating the database.
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
            progress.Report(new(ProgressKind.Info, $"Collation verified: {actual}",
                MessageCode.InfoCollationVerified, new object?[] { actual }));
            return true;
        }

        // Args carry the token itself so the translation layer can see and translate it;
        // the English text shows its readable rendering.
        var message = $"Target collation is '{DescribeLocale(actual)}' — expected ICU '{expectedIcuLocale}'.";
        var args = new object?[] { actual, expectedIcuLocale };
        if (allowMismatch)
        {
            progress.Report(new(ProgressKind.Warning,
                message + " Proceeding because the mismatch was allowed.",
                MessageCode.WarnCollationMismatchAllowed, args));
            return true;
        }
        progress.Report(new(ProgressKind.Error, message +
            " A wrong collation is silent: search and sort quietly misbehave.",
            MessageCode.ErrorCollationMismatch, args));
        return false;
    }

    private static async Task<string> ReadLocaleAsync(NpgsqlConnection pg, CancellationToken ct)
    {
        // PG 17 renamed daticulocale to datlocale.
        foreach (var column in new[] { "daticulocale", "datlocale" })
        {
            try
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT coalesce({column}, datcollate) FROM pg_database WHERE datname = current_database()", pg);
                return await command.ExecuteScalarAsync(ct) as string ?? MessageCode.TokenUnknown;
            }
            catch (PostgresException ex) when (ex.SqlState == "42703")
            {
                // try the next column name
            }
        }
        return MessageCode.TokenUnreadable;
    }

    private static string DescribeLocale(string locale) => locale switch
    {
        MessageCode.TokenUnknown => "(unknown)",
        MessageCode.TokenUnreadable => "(unreadable)",
        _ => locale,
    };

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
