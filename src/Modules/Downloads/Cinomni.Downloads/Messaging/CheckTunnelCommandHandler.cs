using Cinomni.Downloads.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Downloads.Messaging;

/// <summary>Runs the periodic <see cref="CheckTunnelCommand"/> (scheduler-driven).</summary>
public sealed class CheckTunnelCommandHandler(TunnelWatchService watch) : ICommandHandler<CheckTunnelCommand>
{
    public async Task<Result> HandleAsync(CheckTunnelCommand command, CancellationToken cancellationToken = default)
    {
        await watch.CheckAsync(cancellationToken);
        return Result.Success();
    }
}
