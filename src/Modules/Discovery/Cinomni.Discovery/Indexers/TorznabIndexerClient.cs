using System.Net.Http.Headers;
using System.Text;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Application;
using Cinomni.Search.Contracts;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// Production <see cref="IIndexerClient"/>: queries a Torznab/Newznab endpoint over HTTP and
/// parses the feed. The transport is deliberately thin — the SSRF guard, redirect ban, timeout
/// and response-size cap live on the <see cref="HttpClient"/>'s handler (configured in
/// <c>DiscoveryModule</c>); the two error-prone parts are the pure
/// <see cref="TorznabQueryBuilder"/> and <see cref="TorznabFeedParser"/>.
/// An indexer with a stored API key sends it as <c>apikey</c>; one with a username and password sends
/// them as HTTP Basic; one without a credential is queried exactly as it always was, because a public
/// endpoint needs no key and a private one answers with its own error.
/// </summary>
internal sealed class TorznabIndexerClient(HttpClient httpClient, IIndexerQuota? quota = null) : IIndexerClient
{
    /// <param name="definitionContent">
    /// Ignored: a Torznab/Newznab endpoint is described by its own API, never by a declarative
    /// definition. It is on the signature because the port is shared with the definition adapter.
    /// </param>
    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        IndexerSummary indexer,
        IndexerCredential? credential,
        string? definitionContent,
        SearchCriterion criterion,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, TorznabQueryBuilder.BuildRequestUrl(indexer, criterion, credential));
        if (credential is not null && TorznabQueryBuilder.IsBasicAuth(credential))
        {
            // Basic is the password in reversible encoding: over plain HTTP it is the password in
            // clear, and an account password — unlike an indexer API key — is usually reused
            // elsewhere. Storing one is refused for an http base URL; this holds the line for a row
            // that predates that check.
            if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    "A username and password are only sent to an https indexer URL.");
            }

            request.Headers.Authorization = BasicAuthorization(credential);
        }

        if (indexer.Id.Value != Guid.Empty && quota is not null && !await quota.TryConsumeAsync(
                indexer.Id.Value, IndexerRequestKind.Query, indexer.Settings?.QueryLimit, cancellationToken))
        {
            throw new IndexerQuotaExceededException(IndexerRequestKind.Query);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);

        var defaultProtocol = indexer.Protocol == IndexerProtocol.Newznab
            ? ReleaseProtocol.Usenet
            : ReleaseProtocol.Torrent;

        return TorznabFeedParser.Parse(xml, indexer.Name, defaultProtocol, indexer.Settings?.PreferMagnet == true);
    }

    /// <summary>RFC 7617: <c>base64(user-id ":" password)</c> in UTF-8.</summary>
    internal static AuthenticationHeaderValue BasicAuthorization(IndexerCredential credential) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Secret}")));
}
