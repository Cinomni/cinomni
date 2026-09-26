using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Net;

namespace Cinomni.Kernel.Tests;

/// <summary>
/// The hardened outbound handler was five identical copies in five module registrations; lifting it
/// into the Kernel is a change at a trust boundary, so these pin the properties that made those copies
/// safe. They must keep passing byte for byte: a handler that follows redirects, honours an ambient
/// proxy or connects without the callback is an SSRF hole, not a refactor.
/// </summary>
public sealed class SsrfSafeHttpHandlerTests
{
    [Fact]
    public void The_handler_refuses_redirects_and_ambient_proxies_and_keeps_its_connect_callback()
    {
        using var handler = SsrfSafeHttpHandler.Create(
            TimeSpan.FromSeconds(10), CinomniTelemetry.Modules.Discovery);

        // A redirect is chosen by the remote side, so following one hands destination selection to the
        // party being guarded against.
        Assert.False(handler.AllowAutoRedirect);
        // An ambient proxy would make every connection look public while the real origin travels in
        // the request.
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        // The callback is the whole guard: without it the socket layer picks the address.
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task A_loopback_destination_is_refused_and_counted_without_naming_the_host()
    {
        var measurements = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = ListenFor("cinomni.http_guard.rejections", measurements);

        using var handler = SsrfSafeHttpHandler.Create(
            TimeSpan.FromSeconds(2), CinomniTelemetry.Modules.Notifications);
        using var client = new HttpClient(handler);

        // "localhost" resolves only to loopback addresses, all of which SsrfGuard blocks, so the
        // connect callback must throw before any socket is opened.
        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(new Uri("http://localhost:9/")));

        Assert.Contains("Refusing to connect", failure.InnerException?.Message ?? failure.Message, StringComparison.Ordinal);

        var rejection = Assert.Single(measurements);
        Assert.Equal(1, rejection.Value);
        Assert.Equal(NetworkGuardMetrics.Reasons.BlockedAddress, rejection.Tags[CinomniTelemetry.Tags.Reason]);
        Assert.Equal(CinomniTelemetry.Modules.Notifications, rejection.Tags[CinomniTelemetry.Tags.Module]);

        // The refused host is attacker-influenced and, for a provider URL, carries the API key. It may
        // reach a log; it must never reach a time series.
        Assert.DoesNotContain(
            rejection.Tags,
            tag => tag.Value is string text && text.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_url_rejected_before_connecting_is_counted_under_its_own_reason()
    {
        var measurements = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = ListenFor("cinomni.http_guard.rejections", measurements);

        Assert.False(SsrfGuard.TryValidatePublicUrl("http://169.254.169.254/latest/meta-data/", out _));
        NetworkGuardMetrics.RecordRejection(
            NetworkGuardMetrics.Reasons.InvalidUrl, CinomniTelemetry.Modules.Downloads);

        var rejection = Assert.Single(measurements);
        Assert.Equal(NetworkGuardMetrics.Reasons.InvalidUrl, rejection.Tags[CinomniTelemetry.Tags.Reason]);
    }

    /// <summary>
    /// Subscribes to one instrument by name with the BCL listener — no test-only telemetry package, and
    /// it proves the counter is observable exactly the way an exporter observes it.
    /// </summary>
    private static MeterListener ListenFor(
        string instrumentName,
        List<(long Value, IReadOnlyDictionary<string, object?> Tags)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CinomniTelemetry.MeterName && instrument.Name == instrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var copied = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            lock (sink)
            {
                sink.Add((value, copied));
            }
        });

        listener.Start();
        return listener;
    }
}
