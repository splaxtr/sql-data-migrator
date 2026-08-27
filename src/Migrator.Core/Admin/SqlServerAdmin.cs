namespace Migrator.Core.Admin;

using Microsoft.Data.SqlClient;

/// <summary>
/// Server administration for SQL Server.
///
/// <para>SQL Server splits in two what PostgreSQL keeps in one. A <em>login</em> exists on the
/// server; a <em>user</em> exists inside a database and is mapped to a login by SID. The panel
/// speaks in logins, and the privilege levels below are implemented as the database user plus
/// the fixed database roles that mean the same thing:</para>
///
/// <list type="bullet">
///   <item><description><see cref="PrivilegeLevel.Connect"/> — a user exists, in no role.</description></item>
///   <item><description><see cref="PrivilegeLevel.ReadWrite"/> — plus <c>db_datareader</c> and <c>db_datawriter</c>.</description></item>
///   <item><description><see cref="PrivilegeLevel.Owner"/> — the login owns the database outright.</description></item>
/// </list>
///
/// <para>The same substitution happens server-side: SQL Server has no per-login CREATEDB or
/// CREATEROLE flag, so those map to the <c>dbcreator</c> and <c>securityadmin</c> fixed server
/// roles, and "superuser" to <c>sysadmin</c>.</para>
/// </summary>
public sealed class SqlServerAdmin : IServerAdmin
{
    private readonly string _master;

    /// <param name="masterConnectionString">A connection string pointing at <c>master</c>.</param>
    public SqlServerAdmin(string masterConnectionString) => _master = masterConnectionString;

    public AdminCapabilities Capabilities { get; } = new(
        // SQL Server has no database-level PUBLIC grant to revoke; access is the presence of
        // a user, which the privilege level already controls.
        PublicConnect: false,
        Collation: true,
        IcuCollation: false,
        ChangeOwner: true,
        CloseConnections: true,
        Membership: true);

    private static string Q(string name) => AdminIdentifier.SqlServer(name);
    private static string L(string value) => AdminIdentifier.Literal(value);

