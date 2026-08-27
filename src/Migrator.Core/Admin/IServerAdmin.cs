namespace Migrator.Core.Admin;

/// <summary>
/// Everything the management panel can ask of a server, in one shape for both products.
///
/// <para>Every method that names an object takes that name as data and quotes it before it
/// reaches a statement — see <see cref="AdminIdentifier"/>. None of these are parameterizable:
/// no SQL dialect accepts a bound parameter where an identifier goes, so quoting is not a
/// convenience here, it is the whole defence.</para>
/// </summary>
public interface IServerAdmin
{
    AdminCapabilities Capabilities { get; }

    Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseGrant>> ListGrantsAsync(string database, CancellationToken ct = default);

    Task CreateDatabaseAsync(string name, string? collation, string? owner, CancellationToken ct = default);
    Task SetDatabaseOwnerAsync(string database, string owner, CancellationToken ct = default);
    Task<DatabaseDropPreview> PreviewDatabaseDropAsync(string name, CancellationToken ct = default);
    Task DropDatabaseAsync(string name, bool closeConnections, CancellationToken ct = default);

    Task CreateRoleAsync(string name, string password, RoleAttributes attributes, CancellationToken ct = default);
    Task SetRolePasswordAsync(string name, string password, CancellationToken ct = default);
    Task SetRoleAttributesAsync(string name, RoleAttributes attributes, CancellationToken ct = default);
    Task<RoleDropPreview> PreviewRoleDropAsync(string name, CancellationToken ct = default);
    Task DropRoleAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Hands everything a role owns to another one. Both products refuse to drop a principal
    /// that still owns objects, so without this the delete button is a dead end for exactly
    /// the roles this tool creates — a per-database login owns its database and every table
    /// in it. Nothing is destroyed: ownership moves.
    /// </summary>
    Task ReassignOwnedAsync(string role, string newOwner, CancellationToken ct = default);

    Task SetPrivilegeAsync(string database, string role, PrivilegeLevel level, CancellationToken ct = default);
    Task SetPublicConnectAsync(string database, bool allowed, CancellationToken ct = default);
    Task SetMembershipAsync(string role, string group, bool member, CancellationToken ct = default);
}

/// <summary>
/// Quoting and validation for names that arrive from a browser and end up inside DDL.
/// </summary>
public static class AdminIdentifier
{
    /// <summary>
    /// Rejects a name before it is quoted rather than after.
    ///
    /// <para>Correct quoting already makes any name safe, and this runs anyway: a name
    /// carrying a newline or a control character is either a mistake or an attempt, it is
    /// never a database somebody meant to create, and a panel that accepts it produces
    /// statements no operator can read back in a log.</para>
    /// </summary>
    public static void Validate(string? name, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ad boş olamaz.");
        if (name.Any(char.IsControl))
            throw new ArgumentException("Ad, satır sonu veya kontrol karakteri içeremez.");
        if (System.Text.Encoding.UTF8.GetByteCount(name) > maxBytes)
            throw new ArgumentException($"Ad en fazla {maxBytes} bayt olabilir.");
    }

    /// <summary>PostgreSQL: double quotes, with any embedded quote doubled.</summary>
    public static string Postgres(string name)
    {
        Validate(name, 63);
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>SQL Server: square brackets, with any embedded closing bracket doubled.</summary>
    public static string SqlServer(string name)
    {
        Validate(name, 128);
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>A string literal for either product.</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
