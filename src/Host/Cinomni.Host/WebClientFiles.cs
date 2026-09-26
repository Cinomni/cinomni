using Microsoft.AspNetCore.StaticFiles;

namespace Cinomni.Host;

/// <summary>
/// How long a browser may keep each file of the built web client.
/// <para>
/// The bundle has two kinds of file and they need opposite answers. Everything under <c>/assets</c>
/// carries a content hash in its name, so a given URL can never change meaning and may be kept for as
/// long as the browser likes. <c>index.html</c> is the opposite: its URL never changes and its contents
/// name the hashed files of the build it came from. With no <c>Cache-Control</c> at all a browser applies
/// heuristic freshness to it, so after an upgrade a returning viewer can be handed the previous shell,
/// which asks for asset filenames the new image no longer contains — a blank page until a hard refresh.
/// </para>
/// <para>
/// The same options are used for the SPA fallback, because a deep link such as <c>/works/{id}</c> is
/// served the very same document and must not be cached any longer than the shell itself.
/// </para>
/// </summary>
internal static class WebClientFiles
{
    /// <summary>Where the build emits content-hashed files; the one directory that is safe to keep.</summary>
    private const string HashedAssetDirectory = "/assets";

    /// <summary>A year, the conventional ceiling, plus the promise that the bytes will never change.</summary>
    internal const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>Keep it, but ask every time whether it is still current. This is what makes upgrades land.</summary>
    internal const string RevalidateCacheControl = "no-cache";

    /// <summary>The options both the static-file middleware and the SPA fallback are configured with.</summary>
    public static StaticFileOptions Options() => new() { OnPrepareResponse = SetCacheControl };

    /// <summary>Applies the rule above to one response, keyed on the requested path.</summary>
    internal static void SetCacheControl(StaticFileResponseContext context) =>
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments(HashedAssetDirectory)
                ? ImmutableCacheControl
                : RevalidateCacheControl;
}
