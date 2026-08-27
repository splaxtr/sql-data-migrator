namespace Migrator.Core;

/// <summary>What the migration needs to know about a single column.</summary>
public sealed record ColumnInfo(
    string Name,
    string StoreType,
    bool IsNullable,
    bool IsIdentity,
    bool HasDefault,
    int? MaxLength,
    int? Precision = null,
    int? Scale = null);

/// <summary>The copy plan for a single table.</summary>
public sealed record TablePlan(
    string Table,
    List<(ColumnInfo Source, ColumnInfo Target)> CopyColumns,
    List<ColumnInfo> SynthesizedColumns);

public sealed record ForeignKey(
    string Name,
    string ChildTable,
    string ParentTable,
    List<(string Child, string Parent)> Columns,
    string? DeleteAction = null);

public enum ProgressKind { Info, Warning, Error, Success, Step }

/// <summary>
/// One line of progress.
///
/// <para><see cref="Text"/> is always English and is the only field a consumer has to
/// understand. <see cref="Code"/> and <see cref="Args"/> are the same message expressed as
/// data: a stable identifier plus the values interpolated into it. A presentation layer that
/// wants another language translates by code and falls back to <see cref="Text"/> when it has
/// no translation, which is why the engine stays free of any notion of locale — it reports
/// what happened, not how to word it.</para>
///
/// <para>Codes live in <see cref="MessageCode"/>. A message with no code is one whose text
/// came from outside the engine, such as a driver exception.</para>
/// </summary>
public sealed record ProgressMessage(
    ProgressKind Kind,
    string Text,
    string? Code = null,
    object?[]? Args = null);

/// <summary>The gates that can be deliberately relaxed. None of them defaults to open.</summary>
public sealed class MigrationOptions
{
    /// <summary>Ignores source tables with no target counterpart (their data is not migrated).</summary>
    public bool AllowSourceOnlyTables { get; init; }

    /// <summary>
    /// Creates source-only tables in the target from the source schema (mirror mode):
    /// columns, NOT NULL, identity, primary keys and foreign keys. Defaults, indexes and
    /// check constraints are not copied. Ignored when <see cref="VerifyOnly"/> is set —
    /// a verification must not mutate the target.
    /// </summary>
    public bool MirrorMissingTables { get; init; }

    /// <summary>Proceeds despite the NULL/length mismatches the preflight found.</summary>
    public bool AllowSchemaRisk { get; init; }

    /// <summary>Proceeds when the target collation differs from the expected one.</summary>
    public bool AllowCollationMismatch { get; init; }

    /// <summary>Verifies only; moves no data.</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>Expected ICU collation; when empty, no collation check is performed.</summary>
    public string? ExpectedIcuLocale { get; init; }

    /// <summary>
    /// Copies the source's ORM migration-history tables over the target's own.
    ///
    /// <para>Off by default, and it is the one option here that defaults to <em>not</em>
    /// doing something rather than to refusing something. The others are gates that let
    /// questionable data through; this one overwrites correct target state with a false
    /// answer — see <see cref="MigrationHistory"/>. Turn it on for a byte-for-byte copy of a
    /// database whose target is not managed by an ORM.</para>
    /// </summary>
    public bool MigrateHistoryTables { get; init; }
}

/// <summary>
/// The outcome of a run. <see cref="Summary"/> is English; <see cref="Code"/> and
/// <see cref="Args"/> carry the same statement as data, on the same terms as
/// <see cref="ProgressMessage"/>.
/// </summary>
public sealed record MigrationResult(
    bool Succeeded,
    long RowsCopied,
    TimeSpan Duration,
    string Summary,
    string? Code = null,
    object?[]? Args = null);
