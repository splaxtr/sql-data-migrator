namespace Migrator.App.Reporting;

/// <summary>
/// What happened to one database. The text fields are Turkish and ready to print: the PDF
/// is the end of the line, with no presentation layer after it to translate anything.
/// </summary>
public sealed record DatabaseOutcome(
    string SourceDatabase,
    string TargetDatabase,
    bool Succeeded,
    long RowsCopied,
    TimeSpan Duration,
    string Note,
    string? UserName = null,
    string? Password = null,
    bool UserCreated = false,
    string? UserNote = null);

/// <summary>Everything a completed batch has to say about itself.</summary>
public sealed record MigrationReport(
    DateTimeOffset CompletedAt,
    string SourceServer,
    string TargetServer,
    bool VerifyOnly,
    IReadOnlyList<DatabaseOutcome> Databases)
{
    public int SucceededCount => Databases.Count(d => d.Succeeded);
    public int FailedCount => Databases.Count - SucceededCount;
    public long TotalRows => Databases.Sum(d => d.RowsCopied);
    public bool HasUsers => Databases.Any(d => d.UserName is not null);
}
