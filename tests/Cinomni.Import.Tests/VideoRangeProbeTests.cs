using System.Text.Json;
using Cinomni.Import.Probe;

namespace Cinomni.Import.Tests;

/// <summary>Pure tests for reading a video stream's dynamic range out of ffprobe's JSON.</summary>
public sealed class VideoRangeProbeTests
{
    private static string RangeOf(string streamJson)
    {
        using var document = JsonDocument.Parse(streamJson);
        return FfprobeMediaProbe.VideoRangeOf(document.RootElement);
    }

    [Theory]
    [InlineData("""{"color_transfer":"smpte2084"}""", "Hdr10")]
    [InlineData("""{"color_transfer":"arib-std-b67"}""", "Hlg")]
    [InlineData("""{"color_transfer":"bt709"}""", "Sdr")]
    [InlineData("""{}""", "Sdr")]
    public void The_transfer_function_names_the_range(string stream, string expected)
    {
        Assert.Equal(expected, RangeOf(stream));
    }

    [Fact]
    public void Dolby_vision_is_known_by_its_configuration_record_whatever_the_base_layer_says()
    {
        // Profile 7/8 carries an HDR10 base layer; the side data is what says Dolby Vision.
        const string stream = """
            {"color_transfer":"smpte2084","side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":7}]}
            """;

        Assert.Equal("DoVi", RangeOf(stream));
    }
}
