using Cinomni.Decision.Contracts;
using Cinomni.Discovery.Contracts;

namespace Cinomni.Decision.Application;

/// <summary>What a selection says about the release it chose, taken from the candidate as the indexer described it.</summary>
internal static class ReleaseInfo
{
    public static SelectedReleaseInfo Of(ReleaseCandidate candidate) =>
        new(candidate.Title, candidate.IndexerName, candidate.Seeders, candidate.Leechers);
}
