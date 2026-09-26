using System.Diagnostics;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Operations.Diagnostics;

/// <summary>
/// Carries W3C trace context across the two asynchronous hops every cross-module flow makes: the
/// transactional outbox and the command queue.
/// <para>
/// This is what makes one acquisition one trace. Without it a trace ends at the outbox insert and
/// starts again, unrelated, when the relay picks the row up — which is precisely the moment an
/// operator asking "why did this stall" needs the causal link. The context is captured in the
/// producer's execution context, persisted on the row inside the same transaction as the message
/// itself, and re-established as the parent of the consumer's span.
/// </para>
/// <para>
/// The context carries no data: a trace id and a span id are random numbers. It is not an
/// authorization token, it is not a correlation to an account, and it must never be treated as one.
/// </para>
/// </summary>
public static class MessageTracing
{
    /// <summary>Length of a W3C <c>traceparent</c> header: <c>00-{32 hex}-{16 hex}-{2 hex}</c>.</summary>
    public const int TraceParentLength = 55;

    /// <summary>
    /// Storage cap for <c>tracestate</c>. The W3C recommendation is to accept at least 512 characters;
    /// a longer value is dropped rather than truncated, because half a tracestate is invalid anyway.
    /// </summary>
    public const int TraceStateLength = 512;

    /// <summary>W3C's own limit on list members, and a bound on how much this is worth parsing.</summary>
    private const int MaxTraceStateMembers = 32;

    /// <summary>Per-member caps from the W3C grammar.</summary>
    private const int MaxTraceStateKeyLength = 256;

    private const int MaxTraceStateValueLength = 256;

    /// <summary>
    /// Reads the ambient trace context, if any. Returns <c>(null, null)</c> when nothing is tracing —
    /// which is the normal case for an installation with no exporter configured, and the reason every
    /// caller has to treat the stored context as optional.
    /// </summary>
    public static (string? TraceParent, string? TraceState) Capture()
    {
        var current = Activity.Current;
        if (current is null || current.IdFormat != ActivityIdFormat.W3C)
        {
            return (null, null);
        }

        var traceParent = current.Id;
        if (traceParent is null || traceParent.Length > TraceParentLength)
        {
            return (null, null);
        }

        // Dropped rather than truncated or stored as-is: a cut tracestate is malformed, and a malformed
        // one would be rejected by the next hop anyway. The traceparent — the part that carries the
        // causal link — survives either way.
        var traceState = IsWellFormedTraceState(current.TraceStateString) ? current.TraceStateString : null;

        return (traceParent, traceState);
    }

    /// <summary>
    /// Whether a <c>tracestate</c> matches the W3C grammar closely enough to keep.
    /// <para>
    /// This is a trust boundary, not a formality. ASP.NET Core adopts the <c>tracestate</c> header of
    /// any inbound request — including the anonymous probes and the sign-in route — and whatever it says
    /// would otherwise be written to <c>operations.outbox</c> and <c>operations.command</c> and replayed
    /// on the exported consumer span. The values are parameterised, so this is not an injection defence;
    /// it is a refusal to carry an arbitrary attacker-chosen string into the installation's own storage
    /// and out to the operator's collector.
    /// </para>
    /// <para>
    /// Nothing in this product reads a tracestate. It is kept only so a collector that does still sees a
    /// valid one, which is exactly why an invalid one is worth nothing and costs something.
    /// </para>
    /// </summary>
    public static bool IsWellFormedTraceState(string? traceState)
    {
        if (string.IsNullOrEmpty(traceState) || traceState.Length > TraceStateLength)
        {
            return false;
        }

        var members = traceState.Split(',');
        if (members.Length > MaxTraceStateMembers)
        {
            return false;
        }

        foreach (var rawMember in members)
        {
            // Optional whitespace around a list member is part of the grammar.
            var member = rawMember.Trim(' ', '\t');
            if (member.Length == 0)
            {
                return false;
            }

            var separator = member.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            if (!IsValidKey(member.AsSpan(0, separator))
                || !IsValidValue(member.AsSpan(separator + 1)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Lowercase key, optionally one <c>tenant@vendor</c> separator.</summary>
    private static bool IsValidKey(ReadOnlySpan<char> key)
    {
        if (key.Length is 0 or > MaxTraceStateKeyLength)
        {
            return false;
        }

        var tenants = 0;

        foreach (var character in key)
        {
            var allowed = character is (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '_' or '-' or '*' or '/';

            if (allowed)
            {
                continue;
            }

            if (character == '@' && ++tenants == 1)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Printable ASCII only, with the two characters that structure the header excluded.</summary>
    private static bool IsValidValue(ReadOnlySpan<char> value)
    {
        if (value.Length > MaxTraceStateValueLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < ' ' or > '~' or ',' or '=')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Starts the consuming span, parented to the context stored on the message. Returns
    /// <see langword="null"/> when nothing is listening (the default build) or when the stored context
    /// is absent or unparseable — in which case the caller simply runs untraced, never fails.
    /// </summary>
    /// <param name="name">Span name. Must be bounded: an event or command name, never an identifier.</param>
    /// <param name="traceParent">The persisted <c>traceparent</c>, or <see langword="null"/>.</param>
    /// <param name="traceState">The persisted <c>tracestate</c>, or <see langword="null"/>.</param>
    /// <param name="kind">Span kind; <see cref="ActivityKind.Consumer"/> for a queue hop.</param>
    public static Activity? StartConsumer(
        string name,
        string? traceParent,
        string? traceState,
        ActivityKind kind = ActivityKind.Consumer)
    {
        if (traceParent is null)
        {
            // No stored parent: the producer was not tracing. Start a root rather than nothing, so the
            // consumer's own work is still observable.
            return CinomniTelemetry.Source.StartActivity(name, kind);
        }

        // isRemote: the producing execution context is gone — this row crossed a database and possibly
        // a process restart — so the parent is remote in every sense that matters to a sampler.
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent)
            ? CinomniTelemetry.Source.StartActivity(name, kind, parent)
            : CinomniTelemetry.Source.StartActivity(name, kind);
    }
}
