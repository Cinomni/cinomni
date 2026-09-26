using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinomni.Downloads.Persistence;

/// <summary>
/// The seeding rule for a task, enforced by the backend because libtorrent offers no hard
/// per-torrent ratio/seed-time cutoff (only global de-prioritization). Either bound may be null,
/// meaning "no limit on this axis". A task that meets any active bound is stopped.
/// </summary>
public sealed record SeedingPolicy(double? RatioLimit, int? SeedTimeLimitSeconds)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Keep seeding until stopped manually (no ratio or time bound).</summary>
    public static SeedingPolicy Unbounded { get; } = new(RatioLimit: null, SeedTimeLimitSeconds: null);

    /// <summary>True when either active bound is met, given the cumulative transfer figures.</summary>
    public bool IsMet(long allTimeUpload, long allTimeDownload, int seedingSeconds)
    {
        if (RatioLimit is { } ratioLimit && allTimeDownload > 0)
        {
            var ratio = (double)allTimeUpload / allTimeDownload;
            if (ratio >= ratioLimit)
            {
                return true;
            }
        }

        return SeedTimeLimitSeconds is { } timeLimit && seedingSeconds >= timeLimit;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SeedingPolicy FromJson(string json) =>
        JsonSerializer.Deserialize<SeedingPolicy>(json, JsonOptions) ?? Unbounded;
}
