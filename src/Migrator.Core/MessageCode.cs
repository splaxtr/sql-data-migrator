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

    /// <summary>
    /// A NOT NULL target column with no source counterpart, filled with a made-up value.
    /// The run continues, but a fabricated zero is indistinguishable from a real one
    /// afterwards, so it is never allowed to pass unmentioned.
    /// </summary>
    public const string WarnColumnSynthesized = "warn.columnSynthesized";

    /// <summary>A source column with no target counterpart: its data does not travel.</summary>
    public const string WarnSourceColumnDropped = "warn.sourceColumnDropped";

    public const string WarnSourceOnlyTable = "warn.sourceOnlyTable";

    /// <summary>Base tables the source holds outside <c>dbo</c>, which this tool does not read.</summary>
    public const string WarnSourceSchemaSkipped = "warn.sourceSchemaSkipped";
    public const string ErrorSourceSchemaSkipped = "error.sourceSchemaSkipped";

    /// <summary>
    /// What TRUNCATE CASCADE is going to empty, worked out before the transaction opens
    /// rather than read back from PostgreSQL's notices once the locks are held.
    /// </summary>
    public const string WarnCascadePreview = "warn.cascadePreview";

    // ── ORM migration history ─────────────────────────────────────────────────
    public const string InfoHistoryPreserved = "info.historyPreserved";
    public const string InfoHistorySourceOnly = "info.historySourceOnly";
    public const string WarnHistoryCopied = "warn.historyCopied";
    public const string WarnMirrorNoHistory = "warn.mirrorNoHistory";
    public const string WarnMirrorOrmManaged = "warn.mirrorOrmManaged";

    /// <summary>
    /// A preserved history table sits inside the TRUNCATE CASCADE closure, so keeping it out
    /// of the copy plan does not keep its rows. Fatal: the promise cannot be honoured, and a
    /// run that continued would be reporting a guarantee it did not deliver.
    /// </summary>
    public const string ErrorHistoryCascade = "error.historyCascade";

    /// <summary>A mirrored column that carries no meaning off its own server.</summary>
    public const string WarnMirrorSkippedRowVersion = "warn.mirrorSkippedRowVersion";
    public const string FailHistoryCascade = "fail.historyCascade";
    public const string ErrorSourceOnlyTable = "error.sourceOnlyTable";

    // ── Mirror ───────────────────────────────────────────────────────────────
    public const string StepMirroring = "step.mirroring";
    public const string InfoMirrorPlan = "info.mirrorPlan";
    public const string InfoTableCreated = "info.tableCreated";
    public const string InfoMirrorForeignKeys = "info.mirrorForeignKeys";
    public const string WarnMirrorFkSkipped = "warn.mirrorFkSkipped";
    public const string WarnMirrorFkFailed = "warn.mirrorFkFailed";
    public const string ErrorMirrorUnsupportedType = "error.mirrorUnsupportedType";

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

    // ── Database user ────────────────────────────────────────────────────────
    public const string StepCreatingUser = "step.creatingUser";
    public const string SuccessUserCreated = "success.userCreated";
    public const string WarnUserExists = "warn.userExists";
    public const string InfoUserPrivileges = "info.userPrivileges";
    public const string InfoDatabaseIsolated = "info.databaseIsolated";
    public const string WarnUserOwnership = "warn.userOwnership";
    public const string ErrorUserFailed = "error.userFailed";

    // ── PostgreSQL notices ───────────────────────────────────────────────────
    public const string InfoPostgresNotice = "info.postgresNotice";

    // ── Outcomes ─────────────────────────────────────────────────────────────
    public const string SuccessMigrated = "success.migrated";
    public const string SuccessVerifyPassed = "success.verifyPassed";
    public const string FailCollationMismatch = "fail.collationMismatch";
    public const string FailSchemaMismatch = "fail.schemaMismatch";
    public const string FailMirrorFailed = "fail.mirrorFailed";
    public const string FailEmptyIntersection = "fail.emptyIntersection";
    public const string FailVerifyFailed = "fail.verifyFailed";
    public const string FailPreflightUnresolved = "fail.preflightUnresolved";
    public const string FailZeroRows = "fail.zeroRows";
    public const string FailTargetDbNotReady = "fail.targetDbNotReady";
    public const string FailException = "fail.exception";

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
