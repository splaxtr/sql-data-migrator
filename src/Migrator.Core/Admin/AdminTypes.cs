namespace Migrator.Core.Admin;

/// <summary>
/// Server administration, kept deliberately apart from the migration engine.
///
/// <para>Nothing in this namespace takes part in a migration, and none of the guarantees in
/// docs/SAFETY.md about a migration apply to it: these operations create, alter and drop
/// things because that is what they are for. The engine's promise — that a run either moves
/// the data or leaves the target untouched — is about the engine, not about a person using
/// an admin panel.</para>
///
/// <para>The two implementations speak different products' dialects behind one shape, so the
/// panel is one panel. Where a product genuinely lacks a concept, <see cref="AdminCapabilities"/>
/// says so rather than the implementation pretending.</para>
/// </summary>
public sealed record DatabaseSummary(
    string Name,
    string Owner,
    string Collation,
    long SizeBytes,
    int Connections,
    bool IsSystem);

/// <summary>
/// A login-capable principal: a PostgreSQL role, a SQL Server login. The panel calls it
/// whatever the selected server calls it; the wording lives in the browser.
/// </summary>
public sealed record RoleSummary(
    string Name,
    bool CanLogin,
    RoleAttributes Attributes,
    IReadOnlyList<string> MemberOf,
    bool IsSystem,
    // Whether this is a group to put users into rather than a user itself. PostgreSQL says
    // so by the role not being able to log in; SQL Server has a separate principal type, and
    // a disabled login there is still a user. Inferring it from CanLogin would get the
    // second case wrong, so each implementation answers for its own product.
    bool IsGroup);

/// <summary>
/// The server-wide powers a role can hold. SQL Server has no per-login flags for these, so
/// they map to its fixed server roles — <c>dbcreator</c>, <c>securityadmin</c>,
/// <c>sysadmin</c> — which is the same authority by a different mechanism.
/// </summary>
public sealed record RoleAttributes(
    bool CanLogin = true,
    bool CreateDb = false,
    bool CreateRole = false,
    bool Superuser = false);

/// <summary>
/// How much a role has on one database. Deliberately four steps rather than the real
/// privilege matrix: a panel that can express every GRANT is a worse tool than psql, and
/// the four below are what an operator actually reaches for.
/// </summary>
public enum PrivilegeLevel
{
    /// <summary>No access this tool granted. Revoking sets this.</summary>
    None,

    /// <summary>May connect, nothing more.</summary>
    Connect,

    /// <summary>May connect and read and write every table and sequence in it.</summary>
    ReadWrite,

    /// <summary>Owns the database and everything in it — may also alter and drop.</summary>
    Owner,
}

public sealed record DatabaseGrant(string Role, PrivilegeLevel Level);

/// <summary>
/// What the panel shows before it will drop a database. A confirmation that does not say
/// what is being lost is a confirmation nobody reads.
/// </summary>
public sealed record DatabaseDropPreview(
    string Name,
    string Owner,
    long SizeBytes,
    int Tables,
    long Rows,
    int Connections,
    bool IsSystem);

/// <summary>How much a role owns inside one database.</summary>
public sealed record OwnedObjects(string Database, int Objects);

/// <summary>
/// The same for a role, and the interesting half is why the drop will be refused: both
/// products decline to remove a principal that still owns something.
///
/// <para><see cref="Owns"/> is the databases it owns outright. <see cref="Dependencies"/> is
/// what it owns or holds privileges on <em>inside</em> each database — the part that is
/// invisible from any single connection, and the part that most often does the blocking.</para>
/// </summary>
public sealed record RoleDropPreview(
    string Name,
    IReadOnlyList<string> Owns,
    IReadOnlyList<OwnedObjects> Dependencies,
    bool IsSystem,
    bool IsCurrentUser);

/// <summary>
/// What the selected product can actually do. The panel hides a control rather than
/// offering one that quietly does nothing.
/// </summary>
public sealed record AdminCapabilities(
    // PostgreSQL grants CONNECT to PUBLIC by default, and it can be revoked.
    bool PublicConnect,
    // A database's collation can be chosen when it is created.
    bool Collation,
    // That collation is an ICU locale (PostgreSQL) rather than a product collation name.
    bool IcuCollation,
    // An existing database can be handed to another owner.
    bool ChangeOwner,
    // Other sessions can be closed to make a drop possible.
    bool CloseConnections,
    // Roles can be made members of other roles.
    bool Membership);
