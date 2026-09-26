using System.Text.RegularExpressions;

namespace Cinomni.Metadata.Contracts;

/// <summary>The renditions of a selected artwork a client may ask for, by what it renders.</summary>
public enum ArtworkSize
{
    /// <summary>A poster in a grid or a row: 342 px wide.</summary>
    PosterSmall = 1,

    /// <summary>A backdrop behind a card or on a phone: 780 px wide.</summary>
    BackdropSmall = 2,

    /// <summary>A backdrop across a desktop hero: 1280 px wide.</summary>
    BackdropLarge = 3,
}

/// <summary>
/// Derives a smaller rendition of an artwork url a provider published, so a client downloads what it
/// renders instead of the provider's original (a 3840 px backdrop behind a 300 px card).
/// <para>
/// Only TMDB's image service publishes a size ladder addressable from the url alone
/// (<c>https://image.tmdb.org/t/p/{size}/{file}</c>). Every other url — another provider, a TMDB mirror
/// configured as the image base, anything not shaped exactly like that — comes back unchanged, so the
/// answer is always a url the caller can use, at worst the original. It lives here because the url
/// layout is provider knowledge, and this module is the one that owns providers.
/// </para>
/// </summary>
public static partial class ArtworkVariants
{
    private const string TmdbImageHost = "image.tmdb.org";

    /// <summary>
    /// The rendition of <paramref name="url"/> at <paramref name="size"/>, or <paramref name="url"/> itself when
    /// it has no addressable renditions. Null stays null.
    /// </summary>
    public static string? Resize(string? url, ArtworkSize size)
    {
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.Equals(uri.Host, TmdbImageHost, StringComparison.OrdinalIgnoreCase)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return url;
        }

        var match = TmdbImagePath().Match(uri.AbsolutePath);
        return match.Success
            ? $"https://{TmdbImageHost}/t/p/{Width(size)}/{match.Groups["file"].Value}"
            : url;
    }

    private static string Width(ArtworkSize size) => size switch
    {
        ArtworkSize.PosterSmall => "w342",
        ArtworkSize.BackdropSmall => "w780",
        ArtworkSize.BackdropLarge => "w1280",
        _ => "original",
    };

    // One size segment, then one plain file name: anything with more path, or a name that is not a
    // simple image file, is not something this should rewrite.
    [GeneratedRegex(
        @"^/t/p/(?:original|w\d{2,4}|h\d{2,4})/(?<file>[A-Za-z0-9_-]{1,200}\.[A-Za-z0-9]{2,5})$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex TmdbImagePath();
}
