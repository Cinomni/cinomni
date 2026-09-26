using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Messaging;

/// <summary>
/// Marks one episode available on a completed import (⇠ Import's <c>MediaAvailable</c>, one command per
/// catalog unit the asset covers). An unknown or already-available episode is a no-op, which is what makes
/// a redelivered season-pack fan-out safe.
/// </summary>
public sealed class MarkEpisodeAvailableCommandHandler(ICatalogCommands commands)
    : ICommandHandler<MarkEpisodeAvailableCommand>
{
    public async Task<Result> HandleAsync(
        MarkEpisodeAvailableCommand command,
        CancellationToken cancellationToken = default)
    {
        await commands.MarkEpisodeAvailableAsync(
            command.EpisodeId, command.AssetId, command.TargetId, cancellationToken);
        return Result.Success();
    }
}
