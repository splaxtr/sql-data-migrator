namespace Migrator.App;

/// <summary>
/// Identifiers for the messages this application adds around the engine's own — the batch
/// it is working through and the report it produces at the end.
///
/// <para>Kept apart from <see cref="Migrator.Core.MessageCode"/> on purpose: that class is
/// the engine's vocabulary and a batch is not something the engine knows about. Both are
/// wire formats in the same sense — the browser translates by code, so a rename silently
/// drops a translation.</para>
/// </summary>
internal static class AppMessageCode
{
    public const string StepBatchDatabase = "step.batchDatabase";
    public const string InfoBatchSummary = "info.batchSummary";
    public const string InfoReportReady = "info.reportReady";
    public const string WarnReportFailed = "warn.reportFailed";
    public const string ErrorServerNotFound = "error.serverNotFound";
    public const string SuccessBatchAll = "success.batchAll";
    public const string FailBatchPartial = "fail.batchPartial";
}
