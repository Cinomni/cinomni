using System.Text.Json;
using System.Text.Json.Serialization;
using Cinomni.Import.Contracts;

namespace Cinomni.Import.Persistence;

/// <summary>
/// Serialises the analysed <see cref="MediaInfo"/> value object to/from the job's <c>jsonb</c>
/// column (same embedded value-object pattern as the download seeding policy). The relational,
/// queryable stream model is Library's; here it is kept as an opaque document for
/// observability and to feed the <c>MediaAvailable</c> event.
/// </summary>
internal static class MediaInfoSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(MediaInfo mediaInfo) => JsonSerializer.Serialize(mediaInfo, JsonOptions);

    public static MediaInfo? FromJson(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<MediaInfo>(json, JsonOptions);
}
