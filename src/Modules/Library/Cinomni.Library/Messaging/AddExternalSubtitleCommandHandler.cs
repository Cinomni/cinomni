using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Library.Contracts;

namespace Cinomni.Library.Messaging;

/// <summary>Adds an external subtitle track to an asset (⇠ Subtitles' SubtitleAvailable).</summary>
public sealed class AddExternalSubtitleCommandHandler(ILibraryCommands commands)
    : ICommandHandler<AddExternalSubtitleCommand>
{
    public async Task<Result> HandleAsync(AddExternalSubtitleCommand command, CancellationToken cancellationToken = default)
    {
        await commands.AddExternalSubtitleAsync(command.AssetId, command.Language, command.Forced, cancellationToken);
        return Result.Success();
    }
}