    private string For(string database)
    {
        AdminIdentifier.Validate(database, 128);
        return new SqlConnectionStringBuilder(_master) { InitialCatalog = database }.ConnectionString;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(CancellationToken ct = default)
    {
        const string query = """
            SELECT d.name,
                   ISNULL(SUSER_SNAME(d.owner_sid), '—'),
                   ISNULL(d.collation_name, '—'),
                   ISNULL((SELECT SUM(CAST(f.size AS bigint)) * 8192 FROM sys.master_files f
                           WHERE f.database_id = d.database_id), 0),
                   (SELECT COUNT(*) FROM sys.dm_exec_sessions s WHERE s.database_id = d.database_id),
                   CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END
            FROM sys.databases d
            ORDER BY d.name
            """;
        var result = new List<DatabaseSummary>();
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new DatabaseSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5) == 1));
        return result;
    }

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct = default)
    {
        const string query = """
            SELECT p.name,
                   CASE WHEN p.is_disabled = 0 THEN 1 ELSE 0 END,
                   CASE WHEN p.type = 'R' THEN 1 ELSE 0 END,
                   ISNULL(STUFF((SELECT ',' + r.name
                                 FROM sys.server_role_members m
                                 JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
                                 WHERE m.member_principal_id = p.principal_id
                                 ORDER BY r.name
                                 FOR XML PATH('')), 1, 1, ''), ''),
                   -- A backslash means nothing to T-SQL's LIKE, so these need no escape
                   -- clause; adding one would turn the % into a literal and match nothing.
                   CASE WHEN p.type = 'R' OR p.name = 'sa' OR p.name LIKE '##%'
                             OR p.name LIKE 'NT AUTHORITY\%'
                             OR p.name LIKE 'NT SERVICE\%'
                             OR p.name LIKE 'BUILTIN\%'
                        THEN 1 ELSE 0 END
            FROM sys.server_principals p
            -- Server roles ('R') are listed too, and not as an afterthought: they are the
            -- only groups a login can be put into, so leaving them out would leave the
            -- membership control with nothing to offer. They are marked system, which is
            -- what stops the panel from putting a delete button next to sysadmin.
            WHERE p.type IN ('S', 'U', 'G', 'R')
            ORDER BY p.name
            """;
        var result = new List<RoleSummary>();
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var memberOf = reader.GetString(3) is { Length: > 0 } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            var canLogin = reader.GetInt32(1) == 1;
            result.Add(new RoleSummary(
                reader.GetString(0),
                canLogin,
                new RoleAttributes(
                    canLogin,
                    memberOf.Contains("dbcreator"),
                    memberOf.Contains("securityadmin"),
                    memberOf.Contains("sysadmin")),
                // Fixed server roles are the mechanism behind the attribute switches above;
                // repeating them in the membership list would let the panel offer two
                // controls for one fact.
                memberOf.Where(m => m is not ("dbcreator" or "securityadmin" or "sysadmin")).ToArray(),
                reader.GetInt32(4) == 1,
                reader.GetInt32(2) == 1));
        }
        return result;
    }

    public async Task<IReadOnlyList<DatabaseGrant>> ListGrantsAsync(
        string database, CancellationToken ct = default)
    {
        var owner = await DatabaseOwnerAsync(database, ct);
        var result = new List<DatabaseGrant>();
        if (owner is not null) result.Add(new DatabaseGrant(owner, PrivilegeLevel.Owner));

        // Mapped by SID rather than by name: a database user may legitimately be named
        // differently from the login it belongs to, and reporting the user's name as if it
        // were a login would be a grant attributed to the wrong principal.
        const string query = """
            SELECT ISNULL(SUSER_SNAME(dp.sid), dp.name),
                   MAX(CASE WHEN r.name = 'db_owner' THEN 1 ELSE 0 END),
                   MAX(CASE WHEN r.name IN ('db_datareader', 'db_datawriter') THEN 1 ELSE 0 END)
            FROM sys.database_principals dp
            LEFT JOIN sys.database_role_members m ON m.member_principal_id = dp.principal_id
            LEFT JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            WHERE dp.type IN ('S', 'U', 'G') AND dp.principal_id > 4
            GROUP BY ISNULL(SUSER_SNAME(dp.sid), dp.name)
            """;
        await using var connection = new SqlConnection(For(database));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (name == owner) continue;
            result.Add(new DatabaseGrant(name,
                reader.GetInt32(1) == 1 ? PrivilegeLevel.Owner
                : reader.GetInt32(2) == 1 ? PrivilegeLevel.ReadWrite
                : PrivilegeLevel.Connect));
        }
        return result.OrderBy(g => g.Role, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<string?> DatabaseOwnerAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT SUSER_SNAME(owner_sid) FROM sys.databases WHERE name = @n", connection);
        command.Parameters.AddWithValue("n", database);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    // ── Databases ─────────────────────────────────────────────────────────────

    public async Task CreateDatabaseAsync(
        string name, string? collation, string? owner, CancellationToken ct = default)
    {
        var collate = string.IsNullOrWhiteSpace(collation) ? "" : $" COLLATE {Collation(collation)}";
        await ExecAsync(_master, $"CREATE DATABASE {Q(name)}{collate}", ct);
        if (!string.IsNullOrWhiteSpace(owner))
            await SetDatabaseOwnerAsync(name, owner, ct);
    }

    /// <summary>
    /// A collation name cannot be quoted — it is a keyword position, not an identifier — so
    /// it is the one value here that has to be checked instead of escaped.
    /// </summary>
    private static string Collation(string collation)
    {
        if (!collation.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Collation adı yalnızca harf, rakam ve alt çizgi içerebilir.");
        return collation;
    }

    public async Task SetDatabaseOwnerAsync(string database, string owner, CancellationToken ct = default)
    {
        // SQL Server refuses to hand a database to a login that already has a user in it,
        // and the message it gives says nothing about what to do. Removing the user first is
        // what the operator meant: ownership outranks the user it replaces.
        await DropDatabaseUserAsync(database, owner, ct);
        await ExecAsync(_master, $"ALTER AUTHORIZATION ON DATABASE::{Q(database)} TO {Q(owner)}", ct);
    }

    public async Task<DatabaseDropPreview> PreviewDatabaseDropAsync(
        string name, CancellationToken ct = default)
    {
        const string head = """
            SELECT ISNULL(SUSER_SNAME(d.owner_sid), '—'),
                   ISNULL((SELECT SUM(CAST(f.size AS bigint)) * 8192 FROM sys.master_files f
                           WHERE f.database_id = d.database_id), 0),
                   (SELECT COUNT(*) FROM sys.dm_exec_sessions s WHERE s.database_id = d.database_id),
                   CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END
            FROM sys.databases d WHERE d.name = @n
            """;
        string ownerName;
        long size;
        int connections;
        bool system;
        await using (var connection = new SqlConnection(_master))
        {
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(head, connection);
            command.Parameters.AddWithValue("n", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"'{name}' veritabanı bulunamadı.");
            (ownerName, size, connections, system) =
                (reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3) == 1);
        }

        var tables = 0;
        var rows = 0L;
        try
        {
            await using var inner = new SqlConnection(For(name));
            await inner.OpenAsync(ct);
            await using var command = new SqlCommand("""
                SELECT COUNT(DISTINCT t.object_id), ISNULL(SUM(p.rows), 0)
                FROM sys.tables t
                JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                """, inner);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                (tables, rows) = (reader.GetInt32(0), reader.GetInt64(1));
        }
        catch (SqlException)
        {
            // Offline, restoring, or not ours to enter: the rest of the preview still stands.
        }

        return new DatabaseDropPreview(name, ownerName, size, tables, rows, connections, system);
    }

    public async Task DropDatabaseAsync(
        string name, bool closeConnections, CancellationToken ct = default)
    {
        SqlConnection.ClearPool(new SqlConnection(For(name)));
        if (closeConnections)
            await ExecAsync(_master, $"ALTER DATABASE {Q(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE", ct);
        await ExecAsync(_master, $"DROP DATABASE {Q(name)}", ct);
    }

    // ── Logins ────────────────────────────────────────────────────────────────

    public async Task CreateRoleAsync(
        string name, string password, RoleAttributes attributes, CancellationToken ct = default)
    {
        await ExecAsync(_master, $"CREATE LOGIN {Q(name)} WITH PASSWORD = {L(password)}", ct);
        await SetRoleAttributesAsync(name, attributes, ct);
    }

    public async Task SetRolePasswordAsync(string name, string password, CancellationToken ct = default) =>
        await ExecAsync(_master, $"ALTER LOGIN {Q(name)} WITH PASSWORD = {L(password)}", ct);

    public async Task SetRoleAttributesAsync(
        string name, RoleAttributes attributes, CancellationToken ct = default)
    {
        await ExecAsync(_master, $"ALTER LOGIN {Q(name)} {(attributes.CanLogin ? "ENABLE" : "DISABLE")}", ct);
        await SetServerRoleAsync(name, "dbcreator", attributes.CreateDb, ct);
        await SetServerRoleAsync(name, "securityadmin", attributes.CreateRole, ct);
        await SetServerRoleAsync(name, "sysadmin", attributes.Superuser, ct);
    }

    private Task SetServerRoleAsync(string login, string role, bool member, CancellationToken ct) =>
        ExecAsync(_master, member
            ? $"ALTER SERVER ROLE {Q(role)} ADD MEMBER {Q(login)}"
            : $"ALTER SERVER ROLE {Q(role)} DROP MEMBER {Q(login)}", ct);

    public async Task<RoleDropPreview> PreviewRoleDropAsync(string name, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(ct);

        await using (var exists = new SqlCommand(
            "SELECT 1 FROM sys.server_principals WHERE name = @n AND type IN ('S','U','G')", connection))
        {
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new InvalidOperationException($"'{name}' login'i bulunamadı.");
        }

        var owns = new List<string>();
        await using (var command = new SqlCommand(
            "SELECT name FROM sys.databases WHERE SUSER_SNAME(owner_sid) = @n ORDER BY name", connection))
        {
            command.Parameters.AddWithValue("n", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) owns.Add(reader.GetString(0));
        }

        var system = name == "sa" || name.StartsWith("##", StringComparison.Ordinal)
            || name.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("BUILTIN\\", StringComparison.OrdinalIgnoreCase);

        await using var current = new SqlCommand("SELECT SUSER_SNAME()", connection);
        var me = await current.ExecuteScalarAsync(ct) as string;

        // SQL Server blocks a DROP LOGIN on database ownership; database users mapped to it
        // are orphaned rather than blocking, so owned databases are the whole list here.
        var dependencies = owns.Select(d => new OwnedObjects(d, 1)).ToList();

        return new RoleDropPreview(name, owns, dependencies, system,
            string.Equals(me, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DropRoleAsync(string name, CancellationToken ct = default) =>
        await ExecAsync(_master, $"DROP LOGIN {Q(name)}", ct);

    /// <summary>
    /// What a login owns is databases, so handing them over is the whole job — and
    /// <see cref="SetDatabaseOwnerAsync"/> already knows how, including clearing the user
    /// mapping the new owner would otherwise collide with.
    /// </summary>
    public async Task ReassignOwnedAsync(
        string role, string newOwner, CancellationToken ct = default)
    {
        var preview = await PreviewRoleDropAsync(role, ct);
        foreach (var database in preview.Owns)
            await SetDatabaseOwnerAsync(database, newOwner, ct);
    }

    public async Task SetMembershipAsync(
        string role, string group, bool member, CancellationToken ct = default) =>
        await SetServerRoleAsync(role, group, member, ct);

    // ── Privileges ────────────────────────────────────────────────────────────

    public async Task SetPrivilegeAsync(
        string database, string role, PrivilegeLevel level, CancellationToken ct = default)
    {
        var owner = await DatabaseOwnerAsync(database, ct);
        if (string.Equals(owner, role, StringComparison.OrdinalIgnoreCase) && level != PrivilegeLevel.Owner)
            // Silently reassigning to sa would be this tool picking an owner nobody asked
            // for, on a server it does not otherwise make policy for.
            throw new InvalidOperationException(
                $"'{role}' şu anda '{database}' veritabanının sahibi. Önce başka bir sahip belirleyin.");

        if (level == PrivilegeLevel.Owner)
        {
            await SetDatabaseOwnerAsync(database, role, ct);
            return;
        }

        await DropDatabaseUserAsync(database, role, ct);
        if (level == PrivilegeLevel.None) return;

        await ExecInAsync(database, $"CREATE USER {Q(role)} FOR LOGIN {Q(role)}", ct);
        if (level == PrivilegeLevel.ReadWrite)
            await ExecInAsync(database, $"""
                ALTER ROLE [db_datareader] ADD MEMBER {Q(role)};
                ALTER ROLE [db_datawriter] ADD MEMBER {Q(role)};
                """, ct);
    }

    /// <summary>
    /// Removes the database user mapped to a login, by SID, if it is there. Named users are
    /// not assumed to match their login: the mapping is the SID.
    /// </summary>
    private async Task DropDatabaseUserAsync(string database, string login, CancellationToken ct)
    {
        await using var connection = new SqlConnection(For(database));
        await connection.OpenAsync(ct);
        await using var find = new SqlCommand(
            "SELECT name FROM sys.database_principals WHERE type IN ('S','U','G') AND SUSER_SNAME(sid) = @n",
            connection);
        find.Parameters.AddWithValue("n", login);
        var user = await find.ExecuteScalarAsync(ct) as string;
        if (user is null) return;
        // A login that owns the database is mapped to dbo inside it. dbo cannot be dropped,
        // and there is nothing to drop: handing ownership elsewhere re-points dbo by itself.
        if (user == "dbo") return;

        await using var drop = new SqlCommand($"DROP USER {Q(user)}", connection);
        await drop.ExecuteNonQueryAsync(ct);
    }

    public Task SetPublicConnectAsync(string database, bool allowed, CancellationToken ct = default) =>
        throw new NotSupportedException("SQL Server'da veritabanı düzeyinde PUBLIC bağlantı yetkisi yoktur.");

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static async Task ExecAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private Task ExecInAsync(string database, string sql, CancellationToken ct) =>
        ExecAsync(For(database), sql, ct);
}
