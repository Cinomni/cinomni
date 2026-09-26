using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Indexers.Definition;
using Cinomni.Discovery.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Discovery.Application;

/// <summary>
/// Answers <see cref="IReleaseFileSource"/>: finds the indexer that signs in and owns a download link —
/// by exact origin, the same rule every request of a definition lives under — and fetches the file with
/// its session. A link no such indexer owns is left to the caller's anonymous fetch, unchanged.
/// </summary>
internal sealed class ReleaseFileSource(
    DiscoveryDbContext dbContext,
    IndexerCredentialProtector credentials,
    DefinitionIndexerClient definitionClient) : IReleaseFileSource
{
    public async Task<ReleaseFile> FetchAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        // Only https: a session cookie never travels over cleartext, and every sign-in already refuses it.
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
        {
            return ReleaseFile.NotHandled;
        }

        // A handful of rows at most, matched in memory: origin equality is not something to hand to SQL
        // as string prefixes, where "https://site.example" would also match "https://site.example.evil".
        // A disabled indexer lends its session to nothing: disabling one is how an operator stops
        // Cinomni acting on that account.
        var candidates = await dbContext.Indexers
            .AsNoTracking()
            .Where(i => i.Enabled && i.Protocol == IndexerProtocol.Definition && i.DefinitionId != null && i.SecretCipher != null)
            .OrderBy(i => i.Priority)
            .ToListAsync(cancellationToken);

        var owner = candidates.FirstOrDefault(i =>
            Uri.TryCreate(i.BaseUrl, UriKind.Absolute, out var baseUri) && DefinitionQueryBuilder.SameOrigin(baseUri, link));
        if (owner is null || !await CameFromOwnerAsync(owner, downloadUrl, cancellationToken))
        {
            return ReleaseFile.NotHandled;
        }

        var definition = await dbContext.IndexerDefinitions
            .AsNoTracking()
            .Where(d => d.Id == owner.DefinitionId)
            .Select(d => d.RawContent)
            .SingleOrDefaultAsync(cancellationToken);

        // An owner without a readable credential cannot sign in, so the anonymous fetch it would get
        // anyway is the honest answer — the same degradation its searches have.
        if (definition is null || credentials.TryDecrypt(owner) is not { } secret)
        {
            return ReleaseFile.NotHandled;
        }

        var summary = new IndexerSummary(
            new IndexerId(owner.Id), owner.Name, owner.Protocol, owner.BaseUrl, owner.Priority, owner.Enabled,
            DefinitionId: new IndexerDefinitionId(owner.DefinitionId!.Value), Settings: owner.ToSettings());
        return await definitionClient.FetchReleaseFileAsync(
            summary, new IndexerCredential(owner.CredentialUsername, secret), definition, link, cancellationToken);
    }

    /// <summary>
    /// Whether the owner itself returned this exact link from a search. Any indexer can return a link
    /// into a private tracker's origin; fetched with the member's session, that would be a download
    /// charged to the member that the tracker never offered, or any state-changing GET the other
    /// indexer chose. Only the owner's own results carry its session. Results are attributed by the
    /// id of the indexer that returned them, never by its name, which the operator can rename or give
    /// to another indexer. A link that fails the check is left to the anonymous fetch, which a private
    /// tracker refuses — failing closed, with no session sent.
    /// </summary>
    private Task<bool> CameFromOwnerAsync(Indexer owner, string downloadUrl, CancellationToken cancellationToken) =>
        dbContext.SearchResults
            .AsNoTracking()
            .AnyAsync(r => r.IndexerId == owner.Id && r.DownloadUrl == downloadUrl, cancellationToken);
}
