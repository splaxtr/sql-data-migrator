namespace Migrator.Core;

/// <summary>
/// Stable identifiers for every message the engine produces.
///
/// <para>These are part of the public surface. A presentation layer translates by code, so
/// renaming one silently drops a translation back to English — treat a code the way you would
/// treat a wire format, and add a new one rather than repurposing an old one.</para>
///
/// <para>The <c>{0}</c>, <c>{1}</c> … placeholders in the English text of each message
/// correspond to the <c>Args</c> of the <see cref="ProgressMessage"/> carrying that code, in
/// order. Translations may reorder them; they may not invent new ones.</para>
/// </summary>
public static class MessageCode
{
    // ── Steps ────────────────────────────────────────────────────────────────
    public const string StepReadingSchemas = "step.readingSchemas";
    public const string StepPreflight = "step.preflight";
    public const string StepCopying = "step.copying";
    public const string StepVerifyRowCounts = "step.verifyRowCounts";
    public const string StepVerifyForeignKeys = "step.verifyForeignKeys";

    // ── Plan ─────────────────────────────────────────────────────────────────
    public const string InfoTablesToMigrate = "info.tablesToMigrate";
    public const string ErrorNoTablesToCopy = "error.noTablesToCopy";
    public const string ErrorColumnNotSynthesizable = "error.columnNotSynthesizable";
    public const string WarnSourceOnlyTable = "warn.sourceOnlyTable";
    public const string ErrorSourceOnlyTable = "error.sourceOnlyTable";

    // ── Preflight ────────────────────────────────────────────────────────────
    public const string InfoPreflightClean = "info.preflightClean";
    public const string ErrorPreflightNulls = "error.preflightNulls";
    public const string ErrorPreflightLength = "error.preflightLength";
    public const string WarnPreflightAllowed = "warn.preflightAllowed";

    // ── Copy ─────────────────────────────────────────────────────────────────
    public const string InfoTableCopied = "info.tableCopied";
    public const string InfoCopyFinished = "info.copyFinished";
    public const string InfoSequencesAligned = "info.sequencesAligned";
    public const string WarnTruncateCascade = "warn.truncateCascade";
    public const string WarnTruncateCascadeMore = "warn.truncateCascadeMore";
    public const string ErrorZeroRows = "error.zeroRows";

    // ── Verification ─────────────────────────────────────────────────────────
    public const string InfoRowCountsMatch = "info.rowCountsMatch";
    public const string ErrorRowCountMismatch = "error.rowCountMismatch";
    public const string InfoForeignKeysClean = "info.foreignKeysClean";
    public const string ErrorOrphanRows = "error.orphanRows";
    public const string ErrorVerifyFailedRollback = "error.verifyFailedRollback";

    // ── Target database ──────────────────────────────────────────────────────
    public const string ErrorTargetDbNameMissing = "error.targetDbNameMissing";
    public const string InfoTargetDbExists = "info.targetDbExists";
    public const string SuccessTargetDbCreated = "success.targetDbCreated";
    public const string SuccessTargetDbCreatedCollation = "success.targetDbCreatedCollation";
    public const string InfoCollationVerified = "info.collationVerified";
    public const string WarnCollationMismatchAllowed = "warn.collationMismatchAllowed";
    public const string ErrorCollationMismatch = "error.collationMismatch";

    // ── PostgreSQL notices ───────────────────────────────────────────────────
    public const string InfoPostgresNotice = "info.postgresNotice";

    // ── Outcomes ─────────────────────────────────────────────────────────────
    public const string SuccessMigrated = "success.migrated";
    public const string SuccessVerifyPassed = "success.verifyPassed";
    public const string FailCollationMismatch = "fail.collationMismatch";
    public const string FailSchemaMismatch = "fail.schemaMismatch";
    public const string FailEmptyIntersection = "fail.emptyIntersection";
    public const string FailVerifyFailed = "fail.verifyFailed";
    public const string FailPreflightUnresolved = "fail.preflightUnresolved";
    public const string FailZeroRows = "fail.zeroRows";

    /// <summary>
    /// Values a translator has to be able to reach even though they arrive as an argument
    /// rather than as a message of their own. The double <c>@</c> marks them: no ICU locale or
    /// PostgreSQL collation name begins with one, so a presentation layer can translate these
    /// on sight without risking a collision with real data.
    /// </summary>
    public const string TokenUnknown = "@@unknown";

    /// <inheritdoc cref="TokenUnknown"/>
    public const string TokenUnreadable = "@@unreadable";
}
