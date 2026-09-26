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

        var moved = new List<(SubtitleAsset Subtitle, string To)>();
        foreach (var subtitle in subtitles)
        {
            var to = SidecarRelocation.For(command.FromVideoPath, command.ToVideoPath, subtitle.Path);
            if (to is not null && !string.Equals(to, subtitle.Path, StringComparison.Ordinal))
            {
                moved.Add((subtitle, to));
            }
        }

        // Nothing to do is the ordinary case on a redelivery, and it must not open a transaction.
        if (moved.Count == 0)
        {
            return Result.Success();
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var (subtitle, to) in moved)
            {
                subtitle.Relocate(to);
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return Result.Success();
    }
}
