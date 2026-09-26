using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinomni.Library.Persistence;

/// <summary>
/// The controlled quality value-object stored on a version as <c>jsonb</c> (the one legitimate JSON
/// column in the library schema; the queryable structural quality lives in the
/// relational <c>MediaStream</c>). For the movie slice only the resolution is derived (from the video
/// stream geometry); source/modifier come from the release parse and are filled in later.
/// </summary>
public sealed record MediaQuality(string? Source, string? Resolution, string? Modifier, int Revision)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MediaQuality Unknown { get; } = new(Source: null, Resolution: null, Modifier: null, Revision: 1);

    /// <summary>Derives a resolution label from a video stream's height (e.g. 1080 → "1080p").</summary>
    public static MediaQuality FromHeight(int? height) => height switch
    {
        null or 0 => Unknown,
        >= 2000 => Unknown with { Resolution = "2160p" },
        >= 1000 => Unknown with { Resolution = "1080p" },
        >= 700 => Unknown with { Resolution = "720p" },
        >= 570 => Unknown with { Resolution = "576p" },
        >= 470 => Unknown with { Resolution = "480p" },
        _ => Unknown with { Resolution = "SD" },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MediaQuality FromJson(string json) =>
        JsonSerializer.Deserialize<MediaQuality>(json, JsonOptions) ?? Unknown;
}
