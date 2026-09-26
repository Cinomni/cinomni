using Cinomni.Kernel.Messaging;

namespace Cinomni.Operations.Retention;

/// <summary>Stable registered names of the platform kernel's own commands.</summary>
public static class OperationsCommandNames
{
    public const string PurgeRetention = "operations.purge-retention";
}

/// <summary>
/// Ages published outbox messages and terminal commands out of the <c>operations</c> schema.
/// Parameterless on purpose: the scheduler constructs it, and every window is deployment
/// configuration rather than per-run input.
/// </summary>
public sealed record PurgeOperationsCommand : ICommand;
