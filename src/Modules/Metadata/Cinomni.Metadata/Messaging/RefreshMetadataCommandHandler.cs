using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Messaging;

/// <summary>Runs <see cref="RefreshMetadataCommand"/> through the refresh service.</summary>
public sealed class RefreshMetadataCommandHandler(IMetadataRefresh refresh) : ICommandHandler<RefreshMetadataCommand>
{
    public async Task<Result> HandleAsync(RefreshMetadataCommand command, CancellationToken cancellationToken = default)
    {
        await refresh.RefreshAsync(command.WorkId, command.Provider, command.ExternalId, command.Kind, cancellationToken);
        return Result.Success();
    }
}
