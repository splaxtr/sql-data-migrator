namespace Migrator.App;

/// <summary>
/// What a run has been asked to do.
///
/// <para>One value rather than a pair of booleans: the modes are mutually exclusive, and a
/// request that can say "verify only" and "provision only" at the same time is a request
/// something has to guess at. The engine still receives its own
/// <see cref="Migrator.Core.MigrationOptions.VerifyOnly"/> flag — it has no notion of a
/// batch, and provisioning happens entirely outside it.</para>
/// </summary>
public enum RunMode
{
    /// <summary>Prepare the target database, copy, verify, commit. The default.</summary>
    Migrate,

    /// <summary>
    /// Compare an existing target against the source and write nothing at all — no
    /// database is created, no table is truncated, no role is provisioned.
    /// </summary>
    VerifyOnly,

    /// <summary>
    /// Create the target database, and its role when that option is on, then stop. No
    /// table is read from the source and none is written in the target.
    /// </summary>
    ProvisionOnly,
}
