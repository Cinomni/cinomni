using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Catalog.Messaging;

/// <summary>Runs <see cref="UpdateWorkArtworkCommand"/> through the catalog commands (⇠ Metadata's artwork override).</summary>
public sealed class UpdateWorkArtworkCommandHandler(ICatalogCommands commands) : ICommandHandler<UpdateWorkArtworkCommand>
{
    public async Task<Result> HandleAsync(UpdateWorkArtworkCommand command, CancellationToken cancellationToken = default)
    {
        await commands.UpdateWorkArtworkAsync(command.WorkId, command.PosterUrl, command.BackdropUrl, cancellationToken);
        return Result.Success();
    }
}
