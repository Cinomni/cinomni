using Cinomni.Kernel.Identifiers;
using Xunit;

namespace Cinomni.Kernel.Tests;

public sealed class Uuid7Tests
{
    [Fact]
    public void New_produces_a_version_7_guid()
    {
        var id = Uuid7.New();

        Assert.Equal(7, id.Version);
    }

    [Fact]
    public void New_ids_are_time_ordered_by_big_endian_bytes()
    {
        var earlier = Uuid7.New(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = Uuid7.New(new DateTimeOffset(2020, 1, 1, 0, 0, 1, TimeSpan.Zero));

        ReadOnlySpan<byte> e = earlier.ToByteArray(bigEndian: true);
        ReadOnlySpan<byte> l = later.ToByteArray(bigEndian: true);

        // UUIDv7 encodes the timestamp in the most-significant bytes, so a big-endian
        // byte comparison reflects chronological order.
        Assert.True(e.SequenceCompareTo(l) < 0);
    }
}
