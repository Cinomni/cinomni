using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Monitoring.Application;

namespace Cinomni.Monitoring.Messaging;

/// <summary>Runs <see cref="ApplyMonitoringPolicyCommand"/> through the module's command service.</summary>
public sealed class ApplyMonitoringPolicyCommandHandler(MonitoringCommands commands)
    : ICommandHandler<ApplyMonitoringPolicyCommand>
{
    public async Task<Result> HandleAsync(
        ApplyMonitoringPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        var workId = new WorkId(command.WorkId);
        var result = command.InitialOnly
            ? await commands.ApplyInitialPolicyAsync(workId, command.Mode, cancellationToken)
            : await commands.ApplyMonitoringPolicyAsync(workId, command.Mode, cancellationToken);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }
}
