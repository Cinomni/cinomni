using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Settings;

namespace Cinomni.Metadata.Application;

/// <summary>The classification region as the settings store currently has it, not as it was at startup.</summary>
internal sealed class LiveContentRatingRegion(ILiveOptions<ContentRatingOptions> options) : IContentRatingRegion
{
    public string? Current => options.Current.IsConfigured ? options.Current.Region : null;
}
