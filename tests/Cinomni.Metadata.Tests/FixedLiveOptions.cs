using Cinomni.Operations.Settings;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// A live-options value that never changes, for a test that cares what the value is rather than that
/// it can move. The settings store's own reload path has its own tests; standing one up here would
/// only make these depend on it.
/// </summary>
internal sealed class FixedLiveOptions<TOptions>(TOptions value) : ILiveOptions<TOptions>
    where TOptions : class
{
    public TOptions Current { get; } = value;
}
