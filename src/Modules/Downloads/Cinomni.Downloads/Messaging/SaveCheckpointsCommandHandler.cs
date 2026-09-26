using Cinomni.Downloads.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Downloads.Messaging;

/// <summary>Runs the periodic <see cref="SaveCheckpointsCommand"/> (scheduler-driven).</summary>
public sealed class SaveCheckpointsCommandHandler(DownloadService service) : ICommandHandler<SaveCheckpointsCommand>
{
    public async Task<Result> HandleAsync(SaveCheckpointsCommand command, CancellationToken cancellationToken = default)
    {
        await service.SaveCheckpointsAsync(cancellationToken);
        return Result.Success();
    }
}
