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

    public static async Task<Dictionary<string, List<ColumnInfo>>> ReadSqlServerAsync(SqlConnection sql, CancellationToken ct = default)
    {
        const string query = """
            SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH
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
            columns.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetString(3) == "YES", false, false, maxLength));
        }
        return result;
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
