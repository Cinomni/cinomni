using System.Globalization;
using System.Text.Json;

namespace Cinomni.Metadata.Providers;

/// <summary>
/// Defensive readers over an untrusted provider response. Every accessor returns <c>null</c> (or an empty
/// sequence) rather than throwing when the property is missing, null or of the wrong JSON kind — a
/// provider that changes a field's type must degrade one value, never sink a whole refresh. Shared by the
/// three REST adapters so the parsing rules (numeric-as-string, date-only vs timezone-aware) are defined
/// exactly once.
/// </summary>
internal static class ProviderJson
{
    /// <summary>The property as a string, or <c>null</c> when absent, null, or not a string.</summary>
    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The property as an int. Tolerates the numeric-as-string form TheTVDB uses for some ids and years.
    /// </summary>
    public static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static double? Double(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>The property as a nested object, or <c>null</c> when absent or not an object.</summary>
    public static JsonElement? Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>The property's items, or an empty sequence when absent or not an array.</summary>
    public static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    /// <summary>
    /// The property as a published, date-only value (<c>2026-07-28</c>). This is what date-based release
    /// matching compares against, so it is parsed exactly as published — never shifted by a timezone.
    /// </summary>
    public static DateOnly? Date(JsonElement element, string name) => ParseDate(String(element, name));

    /// <summary>
    /// The property as a timezone-aware instant, normalised to UTC. Only ever read from a provider field
    /// that genuinely carries an offset — a date-only field must not become
    /// midnight UTC, which would be a fabricated broadcast time.
    /// </summary>
    public static DateTimeOffset? Instant(JsonElement element, string name)
    {
        var raw = String(element, name);
        return !string.IsNullOrEmpty(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
                ? value.ToUniversalTime()
                : null;
    }

    /// <summary>Parses a published date (<c>yyyy-MM-dd</c>), tolerating an empty or malformed value.</summary>
    public static DateOnly? ParseDate(string? value) =>
        !string.IsNullOrEmpty(value)
        && DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>The leading four digits of a date/year string, or <c>null</c> when there are none.</summary>
    public static int? Year(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length >= 4
        && int.TryParse(value.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;

    /// <summary>
    /// The element's raw JSON with the named properties omitted. Used to keep the stored
    /// <c>raw_response</c> small: an embedded 900-episode array is megabytes of jsonb per refresh, and the
    /// episodes are already persisted relationally.
    /// </summary>
    public static string RawWithout(JsonElement element, params string[] excluded)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element.GetRawText();
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (System.Array.IndexOf(excluded, property.Name) < 0)
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
