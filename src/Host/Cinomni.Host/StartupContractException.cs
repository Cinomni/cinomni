namespace Cinomni.Host;

/// <summary>
/// Raised when the runtime contract a packaged installation is started under is not satisfied: today, a
/// storage root the process must be able to write and cannot.
/// <para>
/// It is a distinct type because of what the composition root does with it. The single audience for this
/// failure is an operator who mounted a volume wrong, the message is already written for them, and the
/// answer they need is one sentence and a clean exit — not a stack trace and a process killed by a
/// signal. Catching <see cref="InvalidOperationException"/> around the start would deliver that outcome
/// for a composition or configuration-binding defect as well, turning a bug into a quiet exit; catching
/// exactly this type cannot. Everything that fails before the contract check keeps crashing, because a
/// stack trace is the right answer to a defect.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/> because that is what it is — the process is
/// not in a state where the requested operation can proceed — and because
/// <c>StartupChecks.WarnWhenRootNotWritable</c> reuses the same probe for the roots that only degrade a
/// feature, so the same failure is already both thrown and caught by design.
/// </para>
/// </summary>
internal sealed class StartupContractException : InvalidOperationException
{
    /// <summary>
    /// The process exit code when this is what stopped the start. 78 is <c>EX_CONFIG</c> from
    /// <c>sysexits.h</c> — "the configuration is wrong" — which is precisely this failure and is
    /// distinct from the 0/1/2 vocabulary the <c>backup</c> verbs use for their own outcomes. What
    /// matters most about it is what it is not: a signal death, which is what an unhandled exception
    /// leaves behind and what a supervisor reports as a crash rather than as a refusal to start.
    /// </summary>
    public const int ExitCode = 78;

    /// <summary>A contract violated by configuration alone, with no underlying failure to report.</summary>
    public StartupContractException(string message)
        : base(message)
    {
    }

    /// <summary>A contract violated by an operation the platform refused; the cause is kept for the log.</summary>
    public StartupContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
