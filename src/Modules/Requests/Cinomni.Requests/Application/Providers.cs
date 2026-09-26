using Cinomni.Catalog.Contracts;

namespace Cinomni.Requests.Application;

/// <summary>
/// Maps the provider name a request carries ("tmdb", the name a metadata source gives itself) onto the
/// Catalog's provider enum. Returns null for anything Catalog cannot key a work on — which submission
/// rejects (<c>requests.invalid_reference</c>), so fulfilment always has an external id to be idempotent
/// on. <c>Enum.TryParse</c> also accepts the numeric form ("99"), which would sail past that guard as an
/// undefined enum value, hence the <c>IsDefined</c> check.
/// </summary>
internal static class Providers
{
    public static MetadataProvider? Parse(string provider) =>
        Enum.TryParse<MetadataProvider>(provider, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
}
