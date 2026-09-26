namespace Cinomni.Operations.Settings;

/// <summary>
/// The write path: validates a batch of <see cref="SettingChange"/>s against
/// the running installation, commits every change in one transaction with per-key optimistic concurrency
/// and an audit row, and — only after the commit — publishes the new snapshot so the running
/// configuration is never touched by a rejected batch.
/// </summary>
public interface ISettingsAdministration
{
    /// <param name="changes">The batch. Empty succeeds trivially, writing nothing.</param>
    /// <param name="actorId">The Identity account making the change, carried as an opaque id.</param>
    /// <param name="cancellationToken">Cancels before the transaction opens; not honoured mid-commit.</param>
    Task<SettingsApplyResult> ApplyAsync(
        IReadOnlyList<SettingChange> changes, Guid actorId, CancellationToken cancellationToken = default);
}
