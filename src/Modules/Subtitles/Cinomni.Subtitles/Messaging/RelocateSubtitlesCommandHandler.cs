using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Subtitles.Messaging;

/// <summary>
/// Corrects the stored path of every sidecar that moved with its video (⇠ Import's
/// MediaFileRelocated). Import moves the files; this only catches the rows up with them.
/// </summary>
public sealed class RelocateSubtitlesCommandHandler(SubtitlesDbContext dbContext, IUnitOfWork unitOfWork)
    : ICommandHandler<RelocateSubtitlesCommand>
{
    public async Task<Result> HandleAsync(
        RelocateSubtitlesCommand command,
        CancellationToken cancellationToken = default)
    {
        var subtitles = await dbContext.Assets
            .Where(s => s.AssetId == command.AssetId)
            .ToListAsync(cancellationToken);

        // The file played and the original download are both sidecars of the video and move together
        // with it. One whose relationship to the video cannot be seen keeps its stored path.
        var moved = new List<(SubtitleAsset Subtitle, string To, string ToOriginal)>();
        foreach (var subtitle in subtitles)
        {
            var to = SidecarRelocation.For(command.FromVideoPath, command.ToVideoPath, subtitle.Path) ?? subtitle.Path;
            var toOriginal = SidecarRelocation.For(command.FromVideoPath, command.ToVideoPath, subtitle.OriginalPath)
                ?? subtitle.OriginalPath;
            if (!string.Equals(to, subtitle.Path, StringComparison.Ordinal)
                || !string.Equals(toOriginal, subtitle.OriginalPath, StringComparison.Ordinal))
            {
                moved.Add((subtitle, to, toOriginal));
            }
        }

        // Nothing to do is the ordinary case on a redelivery, and it must not open a transaction.
        if (moved.Count == 0)
        {
            return Result.Success();
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var (subtitle, to, toOriginal) in moved)
            {
                subtitle.Relocate(to, toOriginal);
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success();
    }
}
