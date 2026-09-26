using Cinomni.Import.Application;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Messaging;

/// <summary>
/// Runs one library-path repair pass (⇠ an administrator's request). The outcome is logged as counts
/// rather than paths: this log line is exported, and a library path names what a household owns.
/// </summary>
public sealed class RepairLibraryPathsCommandHandler(
    LibraryPathRepair repair,
    ILogger<RepairLibraryPathsCommandHandler> logger)
    : ICommandHandler<RepairLibraryPathsCommand>
{
    public async Task<Result> HandleAsync(
        RepairLibraryPathsCommand command,
        CancellationToken cancellationToken = default)
    {
        var report = await repair.RepairAsync(cancellationToken);

        logger.LogInformation(
            "Library path repair {RunId} finished: {Repaired} repaired, {Announced} announced after the fact, "
            + "{Blocked} blocked, {Absent} absent.",
            command.RunId,
            report.Entries.Count(e => e.Outcome is PathRepairOutcome.Repaired),
            report.Entries.Count(e => e.Outcome is PathRepairOutcome.Announced),
            report.Entries.Count(e => e.Outcome is PathRepairOutcome.Blocked),
            report.Entries.Count(e => e.Outcome is PathRepairOutcome.Absent));

        return Result.Success();
    }
}
