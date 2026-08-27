namespace Migrator.Core;

/// <summary>
/// The tables an ORM keeps its own migration log in.
///
/// <para>These are the one kind of table whose contents belong to the <em>target</em> rather
/// than to the data being moved. Their rows say which migrations this database has had
/// applied, and that answer is specific to the provider the target runs on: a PostgreSQL
/// branch of an application has different migration IDs from the SQL Server branch it was
/// ported from. Copying the source's rows over the target's does not merge two histories, it
/// replaces a true statement with a false one — and the ORM believes it. Entity Framework
/// then finds no record of its baseline, re-applies it, and fails on tables that already
/// exist.</para>
///
/// <para>This is the same class of loss the tool already refuses elsewhere. A source table
/// with no target counterpart stops the run because data would be left behind; the target's
/// own migration history being truncated and overwritten is that loss in the other
/// direction, and it went unremarked.</para>
/// </summary>
public static class MigrationHistory
{
    /// <summary>
    /// Recognised by name because that is all these tables have in common — every ORM
    /// defines its own columns. Matched without case because SQL Server and PostgreSQL
    /// disagree about it and an operator should not have to.
    /// </summary>
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "__EFMigrationsHistory",   // Entity Framework Core
        "django_migrations",       // Django
        "schema_migrations",       // Rails, and several tools that copied it
        "flyway_schema_history",   // Flyway
        "__drizzle_migrations",    // Drizzle
    };

    public static bool IsHistoryTable(string table) => Names.Contains(table);

    public static List<string> In(IEnumerable<string> tables) =>
        tables.Where(IsHistoryTable).OrderBy(t => t, StringComparer.Ordinal).ToList();
}
