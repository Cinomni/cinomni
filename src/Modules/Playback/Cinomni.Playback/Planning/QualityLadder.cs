using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Planning;

/// <summary>
/// One rung of the quality ladder: the box the picture is scaled to fit — width and height both, so a
/// wide 1920x800 film counts as 1080p rather than as something under 1080 — and the video bitrate it
/// is held under.
/// </summary>
public sealed record QualityStep(string Id, int MaxWidth, int MaxHeight, int MaxBitrateKbps)
{
    /// <summary>Whether a picture of this size is larger than the box in either direction.</summary>
    public bool IsExceededBy(int? width, int? height) =>
        (width is { } w && w > MaxWidth) || (height is { } h && h > MaxHeight);

    public QualityOptionView ToView() => new(Id, MaxWidth, MaxHeight, MaxBitrateKbps);
}

/// <summary>
/// The qualities a viewer may pick instead of the file as it is. A fixed ladder, owned by the server:
/// the client offers what it is given and sends back an id, so no free number ever reaches FFmpeg.
/// </summary>
public static class QualityLadder
{
    /// <summary>The file as it is — no ceiling. Always offered, and the default.</summary>
    public const string Original = "original";

    /// <summary>Highest first.</summary>
    public static IReadOnlyList<QualityStep> Steps { get; } =
    [
        new("1080p", 1920, 1080, 10_000),
        new("720p", 1280, 720, 4_000),
        new("480p", 854, 480, 2_000),
        new("360p", 640, 360, 1_000),
    ];

    /// <summary>
    /// The rung with this id; null for <see cref="Original"/> or no id. <paramref name="known"/> is false
    /// for an id that names no rung, which the caller refuses rather than guessing.
    /// </summary>
    public static QualityStep? Find(string? id, out bool known)
    {
        known = true;
        if (string.IsNullOrEmpty(id) || string.Equals(id, Original, StringComparison.Ordinal))
        {
            return null;
        }

        var step = Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        known = step is not null;
        return step;
    }

    /// <summary>
    /// The options worth offering for a picture of this size: <see cref="Original"/>, then every rung the
    /// picture reaches — one it is wider or taller than the rung below. A 720p file is not offered 1080p,
    /// which would be the same picture under a different name. An unknown size is offered every rung.
    /// </summary>
    public static IReadOnlyList<QualityOptionView> OfferedFor(int? width, int? height)
    {
        var offered = new List<QualityOptionView> { new(Original, null, null, null) };
        for (var i = 0; i < Steps.Count; i++)
        {
            var below = i + 1 < Steps.Count ? Steps[i + 1] : null;
            var reaches = (width is null && height is null) || below is null || below.IsExceededBy(width, height);
            if (reaches)
            {
                offered.Add(Steps[i].ToView());
            }
        }

        return offered;
    }
}
