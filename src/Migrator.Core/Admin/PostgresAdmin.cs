namespace Migrator.Core.Admin;

using Npgsql;

/// <summary>
/// Server administration for PostgreSQL.
///
/// <para>Two connections are in play throughout. Anything about the server — the catalog,
/// roles, CREATE/DROP DATABASE — runs on the maintenance database. Anything <em>inside</em> a
/// database — its tables, its <c>public</c> schema, its object ownership — needs a connection
/// to that database, because PostgreSQL has no cross-database statements. The methods below
/// open the second one only when they need it.</para>
/// </summary>
public sealed class PostgresAdmin : IServerAdmin
{
    private readonly string _maintenance;
    private string? _localeColumn;

    /// <param name="maintenanceConnectionString">
    /// A connection string pointing at the maintenance database (<c>postgres</c>).
    /// </param>
    public PostgresAdmin(string maintenanceConnectionString)
    {
        // PostgreSQL's DETAIL line is the useful half of a refused DROP — "owner of database
        // Satis / 7 objects in database Satis" is the whole answer, and without it the error
        // says only that something, somewhere, depends on the role. Npgsql withholds DETAIL
        // by default because it can quote row values; nothing in this class reads rows, so
        // here it only ever names catalog objects.
        _maintenance = new NpgsqlConnectionStringBuilder(maintenanceConnectionString)
        {
            IncludeErrorDetail = true,
        }.ConnectionString;
    }

    public AdminCapabilities Capabilities { get; } = new(
        PublicConnect: true,
        Collation: true,
        IcuCollation: true,
        ChangeOwner: true,
        CloseConnections: true,
        Membership: true);

    private static string Q(string name) => AdminIdentifier.Postgres(name);
    private static string L(string value) => AdminIdentifier.Literal(value);

    private string For(string database)
    {
        AdminIdentifier.Validate(database, 63);
        return new NpgsqlConnectionStringBuilder(_maintenance) { Database = database }.ConnectionString;
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// PostgreSQL 15 called it <c>daticulocale</c> and 17 renamed it to <c>datlocale</c>.
    /// Asked once per instance rather than guessed, because guessing wrong is a hard error
    /// on a query that is otherwise the panel's front page.
    /// </summary>
    private async Task<string> LocaleColumnAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (_localeColumn is not null) return _localeColumn;
        const string query = """
            SELECT attname FROM pg_attribute
            WHERE attrelid = 'pg_database'::regclass AND attname IN ('daticulocale', 'datlocale')
            LIMIT 1
            """;
        await using var command = new NpgsqlCommand(query, connection);
        _localeColumn = await command.ExecuteScalarAsync(ct) as string ?? "datcollate";
        return _localeColumn;
    }

