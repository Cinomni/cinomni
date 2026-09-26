using Cinomni.Kernel.Messaging;

namespace Cinomni.Identity.Messaging;

/// <summary>Stable registered names of the Identity commands.</summary>
public static class IdentityCommandNames
{
    public const string PurgeSessions = "identity.purge-sessions";
}

/// <summary>
/// Removes session rows that can no longer authenticate anything, so their stored token hashes stop
/// accumulating. Parameterless — the scheduler constructs it and the grace period is deployment
/// configuration.
/// </summary>
public sealed record PurgeSessionsCommand : ICommand;
