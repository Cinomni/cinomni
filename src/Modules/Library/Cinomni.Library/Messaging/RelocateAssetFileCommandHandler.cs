using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Library.Contracts;

namespace Cinomni.Library.Messaging;

/// <summary>Points a version at the path its file was moved to (⇠ Import's MediaFileRelocated).</summary>
public sealed class RelocateAssetFileCommandHandler(ILibraryCommands commands)
    : ICommandHandler<RelocateAssetFileCommand>
{
    public async Task<Result> HandleAsync(
        RelocateAssetFileCommand command,
        CancellationToken cancellationToken = default)
    {
        await commands.RelocateAssetFileAsync(command.AssetId, command.FromPath, command.ToPath, cancellationToken);
        return Result.Success();
    }
}
