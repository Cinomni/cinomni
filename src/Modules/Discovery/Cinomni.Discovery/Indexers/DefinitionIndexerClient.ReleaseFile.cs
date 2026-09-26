using System.Net;
using System.Text;
using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Diagnostics;
using Cinomni.Discovery.Indexers.Definition;

namespace Cinomni.Discovery.Indexers;

/// <summary>
/// The .torrent behind a release link of an indexer that signs in, fetched as that member — with the
/// kept session, and behind the clearance when the site sits behind a browser challenge. The session
/// renews once when the site says it expired, exactly as a search does, and never more.
/// </summary>
internal sealed partial class DefinitionIndexerClient
{
    /// <summary>Far above any real .torrent (a large season pack is a few hundred kilobytes).</summary>
    internal const int MaxReleaseFileBytes = 8 * 1024 * 1024;

    private static readonly byte[] InfoDictionaryKey = "4:infod"u8.ToArray();

    internal async Task<ReleaseFile> FetchReleaseFileAsync(
        IndexerSummary indexer,
        IndexerCredential credential,
        string definitionContent,
        Uri link,
        CancellationToken cancellationToken)
    {
        var parsed = IndexerDefinitionParser.Parse(definitionContent);
        if (parsed.IsFailure)
        {
            throw new InvalidOperationException($"Indexer '{indexer.Name}' has an invalid definition ({parsed.Error.Code}).");
        }

        var baseUri = new Uri(indexer.BaseUrl, UriKind.Absolute);
        if (parsed.Value.Session is not { } declaredSession || !DefinitionQueryBuilder.SameOrigin(baseUri, link))
        {
            return ReleaseFile.NotHandled;
        }

        if (sessionManager is null)
        {
            throw new InvalidOperationException("Indexer session support is unavailable.");
        }

        // A release file is what the grab quota exists to count.
        if (quota is not null && !await quota.TryConsumeAsync(
                indexer.Id.Value, IndexerRequestKind.Grab, indexer.Settings?.GrabLimit, cancellationToken))
        {
            throw new IndexerQuotaExceededException(IndexerRequestKind.Grab);
        }

        var cleared = RunsCleared(indexer, declaredSession, credential);
        async Task<IReadOnlyList<JarCookie>> SignedInAsync()
        {
            var jar = cleared
                ? await EnsureClearedSessionAsync(indexer, baseUri, declaredSession, credential, cancellationToken)
                : await sessionManager.EnsureSessionAsync(
                    indexer.Id.Value, indexer.Name, baseUri, declaredSession, credential,
                    cancellationToken: cancellationToken);
            return jar ?? throw new InvalidOperationException(
                $"Indexer '{indexer.Name}' could not sign in to fetch a release file.");
        }

        var session = await SignedInAsync();
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendForFileAsync(indexer, baseUri, link, session, cleared, cancellationToken);

            if (IsRedirect(response.Status) && response.Location is { } location
                && location.OriginalString.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            {
                await sessionManager.ConfirmAsync(indexer.Id.Value, session, cancellationToken);
                return new ReleaseFile(ReleaseFileOutcome.Magnet, Magnet: location.OriginalString);
            }

            if ((int)response.Status is >= 200 and < 300 && IsTorrentFile(response.Content))
            {
                await sessionManager.ConfirmAsync(indexer.Id.Value, session, cancellationToken);
                return new ReleaseFile(ReleaseFileOutcome.TorrentFile, response.Content);
            }

            var loggedOut = ResponseSaysLoggedOut(
                new SessionedResponse(response.Status, Decode(response.Content), response.Location),
                declaredSession.Check, baseUri, declaredSession);
            if (loggedOut && attempt == 0)
            {
                await sessionManager.ReportRejectedAsync(indexer.Id.Value, indexer.Name, session, cancellationToken);
                DiscoveryMetrics.RecordRelogin(indexer.Name);
                session = await SignedInAsync();
                continue;
            }

            // Never the link: on a private tracker it identifies the member's account.
            throw new HttpRequestException(
                $"Indexer '{indexer.Name}' did not answer with a release file ({(int)response.Status}).",
                null,
                response.Status);
        }
    }

    private readonly record struct FileResponse(HttpStatusCode Status, byte[] Content, Uri? Location);

    /// <summary>One request for the file; behind a challenge, renewed and retried once like any cleared request.</summary>
    private async Task<FileResponse> SendForFileAsync(
        IndexerSummary indexer,
        Uri baseUri,
        Uri link,
        IReadOnlyList<JarCookie> session,
        bool cleared,
        CancellationToken cancellationToken)
    {
        if (!cleared)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            if (IndexerSessionCookies.HeaderFor(session, link) is { } header)
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            return new FileResponse(response.StatusCode, await ReadBoundedAsync(response, cancellationToken), response.Headers.Location);
        }

        for (var attempt = 0; ; attempt++)
        {
            var current = await clearance!.GetAsync(indexer.Id.Value, link, cancellationToken);
            var http = httpClientFactory!.CreateClient(IndexerClearanceCache.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            BrowserChallenge.Apply(request, current, session);

            using var response = await http.SendAsync(request, cancellationToken);
            var content = await ReadBoundedAsync(response, cancellationToken);
            if (!BrowserChallenge.IsChallenge(response, Decode(content)))
            {
                return new FileResponse(response.StatusCode, content, response.Headers.Location);
            }

            clearance.Invalidate(indexer.Id.Value, current);
            if (attempt == 1)
            {
                throw new IndexerChallengeException($"Indexer {indexer.Name} kept answering with a browser challenge.");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxReleaseFileBytes)
        {
            throw new InvalidDataException("The release file exceeded the allowed size.");
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return content.Length > MaxReleaseFileBytes
            ? throw new InvalidDataException("The release file exceeded the allowed size.")
            : content;
    }

    /// <summary>
    /// A bencoded dictionary with an info dictionary in it: the shape of a .torrent, and not of the
    /// login page, error page or challenge a site answers with when it will not serve the file.
    /// </summary>
    internal static bool IsTorrentFile(byte[] content) =>
        content.Length > InfoDictionaryKey.Length
        && content[0] == (byte)'d'
        && content.AsSpan().IndexOf(InfoDictionaryKey) > 0;

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static string Decode(byte[] content) => Encoding.UTF8.GetString(content);
}
