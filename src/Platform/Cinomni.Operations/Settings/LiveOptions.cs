using Microsoft.Extensions.Configuration;

namespace Cinomni.Operations.Settings;

/// <summary>
/// Caches <c>(generation, TOptions instance)</c>. A read is a <see cref="Volatile"/> read of the
/// current snapshot plus a <see langword="long"/> comparison; an equal generation returns the cached
/// instance with no dictionary lookup and no database round trip. On a generation change it rebuilds
/// under a lock using the module-supplied binder.
/// </summary>
internal sealed class LiveOptions<TOptions> : ILiveOptions<TOptions>
    where TOptions : class
{
    private readonly SettingsCache _cache;
    private readonly IConfiguration _configuration;
    private readonly Func<SettingsView, TOptions> _binder;
    private readonly Lock _lock = new();

    private long _generation = -1;
    private TOptions? _instance;

    public LiveOptions(SettingsCache cache, IConfiguration configuration, Func<SettingsView, TOptions> binder)
    {
        _cache = cache;
        _configuration = configuration;
        _binder = binder;
    }

    public TOptions Current
    {
        get
        {
            var snapshot = _cache.Current;
            if (Volatile.Read(ref _generation) == snapshot.Generation && _instance is { } fresh)
            {
                return fresh;
            }

            lock (_lock)
            {
                // Re-read inside the lock: another thread may have already rebuilt for this generation.
                snapshot = _cache.Current;
                if (_generation == snapshot.Generation && _instance is { } alreadyBuilt)
                {
                    return alreadyBuilt;
                }

                var built = _binder(new SettingsView(snapshot, _configuration));
                _instance = built;
                Volatile.Write(ref _generation, snapshot.Generation);
                return built;
            }
        }
    }
}
