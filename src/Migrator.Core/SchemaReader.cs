namespace Migrator.Core;

using Microsoft.Data.SqlClient;
using Npgsql;

/// <summary>Reads both sides' schemas from information_schema.</summary>
public static class SchemaReader
{
    public static async Task<List<string>> ListSqlServerDatabasesAsync(string connectionString, CancellationToken ct = default)
    {
        const string query = """
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1
            ORDER BY name
            """;
        var result = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public static async Task<List<string>> ListPostgresDatabasesAsync(string connectionString, CancellationToken ct = default)
    {
        const string query = """
            SELECT datname FROM pg_database
            WHERE NOT datistemplate AND datallowconn AND datname <> 'postgres'
            ORDER BY datname
            """;
        var result = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Opens the source and closes it again. Callers that create something in the target
    /// use this first: a target database created for a source that turns out to be
    /// unreachable is an empty database nobody asked for, and in a batch there would be
    /// one of them per item.
    /// </summary>
    public static async Task ProbeSqlServerAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
    }

    public static async Task<Dictionary<string, List<ColumnInfo>>> ReadSqlServerAsync(SqlConnection sql, CancellationToken ct = default)
    {
        const string query = """
            SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH,
                   c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                   COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity')
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.TABLE_SCHEMA = 'dbo' AND t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
            """;
        var result = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        await using var command = new SqlCommand(query, sql) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var columns))
                result[table] = columns = new List<ColumnInfo>();
            int? maxLength = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            int? precision = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5));
            int? scale = reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6));
            var isIdentity = !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7)) == 1;
            columns.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetString(3) == "YES",
                isIdentity, false, maxLength, precision, scale));
        }
        return result;
    }

    public static async Task<Dictionary<string, List<string>>> ReadSqlServerPrimaryKeysAsync(
        SqlConnection sql, CancellationToken ct = default)
    {
        const string query = """
            SELECT tc.TABLE_NAME, kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
             AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA AND kcu.TABLE_NAME = tc.TABLE_NAME
            WHERE tc.TABLE_SCHEMA = 'dbo' AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY tc.TABLE_NAME, kcu.ORDINAL_POSITION
            """;
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var command = new SqlCommand(query, sql) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var columns))
                result[table] = columns = new List<string>();
            columns.Add(reader.GetString(1));
        }
        return result;
    }

    /// <summary>
    /// Base tables the source holds outside <c>dbo</c>, grouped by schema.
    ///
    /// <para>Everything else in this class filters to <c>dbo</c>, which is what makes those
    /// tables invisible: absent from <c>sourceSchema</c>, they are not source-only tables
    /// either, so nothing ever mentions them. This query exists purely so the run can name
    /// what it is leaving behind.</para>
    /// </summary>
    public static async Task<List<(string Schema, int Tables)>> ReadSqlServerOtherSchemasAsync(
        SqlConnection sql, CancellationToken ct = default)
    {
        const string query = """
            SELECT s.name, COUNT(*)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name <> 'dbo' AND t.is_ms_shipped = 0
            GROUP BY s.name
            ORDER BY s.name
            """;
        var result = new List<(string, int)>();
        await using var command = new SqlCommand(query, sql) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    /// <summary>
    /// The ORM migration-history tables a source database carries, if any.
    ///
    /// <para>Cheap on purpose: one catalog query, no columns read. It exists so the browser
    /// can tell an operator that the database they are about to mirror is ORM-managed at the
    /// moment they tick the box, rather than in the log of a run they already started.</para>
    /// </summary>
    public static async Task<List<string>> ReadSqlServerHistoryTablesAsync(
        string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT name FROM sys.tables WHERE is_ms_shipped = 0", connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        return MigrationHistory.In(tables);
    }

    public static async Task<List<ForeignKey>> ReadSqlServerForeignKeysAsync(
        SqlConnection sql, CancellationToken ct = default)
    {
        // sys.foreign_keys naming is inverted relative to ours: its parent_object_id is the
        // table CARRYING the constraint (our child), referenced_object_id the one pointed at.
        const string query = """
            SELECT fk.name, ct.name, pt.name, cc.name, pc.name, fk.delete_referential_action_desc
            FROM sys.foreign_keys fk
            JOIN sys.tables ct ON ct.object_id = fk.parent_object_id
            JOIN sys.tables pt ON pt.object_id = fk.referenced_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id AND cc.column_id = fkc.parent_column_id
            JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id
            WHERE SCHEMA_NAME(ct.schema_id) = 'dbo' AND SCHEMA_NAME(pt.schema_id) = 'dbo'
            ORDER BY fk.name, fkc.constraint_column_id
            """;
        var byName = new Dictionary<string, ForeignKey>(StringComparer.Ordinal);
        await using var command = new SqlCommand(query, sql) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var fk))
                byName[name] = fk = new ForeignKey(name, reader.GetString(1), reader.GetString(2),
                    new List<(string, string)>(), reader.GetString(5));
            fk.Columns.Add((reader.GetString(3), reader.GetString(4)));
        }
        return byName.Values.ToList();
    }

    public static async Task<Dictionary<string, List<ColumnInfo>>> ReadPostgresAsync(NpgsqlConnection pg, CancellationToken ct = default)
    {
        const string query = """
            SELECT c.table_name, c.column_name, c.udt_name, c.is_nullable, c.is_identity,
                   c.column_default IS NOT NULL, c.character_maximum_length
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
            ORDER BY c.table_name, c.ordinal_position
            """;
        var result = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(query, pg) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var columns))
                result[table] = columns = new List<ColumnInfo>();
            int? maxLength = reader.IsDBNull(6) ? null : reader.GetInt32(6);
            columns.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetString(3) == "YES",
                reader.GetString(4) == "YES", reader.GetBoolean(5), maxLength));
        }
        return result;
    }

    public static async Task<List<ForeignKey>> ReadForeignKeysAsync(NpgsqlConnection pg, CancellationToken ct = default)
    {
        const string query = """
            SELECT con.conname, ch.relname, ca.attname, pt.relname, pa.attname, k.ord
            FROM pg_constraint con
            JOIN pg_class ch ON ch.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = ch.relnamespace
            JOIN pg_class pt ON pt.oid = con.confrelid
            JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(child_attnum, parent_attnum, ord) ON true
            JOIN pg_attribute ca ON ca.attrelid = con.conrelid AND ca.attnum = k.child_attnum
            JOIN pg_attribute pa ON pa.attrelid = con.confrelid AND pa.attnum = k.parent_attnum
            WHERE con.contype = 'f' AND n.nspname = 'public'
            ORDER BY con.conname, k.ord
            """;
        var byName = new Dictionary<string, ForeignKey>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(query, pg) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var fk))
                byName[name] = fk = new ForeignKey(name, reader.GetString(1), reader.GetString(3), new List<(string, string)>());
            fk.Columns.Add((reader.GetString(2), reader.GetString(4)));
        }
        return byName.Values.ToList();
    }
}
