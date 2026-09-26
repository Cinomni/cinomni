using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Subtitles.Application;

namespace Cinomni.Subtitles.Messaging;

/// <summary>Runs <see cref="CatchUpSubtitlesCommand"/> through <see cref="SubtitleCatchUp"/>.</summary>
public sealed class CatchUpSubtitlesCommandHandler(SubtitleCatchUp catchUp) : ICommandHandler<CatchUpSubtitlesCommand>
{
    public async Task<Result> HandleAsync(CatchUpSubtitlesCommand command, CancellationToken cancellationToken = default)
    {
        await catchUp.EnqueueAsync(cancellationToken);
        return Result.Success();
    }
}
