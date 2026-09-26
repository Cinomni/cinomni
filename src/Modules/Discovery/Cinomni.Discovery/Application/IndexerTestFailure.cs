using System.Net;
using System.Net.Sockets;
using Cinomni.Discovery.Indexers;

namespace Cinomni.Discovery.Application;

/// <summary>
/// Turns the exception an indexer test ended with into a bounded code and a fixed sentence an
/// operator can act on. "Indexer test failed" says nothing about whether to fix the URL, the network,
/// the credential or the site itself; each case here points at one of those. Messages are fixed text —
/// never the exception's own message, which may carry whatever the remote side sent.
/// </summary>
internal static class IndexerTestFailure
{
    public const string Failed = "discovery.indexer.test_failed";
    public const string DestinationRefused = "discovery.indexer.test_destination_refused";
    public const string Unresolved = "discovery.indexer.test_unresolved";
    public const string Unreachable = "discovery.indexer.test_unreachable";
    public const string TimedOut = "discovery.indexer.test_timed_out";
    public const string HttpStatus = "discovery.indexer.test_http_status";
    public const string NotAFeed = "discovery.indexer.test_not_a_feed";
    public const string BadResponse = "discovery.indexer.test_bad_response";
    public const string ChallengeBlocked = "discovery.indexer.test_browser_challenge";

    /// <summary>The prefix of the refusal <c>SsrfSafeHttpHandler</c> raises for a non-public address.</summary>
    private const string RefusalPrefix = "Refusing to connect to";

    public static (string Code, string Message) Describe(Exception failure)
    {
        if (failure is TorznabFeedException feed)
        {
            return (NotAFeed, $"The indexer answered, but not with a Torznab/Newznab feed ({feed.Code}). Check the URL and API key.");
        }

        if (failure is IndexerChallengeException)
        {
            return (ChallengeBlocked,
                "The site answered with a browser challenge. Turn on Use FlareSolverr in this indexer's settings; "
                + "if it is already on, the challenge could not be solved right now — try again in a few minutes.");
        }

        if (failure is HttpRequestException { StatusCode: { } status })
        {
            return (HttpStatus, StatusSentence(status));
        }

        if (Find<IOException>(failure) is { } io && io.Message.StartsWith(RefusalPrefix, StringComparison.Ordinal))
        {
            return (DestinationRefused,
                "The site's address resolves to a private or local network, which Cinomni never contacts. "
                + "Some internet providers answer blocked sites this way; a different DNS resolver or a VPN usually fixes it.");
        }

        if (Find<SocketException>(failure) is { } socket)
        {
            return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? (Unresolved, "The site's name does not resolve. Check the URL, or whether the domain has moved.")
                : (Unreachable, "The site could not be reached: the connection was refused or dropped.");
        }

        if (failure is OperationCanceledException or TimeoutException || Find<TimeoutException>(failure) is not null)
        {
            return (TimedOut, "The site did not answer in time. It may be down, slow, or behind a challenge page.");
        }

        if (failure is HttpRequestException)
        {
            return (Unreachable, "The site could not be reached.");
        }

        if (failure is InvalidDataException or System.Xml.XmlException)
        {
            return (BadResponse, "The site answered with something that could not be read as search results.");
        }

        return (Failed, "Indexer test failed.");
    }

    private static string StatusSentence(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"The site refused the request (HTTP {(int)status}). Check the credential, or whether it blocks automated access.",
        HttpStatusCode.NotFound => "The site answered HTTP 404: the URL does not point at a search endpoint.",
        HttpStatusCode.TooManyRequests => "The site answered HTTP 429: too many requests. Try again later.",
        _ when (int)status >= 500 => $"The site answered HTTP {(int)status}: it is having trouble right now.",
        _ => $"The site answered HTTP {(int)status} to the test search.",
    };

    private static T? Find<T>(Exception failure)
        where T : Exception
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
