using Cinomni.Kernel.Security;

namespace Cinomni.RealTime.Streaming;

/// <summary>
/// Who a live message is for. The server decides this from the event, never the browser: a connection
/// receives what its account is entitled to and nothing else, exactly as the REST surface would answer.
/// </summary>
/// <param name="OperatorsOnly">Operator concern (downloads, acquisition, imports, the approval inbox).</param>
/// <param name="AccountId">One specific account, when the signal is about their own data.</param>
public readonly record struct RealtimeAudience(bool OperatorsOnly = false, Guid? AccountId = null)
{
    /// <summary>Every signed-in connection. Used for signals about content the library read model filters anyway.</summary>
    public static readonly RealtimeAudience Everyone = new();

    /// <summary>Administrators only.</summary>
    public static readonly RealtimeAudience Operators = new(OperatorsOnly: true);

    /// <summary>One account's own connections.</summary>
    public static RealtimeAudience Account(Guid accountId) => new(AccountId: accountId);

    /// <summary>Whether this reader may receive the message.</summary>
    public bool Includes(Viewer reader)
    {
        if (AccountId is { } account && account != reader.UserId)
        {
            return false;
        }

        return !OperatorsOnly || reader.IsAdministrator;
    }
}

/// <summary>
/// One thing the browser should react to. A message is a <b>signal, not a record</b>: apart from the
/// download progress snapshot — the one payload that exists precisely to replace a poll — it carries a
/// topic and at most an opaque id, and the client answers it by re-reading the REST endpoint that
/// already knows what that account may see.
/// <para>
/// Keeping it that way is what makes the stream safe to fan out: no title, path, channel target or
/// provider payload ever leaves through it, so a signal delivered to a connection that should not have
/// had it discloses nothing.
/// </para>
/// </summary>
/// <param name="Topic">What changed, in the client's vocabulary (<c>downloads</c>, <c>activity</c>).</param>
/// <param name="Payload">Optional data for a topic that carries some; null for a pure invalidation.</param>
public sealed record RealtimeMessage(string Topic, RealtimeAudience Audience, object? Payload = null)
{
    /// <summary>The topics the client knows about. Stable strings — the SPA switches on them.</summary>
    public static class Topics
    {
        /// <summary>A download task changed state (queued, started, finished, failed).</summary>
        public const string Downloads = "downloads";

        /// <summary>A live snapshot of the active download tasks — the only message carrying data.</summary>
        public const string DownloadProgress = "downloads.progress";

        /// <summary>An acquisition goal advanced, retried or ended.</summary>
        public const string Activity = "activity";

        /// <summary>Content landed in the library, or was replaced there.</summary>
        public const string Library = "library";

        /// <summary>An in-app notification was raised for this reader's audience.</summary>
        public const string Notifications = "notifications";

        /// <summary>A media request was submitted or resolved.</summary>
        public const string Requests = "requests";
    }
}
