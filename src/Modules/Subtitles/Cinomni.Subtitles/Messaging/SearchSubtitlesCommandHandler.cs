using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Messaging;

/// <summary>Runs <see cref="SearchSubtitlesCommand"/> through the search service (⇠ MediaAssetRegistered).</summary>
public sealed class SearchSubtitlesCommandHandler(ISubtitleSearch search) : ICommandHandler<SearchSubtitlesCommand>
{
    public async Task<Result> HandleAsync(SearchSubtitlesCommand command, CancellationToken cancellationToken = default)
    {
        await search.SearchForAssetAsync(command.AssetId, cancellationToken);
        return Result.Success();
    }
}
