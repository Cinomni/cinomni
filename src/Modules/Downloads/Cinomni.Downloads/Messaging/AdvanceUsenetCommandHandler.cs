using Cinomni.Downloads.Usenet;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;

namespace Cinomni.Downloads.Messaging;

/// <summary>Runs one tick of the Usenet downloader. Idempotent: a second tick skips articles already on disk.</summary>
public sealed class AdvanceUsenetCommandHandler(UsenetDownloadService usenet) : ICommandHandler<AdvanceUsenetCommand>
{
    public async Task<Result> HandleAsync(AdvanceUsenetCommand command, CancellationToken cancellationToken = default)
    {
        await usenet.AdvanceAsync(cancellationToken);
        return Result.Success();
    }
}