    public async Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_maintenance);
        await connection.OpenAsync(ct);
        var locale = await LocaleColumnAsync(connection, ct);

        // Size is guarded: pg_database_size raises on a database the current user may not
        // connect to, and one unreadable database must not blank the whole list.
        var query = $"""
            SELECT d.datname,
                   pg_get_userbyid(d.datdba),
                   coalesce(nullif(d.{locale}, ''), d.datcollate),
                   CASE WHEN has_database_privilege(current_user, d.datname, 'CONNECT')
                        THEN pg_database_size(d.datname) ELSE 0 END,
                   (SELECT count(*) FROM pg_stat_activity a WHERE a.datname = d.datname),
                   (d.datistemplate OR d.datname = 'postgres')
            FROM pg_database d
            ORDER BY d.datname
            """;
        var result = new List<DatabaseSummary>();
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new DatabaseSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), (int)reader.GetInt64(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct = default)
    {
        const string query = """
            SELECT r.rolname, r.rolcanlogin, r.rolcreatedb, r.rolcreaterole, r.rolsuper,
                   coalesce((SELECT array_agg(g.rolname ORDER BY g.rolname)
                             FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                             WHERE m.member = r.oid), ARRAY[]::name[]),
                   r.rolname LIKE 'pg\_%'
            FROM pg_roles r
            ORDER BY r.rolname
            """;
        var result = new List<RoleSummary>();
        await using var connection = new NpgsqlConnection(_maintenance);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new RoleSummary(
                reader.GetString(0),
                reader.GetBoolean(1),
                new RoleAttributes(reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4)),
                reader.GetFieldValue<string[]>(5),
                reader.GetBoolean(6),
                // In PostgreSQL a user is simply a role that can log in; the rest are groups.
                !reader.GetBoolean(1)));
        return result;
    }

    /// <summary>
    /// Reads back what this panel is able to set, rather than the full privilege matrix:
    /// the level reported is the one whose GRANTs are all present.
    ///
    /// <para>Superusers are left out unless one owns the database. They hold every privilege
    /// implicitly, so listing them would put every superuser on every database's row and say
    /// nothing about what anybody granted.</para>
    /// </summary>
    public async Task<IReadOnlyList<DatabaseGrant>> ListGrantsAsync(string database, CancellationToken ct = default)
    {
        const string query = """
            SELECT r.rolname,
                   (SELECT d.datdba FROM pg_database d WHERE d.datname = current_database()) = r.oid,
                   has_database_privilege(r.oid, current_database(), 'CONNECT'),
                   has_schema_privilege(r.oid, 'public', 'CREATE'),
                   r.rolsuper
            FROM pg_roles r
            WHERE r.rolname NOT LIKE 'pg\_%'
            ORDER BY r.rolname
            """;
        var result = new List<DatabaseGrant>();
        await using var connection = new NpgsqlConnection(For(database));
        await connection.OpenAsync(ct);

        // The reader is scoped and drained before the next query: Npgsql runs one statement
        // per connection at a time, so a second command with this one still open would fail.
        await using (var roles = new NpgsqlCommand(query, connection))
        await using (var reader = await roles.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var (name, owner, connect, write, super) =
                    (reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
                     reader.GetBoolean(3), reader.GetBoolean(4));
                var level = owner ? PrivilegeLevel.Owner
                    : write && connect ? PrivilegeLevel.ReadWrite
                    : connect ? PrivilegeLevel.Connect
                    : PrivilegeLevel.None;
                if (level == PrivilegeLevel.None) continue;
                if (super && !owner) continue;
                result.Add(new DatabaseGrant(name, level));
            }
        }

        // PUBLIC is not a role and appears in no role table, so nothing above can report it
        // — and it is the entry that decides whether "no grant" still means "can connect".
        // A null ACL is the default one, which includes CONNECT for PUBLIC.
        const string publicConnect = """
            SELECT datacl IS NULL OR EXISTS (
                       SELECT 1 FROM aclexplode(datacl) a
                       WHERE a.grantee = 0 AND a.privilege_type = 'CONNECT')
            FROM pg_database WHERE datname = current_database()
            """;
        await using (var check = new NpgsqlCommand(publicConnect, connection))
            if (await check.ExecuteScalarAsync(ct) is true)
                result.Add(new DatabaseGrant("PUBLIC", PrivilegeLevel.Connect));

        return result;
    }

    // ── Databases ─────────────────────────────────────────────────────────────

    public async Task CreateDatabaseAsync(
        string name, string? collation, string? owner, CancellationToken ct = default)
    {
        var locale = string.IsNullOrWhiteSpace(collation)
            ? ""
            : $" LOCALE_PROVIDER icu ICU_LOCALE {L(collation)}";
        var ownedBy = string.IsNullOrWhiteSpace(owner) ? "" : $" OWNER {Q(owner)}";
        await ExecAsync(_maintenance,
            $"CREATE DATABASE {Q(name)} ENCODING 'UTF8'{locale} TEMPLATE template0{ownedBy}", ct);
    }

    public async Task SetDatabaseOwnerAsync(string database, string owner, CancellationToken ct = default)
    {
        await ExecAsync(_maintenance, $"ALTER DATABASE {Q(database)} OWNER TO {Q(owner)}", ct);
        // Ownership of the database says nothing about the tables in it; the panel means
        // both by "owner", so it hands over both.
        await TakeObjectOwnershipAsync(database, owner, ct);
    }

    public async Task<DatabaseDropPreview> PreviewDatabaseDropAsync(string name, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_maintenance);
        await connection.OpenAsync(ct);

        const string head = """
            SELECT pg_get_userbyid(d.datdba),
                   CASE WHEN has_database_privilege(current_user, d.datname, 'CONNECT')
                        THEN pg_database_size(d.datname) ELSE 0 END,
                   (SELECT count(*) FROM pg_stat_activity a WHERE a.datname = d.datname),
                   (d.datistemplate OR d.datname = 'postgres')
            FROM pg_database d WHERE d.datname = @n
            """;
        string ownerName;
        long size;
        int connections;
        bool system;
        await using (var command = new NpgsqlCommand(head, connection))
        {
            command.Parameters.AddWithValue("n", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"'{name}' veritabanı bulunamadı.");
            (ownerName, size, connections, system) =
                (reader.GetString(0), reader.GetInt64(1), (int)reader.GetInt64(2), reader.GetBoolean(3));
        }

        // Row counts are the planner's estimates. An exact count would mean a full scan of
        // every table to answer a question asked before a drop, which is the wrong trade —
        // the panel says "yaklaşık" next to the number.
        var tables = 0;
        var rows = 0L;
        try
        {
            await using var inner = new NpgsqlConnection(For(name));
            await inner.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT count(*), coalesce(sum(n_live_tup), 0) FROM pg_stat_user_tables", inner);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                (tables, rows) = ((int)reader.GetInt64(0), reader.GetInt64(1));
        }
        catch (NpgsqlException)
        {
            // A template or a database this login cannot enter: the count stays unknown and
            // the rest of the preview is still worth showing.
        }

        return new DatabaseDropPreview(name, ownerName, size, tables, rows, connections, system);
    }

    public async Task DropDatabaseAsync(string name, bool closeConnections, CancellationToken ct = default)
    {
        // Npgsql pools connections; one this app opened earlier would itself block the drop
        // and then be handed out dead afterwards.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(For(name)));
        var force = closeConnections ? " WITH (FORCE)" : "";
        await ExecAsync(_maintenance, $"DROP DATABASE {Q(name)}{force}", ct);
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    public async Task CreateRoleAsync(
        string name, string password, RoleAttributes attributes, CancellationToken ct = default)
    {
        await ExecAsync(_maintenance,
            $"CREATE ROLE {Q(name)} {Flags(attributes)} PASSWORD {L(password)}", ct);
    }

    public async Task SetRolePasswordAsync(string name, string password, CancellationToken ct = default) =>
        await ExecAsync(_maintenance, $"ALTER ROLE {Q(name)} PASSWORD {L(password)}", ct);

    public async Task SetRoleAttributesAsync(
        string name, RoleAttributes attributes, CancellationToken ct = default) =>
        await ExecAsync(_maintenance, $"ALTER ROLE {Q(name)} {Flags(attributes)}", ct);

    private static string Flags(RoleAttributes a) => string.Join(' ',
        a.CanLogin ? "LOGIN" : "NOLOGIN",
        a.CreateDb ? "CREATEDB" : "NOCREATEDB",
        a.CreateRole ? "CREATEROLE" : "NOCREATEROLE",
        a.Superuser ? "SUPERUSER" : "NOSUPERUSER");

    public async Task<RoleDropPreview> PreviewRoleDropAsync(string name, CancellationToken ct = default)
    {
        const string head = """
            SELECT coalesce((SELECT array_agg(d.datname ORDER BY d.datname)
                             FROM pg_database d WHERE pg_get_userbyid(d.datdba) = r.rolname), ARRAY[]::name[]),
                   r.rolname LIKE 'pg\_%',
                   r.rolname = current_user
            FROM pg_roles r WHERE r.rolname = @n
            """;
        string[] owns;
        bool system, current;
        await using var connection = new NpgsqlConnection(_maintenance);
        await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(head, connection))
        {
            command.Parameters.AddWithValue("n", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"'{name}' rolü bulunamadı.");
            (owns, system, current) =
                (reader.GetFieldValue<string[]>(0), reader.GetBoolean(1), reader.GetBoolean(2));
        }

        // What actually blocks DROP ROLE lives in every database at once, and visiting them
        // one by one to find out would be a connection per database. pg_shdepend is the
        // catalog PostgreSQL itself consults to write that error, so ask it the same
        // question first — the answer is the same one, before the attempt instead of after.
        const string depends = """
            SELECT d.datname, count(*)
            FROM pg_shdepend s
            JOIN pg_database d ON d.oid = s.dbid
            WHERE s.refclassid = 'pg_authid'::regclass
              AND s.refobjid = (SELECT oid FROM pg_roles WHERE rolname = @n)
              AND s.deptype IN ('o', 'a')
            GROUP BY d.datname
            ORDER BY d.datname
            """;
        var dependencies = new List<OwnedObjects>();
        await using (var command = new NpgsqlCommand(depends, connection))
        {
            command.Parameters.AddWithValue("n", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                dependencies.Add(new OwnedObjects(reader.GetString(0), (int)reader.GetInt64(1)));
        }

        return new RoleDropPreview(name, owns, dependencies, system, current);
    }

    public async Task DropRoleAsync(string name, CancellationToken ct = default) =>
        await ExecAsync(_maintenance, $"DROP ROLE {Q(name)}", ct);

    /// <summary>
    /// Hands everything a role owns to another role, so it can then be dropped.
    ///
    /// <para>This is PostgreSQL's own recipe and it destroys nothing: <c>REASSIGN OWNED</c>
    /// changes owners, and the <c>DROP OWNED</c> that follows it has no objects left to drop
    /// — after a complete reassignment all it removes is the privilege and default-privilege
    /// entries naming the old role, which is the other half of what blocks a DROP ROLE.</para>
    ///
    /// <para>Both statements only see the database they run in, so they run once per database
    /// the role has any dependency in. The maintenance database is included even when it has
    /// none: shared objects — database ownership itself — are reassigned from there.</para>
    /// </summary>
    public async Task ReassignOwnedAsync(
        string role, string newOwner, CancellationToken ct = default)
    {
        var from = Q(role);
        var to = Q(newOwner);
        var statements = $"REASSIGN OWNED BY {from} TO {to}; DROP OWNED BY {from};";

        var preview = await PreviewRoleDropAsync(role, ct);
        var databases = preview.Dependencies.Select(d => d.Database)
            .Concat(preview.Owns)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await ExecAsync(_maintenance, statements, ct);
        foreach (var database in databases)
        {
            if (string.Equals(database, new NpgsqlConnectionStringBuilder(_maintenance).Database, StringComparison.Ordinal))
                continue;
            await ExecInAsync(database, statements, ct);
        }
    }

    public async Task SetMembershipAsync(
        string role, string group, bool member, CancellationToken ct = default) =>
        await ExecAsync(_maintenance, member
            ? $"GRANT {Q(group)} TO {Q(role)}"
            : $"REVOKE {Q(group)} FROM {Q(role)}", ct);

    // ── Privileges ────────────────────────────────────────────────────────────

    public async Task SetPrivilegeAsync(
        string database, string role, PrivilegeLevel level, CancellationToken ct = default)
    {
        var db = Q(database);
        var r = Q(role);

        // Always start from nothing this tool granted, so moving a role down a level really
        // takes the higher one away instead of leaving it in place underneath.
        await ExecAsync(_maintenance, $"REVOKE ALL ON DATABASE {db} FROM {r}", ct);
        await ExecInAsync(database, $"""
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM {r};
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM {r};
            REVOKE ALL ON SCHEMA public FROM {r};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM {r};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM {r};
            """, ct);

        if (level == PrivilegeLevel.None) return;

        await ExecAsync(_maintenance, $"GRANT CONNECT ON DATABASE {db} TO {r}", ct);
        if (level == PrivilegeLevel.Connect) return;

        await ExecInAsync(database, $"""
            GRANT USAGE, CREATE ON SCHEMA public TO {r};
            GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO {r};
            GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO {r};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO {r};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO {r};
            """, ct);
        if (level == PrivilegeLevel.ReadWrite) return;

        await SetDatabaseOwnerAsync(database, role, ct);
    }

    public async Task SetPublicConnectAsync(string database, bool allowed, CancellationToken ct = default) =>
        await ExecAsync(_maintenance, allowed
            ? $"GRANT CONNECT ON DATABASE {Q(database)} TO PUBLIC"
            : $"REVOKE CONNECT ON DATABASE {Q(database)} FROM PUBLIC", ct);

    /// <summary>
    /// Hands the public schema and everything in it to a role, in one round trip rather than
    /// one per object: a migrated database can hold hundreds of tables.
    /// </summary>
    private async Task TakeObjectOwnershipAsync(string database, string role, CancellationToken ct)
    {
        var literal = L(role);
        await ExecInAsync(database, $"""
            DO $$
            DECLARE r record;
            BEGIN
              EXECUTE format('ALTER SCHEMA public OWNER TO %I', {literal});
              FOR r IN SELECT tablename AS n FROM pg_tables WHERE schemaname = 'public' LOOP
                EXECUTE format('ALTER TABLE public.%I OWNER TO %I', r.n, {literal});
              END LOOP;
              FOR r IN SELECT sequencename AS n FROM pg_sequences WHERE schemaname = 'public' LOOP
                EXECUTE format('ALTER SEQUENCE public.%I OWNER TO %I', r.n, {literal});
              END LOOP;
              FOR r IN SELECT viewname AS n FROM pg_views WHERE schemaname = 'public' LOOP
                EXECUTE format('ALTER VIEW public.%I OWNER TO %I', r.n, {literal});
              END LOOP;
            END $$;
            """, ct);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static async Task ExecAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private Task ExecInAsync(string database, string sql, CancellationToken ct) =>
        ExecAsync(For(database), sql, ct);
}
