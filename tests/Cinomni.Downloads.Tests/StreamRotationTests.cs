using Cinomni.Downloads.Streaming;

namespace Cinomni.Downloads.Tests;

/// <summary>
/// Which transfers the status pump watches when there are more in flight than it may stream at once.
/// The sidecar gives status streams a fixed share of its threads, so the pump has to take turns — and
/// taking turns only works if nobody is starved.
/// </summary>
public sealed class StreamRotationTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Never_takes_more_than_the_free_slots()
    {
        var chosen = DownloadStreamPump.ChooseToStream(["a", "b", "c", "d"], new HashSet<string>(), new Dictionary<string, DateTimeOffset>(), free: 2);

        Assert.Equal(2, chosen.Count);
    }

    [Fact]
    public void Takes_nothing_when_every_slot_is_in_use()
    {
        var chosen = DownloadStreamPump.ChooseToStream(["a", "b"], new HashSet<string>(), new Dictionary<string, DateTimeOffset>(), free: 0);

        Assert.Empty(chosen);
    }

    [Fact]
    public void Does_not_open_a_second_stream_for_a_transfer_already_watched()
    {
        var chosen = DownloadStreamPump.ChooseToStream(
            ["a", "b"], new HashSet<string> { "a" }, new Dictionary<string, DateTimeOffset>(), free: 5);

        Assert.Equal(["b"], chosen);
    }

    [Fact]
    public void A_transfer_never_watched_goes_first_and_then_the_one_watched_longest_ago()
    {
        // The stream that just ended would otherwise win the slot back on the next pass, every pass,
        // and the transfers waiting behind it would never be watched at all.
        var lastStreamed = new Dictionary<string, DateTimeOffset>
        {
            ["just-ended"] = Noon,
            ["a-while-ago"] = Noon.AddMinutes(-2),
        };

        var chosen = DownloadStreamPump.ChooseToStream(
            ["just-ended", "a-while-ago", "never"], new HashSet<string>(), lastStreamed, free: 2);

        Assert.Equal(["never", "a-while-ago"], chosen);
    }
}
