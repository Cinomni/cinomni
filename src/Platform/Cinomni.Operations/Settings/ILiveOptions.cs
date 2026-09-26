namespace Cinomni.Operations.Settings;

/// <summary>
/// A hot-reloadable options value. A consumer reads <see cref="Current"/> at use time — never in its
/// constructor — so it always sees the latest resolved value instead of the one frozen when the type
/// was constructed.
/// </summary>
public interface ILiveOptions<TOptions>
    where TOptions : class
{
    TOptions Current { get; }
}
