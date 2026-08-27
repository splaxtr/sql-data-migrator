namespace Migrator.Core;

using System.Security.Cryptography;
using System.Text;
using Npgsql;

/// <summary>
/// A login role belonging to one migrated database.
/// <para><see cref="Password"/> is null when the role already existed: an existing role's
/// password is never rotated, because something is probably already using it.</para>
/// </summary>
public sealed record ProvisionedUser(string Role, string? Password, bool Created);

/// <summary>
/// Gives each migrated database its own PostgreSQL login.
///
/// <para>The role is made the owner of the database and of everything in its public schema,
/// and is additionally granted explicit privileges. Ownership alone would be enough for a
/// well-behaved target, but the grants cost nothing and keep the role usable even when an
/// ownership transfer is refused — which happens whenever the migrator connects as
/// something less than a superuser.</para>
/// </summary>
public static class UserProvisioner
{
    /// <summary>
    /// Unambiguous alphabet: no l/1/I, no O/0. These passwords get read off a PDF and typed
    /// into a connection string by a human, and a character you cannot identify is a support
    /// ticket. 24 characters of this alphabet is ~139 bits.
    /// </summary>
    private const string PasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int PasswordLength = 24;

    /// <param name="restrictPublicAccess">
    /// Takes CONNECT away from PUBLIC, so the database is reachable only by its own role.
    /// Pass this only for a database the current run created: PostgreSQL grants CONNECT to
    /// PUBLIC by default, and revoking it on a database that was already there would lock
    /// out whoever has been relying on that default.
    /// </param>
    public static async Task<ProvisionedUser?> EnsureAsync(
        string maintenanceConnectionString,
        string targetConnectionString,
        string databaseName,
        string roleName,
        bool restrictPublicAccess,
        IProgress<ProgressMessage> progress,
        CancellationToken ct = default)
    {
        progress.Report(new(ProgressKind.Step, $"Creating the database user '{roleName}'",
            MessageCode.StepCreatingUser, new object?[] { roleName }));

        try
        {
            await using var maintenance = new NpgsqlConnection(maintenanceConnectionString);
            await maintenance.OpenAsync(ct);

            string? password = null;
            bool created;
            await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @r", maintenance))
            {
                exists.Parameters.AddWithValue("r", roleName);
                created = await exists.ExecuteScalarAsync(ct) is null;
            }

            if (created)
            {
                password = GeneratePassword();
                await ExecAsync(maintenance,
                    $"CREATE ROLE {TargetDatabase.Quote(roleName)} LOGIN PASSWORD {Literal(password)}", ct);
                progress.Report(new(ProgressKind.Success, $"Role '{roleName}' created.",
                    MessageCode.SuccessUserCreated, new object?[] { roleName }));
            }
            else
            {
                progress.Report(new(ProgressKind.Warning,
                    $"Role '{roleName}' already exists — its password was left unchanged.",
                    MessageCode.WarnUserExists, new object?[] { roleName }));
            }

            var database = TargetDatabase.Quote(databaseName);
            var role = TargetDatabase.Quote(roleName);
            await ExecAsync(maintenance, $"GRANT CONNECT ON DATABASE {database} TO {role}", ct);

            if (restrictPublicAccess)
            {
                await ExecAsync(maintenance, $"REVOKE CONNECT ON DATABASE {database} FROM PUBLIC", ct);
                progress.Report(new(ProgressKind.Info,
                    $"Only '{roleName}' can connect to '{databaseName}' now.",
                    MessageCode.InfoDatabaseIsolated, new object?[] { roleName, databaseName }));
            }

            // Ownership is what lets the role later ALTER and DROP its own tables (an EF
            // migration run against the target needs exactly that). It is best-effort: a
            // non-superuser migrator cannot hand out ownership, and that is not fatal.
            var ownershipTaken = await TryOwnershipAsync(maintenance, database, role, ct);
            await using (var target = new NpgsqlConnection(targetConnectionString))
            {
                await target.OpenAsync(ct);
                await ExecAsync(target, $"GRANT USAGE, CREATE ON SCHEMA public TO {role}", ct);
                await ExecAsync(target, $"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO {role}", ct);
                await ExecAsync(target, $"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO {role}", ct);
                // Tables created later — by a migration, by the app — must not need a second visit.
                await ExecAsync(target,
                    $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO {role}", ct);
                await ExecAsync(target,
                    $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO {role}", ct);
                ownershipTaken &= await TryObjectOwnershipAsync(target, roleName, ct);
            }

            if (!ownershipTaken)
                progress.Report(new(ProgressKind.Warning,
                    $"Ownership could not be transferred to '{roleName}' — it has full privileges but cannot alter or drop objects. " +
                    "Connect as a superuser to change that.",
                    MessageCode.WarnUserOwnership, new object?[] { roleName }));

            progress.Report(new(ProgressKind.Info, $"Privileges on '{databaseName}' granted to '{roleName}'.",
                MessageCode.InfoUserPrivileges, new object?[] { databaseName, roleName }));
            return new ProvisionedUser(roleName, password, created);
        }
        catch (Exception ex)
        {
            progress.Report(new(ProgressKind.Error, $"The user '{roleName}' could not be created: {ex.Message}",
                MessageCode.ErrorUserFailed, new object?[] { roleName, ex.Message }));
            return null;
        }
    }

    private static async Task<bool> TryOwnershipAsync(
        NpgsqlConnection maintenance, string quotedDatabase, string quotedRole, CancellationToken ct)
    {
        try
        {
            await ExecAsync(maintenance, $"ALTER DATABASE {quotedDatabase} OWNER TO {quotedRole}", ct);
            return true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands the public schema and everything in it to the role. One round trip rather than
    /// one per object: a mirrored database can hold hundreds of tables.
    /// </summary>
    private static async Task<bool> TryObjectOwnershipAsync(
        NpgsqlConnection target, string roleName, CancellationToken ct)
    {
        var role = Literal(roleName);
        var sql = $"""
            DO $$
            DECLARE r record;
            BEGIN
              EXECUTE format('ALTER SCHEMA public OWNER TO %I', {role});
              FOR r IN SELECT tablename AS n FROM pg_tables WHERE schemaname = 'public' LOOP
                EXECUTE format('ALTER TABLE public.%I OWNER TO %I', r.n, {role});
              END LOOP;
              FOR r IN SELECT sequencename AS n FROM pg_sequences WHERE schemaname = 'public' LOOP
                EXECUTE format('ALTER SEQUENCE public.%I OWNER TO %I', r.n, {role});
              END LOOP;
              FOR r IN SELECT viewname AS n FROM pg_views WHERE schemaname = 'public' LOOP
                EXECUTE format('ALTER VIEW public.%I OWNER TO %I', r.n, {role});
              END LOOP;
            END $$;
            """;
        try
        {
            await ExecAsync(target, sql, ct);
            return true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns a database name into a legal role name: lower case, only letters, digits and
    /// underscores, never starting with a digit, never longer than PostgreSQL's 63-byte
    /// identifier limit. The pattern may use <c>{db}</c> for the sanitized database name.
    /// </summary>
    public static string BuildRoleName(string pattern, string databaseName)
    {
        var sanitized = Sanitize(databaseName);
        var name = Sanitize((string.IsNullOrWhiteSpace(pattern) ? "{db}_user" : pattern)
            .Replace("{db}", sanitized, StringComparison.Ordinal));
        if (name.Length == 0) name = "migrated_user";
        if (char.IsDigit(name[0])) name = "_" + name;
        return name.Length <= 63 ? name : name[..63];
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        return builder.ToString();
    }

    private static string GeneratePassword()
    {
        var buffer = new char[PasswordLength];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        return new string(buffer);
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }
}
