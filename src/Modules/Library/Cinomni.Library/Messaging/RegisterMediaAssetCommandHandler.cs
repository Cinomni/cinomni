using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Library.Contracts;

namespace Cinomni.Library.Messaging;

/// <summary>Runs <see cref="RegisterMediaAssetCommand"/> through the library service (⇠ MediaAvailable).</summary>
public sealed class RegisterMediaAssetCommandHandler(ILibraryCommands commands)
    : ICommandHandler<RegisterMediaAssetCommand>
{
    public async Task<Result> HandleAsync(RegisterMediaAssetCommand command, CancellationToken cancellationToken = default)
    {
        await commands.RegisterMediaAssetAsync(
            new RegisterMediaAssetRequest(
                command.AssetId,
                command.WorkId,
                command.TargetIds,
                command.FullPath,
                command.Size,
                command.Container,
                command.Streams,
                command.UnitIds,
                command.Quality,
                command.DurationSeconds,
                command.Bitrate),
            cancellationToken);
        return Result.Success();
    }
}

/// <summary>Runs <see cref="LinkAssetUnitsCommand"/> through the library service.</summary>
public sealed class LinkAssetUnitsCommandHandler(ILibraryCommands commands)
    : ICommandHandler<LinkAssetUnitsCommand>
{
    public async Task<Result> HandleAsync(LinkAssetUnitsCommand command, CancellationToken cancellationToken = default)
    {
        await commands.LinkAssetUnitsAsync(command.AssetId, command.UnitIds, cancellationToken);
        return Result.Success();
    }
}
