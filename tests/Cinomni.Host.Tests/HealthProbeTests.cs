using System.Text;
using System.Text.Json;
using Cinomni.Host.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cinomni.Host.Tests;

/// <summary>
/// The probes are the one part of this surface an unauthenticated caller reaches on purpose, so what
/// they say is a security decision. These pin both halves of it: that readiness actually fails when a
/// dependency does, and that nothing about the installation's insides leaves with the answer.
/// <para>
/// No server and no database — an <see cref="IHealthCheck"/> is invoked directly and the writer is fed
/// a report, which is what makes the leak assertions exact rather than approximate.
/// </para>
/// </summary>
public sealed class HealthProbeTests
{
    [Fact]
    public async Task A_storage_root_below_its_floor_is_degraded_rather_than_unhealthy()
    {
        var root = Directory.CreateTempSubdirectory("cinomni-health-");
        try
        {
            // A floor no volume can satisfy: the sampler must see the root, read the volume, and say the
            // space is short — not that the root is missing.
            var now = DateTimeOffset.UtcNow;
            var sampler = Sampler(root.FullName, long.MaxValue, now);
            await sampler.RefreshAsync(now);

            // Degraded, not Unhealthy: a full disk stops new imports, but the library, playback of what
            // is already there and the interface an operator needs to fix it all keep working. Taking
            // the node out of rotation would turn a partial outage into a total one.
            Assert.Equal(HealthStatus.Degraded, sampler.Evaluate(Watched, now).Status);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_storage_root_that_is_not_there_is_unhealthy()
    {
        // A vanished root means a mount is missing, which is a different failure from a full one.
        var missing = Path.Combine(Path.GetTempPath(), $"cinomni-absent-{Guid.NewGuid():N}");
        var now = DateTimeOffset.UtcNow;
        var sampler = Sampler(missing, ObservabilityOptionsFloor, now);

        await sampler.RefreshAsync(now);

        Assert.Equal(HealthStatus.Unhealthy, sampler.Evaluate(Watched, now).Status);
    }

    [Fact]
    public async Task A_root_with_room_is_healthy()
    {
        var root = Directory.CreateTempSubdirectory("cinomni-health-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var sampler = Sampler(root.FullName, 1, now);
            await sampler.RefreshAsync(now);

            Assert.Equal(HealthStatus.Healthy, sampler.Evaluate(Watched, now).Status);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The reason the reading is taken off the request path at all: a root on a stale network mount
    /// blocks in a syscall no <see cref="CancellationToken"/> can interrupt. The probe must keep
    /// answering, and it must stop claiming the root is fine.
    /// </summary>
    [Fact]
    public void A_root_that_stops_answering_goes_degraded_instead_of_hanging_the_probe()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sampler = Sampler("/mnt/nas/library", ObservabilityOptionsFloor, startedAt);

        // Nothing has been read yet and nothing is known to be wrong: the probe answers, immediately.
        Assert.Equal(HealthStatus.Healthy, sampler.Evaluate(Watched, startedAt).Status);

        // No reading has landed by the time the window closes, because the measurement is still blocked
        // in the mount. Degraded, not Unhealthy — the root may be slow rather than gone, and the node
        // must stay in rotation.
        var later = startedAt + StorageSpaceSampler.StaleAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(HealthStatus.Degraded, sampler.Evaluate(Watched, later).Status);
    }

    [Fact]
    public async Task A_root_this_composition_does_not_have_reports_nothing_rather_than_a_failure()
    {
        // An installation registered without the production import or playback adapters watches no root.
        var now = DateTimeOffset.UtcNow;
        var sampler = new StorageSpaceSampler([], now);

        await sampler.RefreshAsync(now);

        Assert.Equal(HealthStatus.Healthy, sampler.Evaluate(HealthRegistration.LibraryStorage, now).Status);
        Assert.Equal(
            HealthStatus.Healthy,
            sampler.Evaluate(HealthRegistration.LibraryStorage, now + TimeSpan.FromDays(1)).Status);
    }

    [Fact]
    public async Task The_check_the_probe_runs_reads_the_sample_and_never_the_filesystem()
    {
        var root = Directory.CreateTempSubdirectory("cinomni-health-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var sampler = Sampler(root.FullName, long.MaxValue, now);
            await sampler.RefreshAsync(now);

            var check = new DiskSpaceHealthCheck(Watched, sampler);
            var result = await check.CheckHealthAsync(
                new HealthCheckContext
                {
                    Registration = new HealthCheckRegistration(Watched, check, HealthStatus.Degraded, null),
                });

            Assert.Equal(HealthStatus.Degraded, result.Status);
            // Nothing about the root travels with the answer, not even a description.
            Assert.Null(result.Description);
            Assert.Empty(result.Data);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Liveness_answers_while_readiness_fails_on_an_unreachable_database()
    {
        // The composed installation, with the deliberately unusable connection string every test in
        // this project uses. That is exactly the shape of a node whose database has gone away.
        var (services, _) = ApiAuthorizationTests.Compose();
        var health = services.GetRequiredService<HealthCheckService>();

        // Liveness runs no check at all, and that is the whole reason the two probes exist separately.
        // The single endpoint this replaces pinged PostgreSQL, so a database restart or failover failed
        // the probe an orchestrator restarts on — killing the installation, repeatedly, for a fault no
        // restart can fix. "Restart me" and "do not route to me yet" are different answers.
        var live = await health.CheckHealthAsync(_ => false);
        Assert.Equal(HealthStatus.Healthy, live.Status);
        Assert.Empty(live.Entries);

        // Readiness does look, and it must actually fail — a readiness probe that cannot go red is a
        // readiness probe that does nothing.
        var ready = await health.CheckHealthAsync(
            check => check.Tags.Contains(HealthResponse.ReadyTag));

        Assert.Equal(HealthStatus.Unhealthy, ready.Status);
        Assert.Equal(HealthStatus.Unhealthy, ready.Entries[HealthRegistration.Postgres].Status);

        // No sidecar is registered in this composition — the production engine is an opt-in adapter —
        // and that must read as "nothing to report", not as a resolution failure.
        Assert.Equal(HealthStatus.Healthy, ready.Entries[HealthRegistration.Sidecar].Status);
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_and_503_when_a_dependency_is_down()
    {
        var report = Report(
            (HealthRegistration.Postgres, HealthStatus.Unhealthy),
            (HealthRegistration.Sidecar, HealthStatus.Healthy));

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await HealthResponse.WriteAsync(context, report);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        // Never cacheable: the answer is memoized inside the application for a few seconds on purpose,
        // and a proxy holding it for longer would report a dependency as fine after it stopped being so.
        Assert.Equal("no-store, no-cache", context.Response.Headers.CacheControl);

        var payload = ReadJson(context);
        Assert.Equal("Unhealthy", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_degraded_dependency_still_answers_200_so_the_node_stays_in_rotation()
    {
        var report = Report(
            (HealthRegistration.Postgres, HealthStatus.Healthy),
            (HealthRegistration.Sidecar, HealthStatus.Degraded));

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await HealthResponse.WriteAsync(context, report);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("Degraded", ReadJson(context).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_response_carries_no_description_no_exception_and_no_path()
    {
        // Everything a real failing check could plausibly be holding. The framework's default writer
        // emits all of it; this one must emit none of it, because the caller has no credentials.
        const string secretPath = "/srv/media/library";
        const string secretMessage = "Host=db;Username=cinomni;Password=hunter2";

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
            {
                [HealthRegistration.Postgres] = new(
                    HealthStatus.Unhealthy,
                    description: secretMessage,
                    duration: TimeSpan.FromSeconds(7),
                    exception: new InvalidOperationException(secretMessage),
                    data: new Dictionary<string, object> { ["root"] = secretPath }),
                [HealthRegistration.LibraryStorage] = new(
                    HealthStatus.Degraded,
                    description: $"Only 12 bytes left on {secretPath}.",
                    duration: TimeSpan.FromMilliseconds(3),
                    exception: null,
                    data: new Dictionary<string, object> { ["path"] = secretPath }),
            },
            HealthStatus.Unhealthy,
            TimeSpan.FromSeconds(7));

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await HealthResponse.WriteAsync(context, report);

        var body = ReadBody(context);

        Assert.DoesNotContain(secretPath, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duration", body, StringComparison.OrdinalIgnoreCase);

        // What it does say: the fixed check names and their statuses, which is all an orchestrator and
        // an operator reading a probe actually need.
        Assert.Contains(HealthRegistration.Postgres, body, StringComparison.Ordinal);
        Assert.Contains("Unhealthy", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probes take no credentials, so the dependency work behind them must not be something an
    /// anonymous caller can multiply. Each request used to open a pooled PostgreSQL connection and an
    /// outbound gRPC connect, both parked for the full readiness budget while the sidecar is down.
    /// </summary>
    [Fact]
    public async Task A_flood_of_anonymous_probes_asks_the_dependencies_once()
    {
        var counting = new CountingCheck();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                "counting", counting, HealthStatus.Unhealthy, [HealthResponse.ReadyTag]));

        using var provider = services.BuildServiceProvider();
        var readiness = new ReadinessSnapshot(
            provider.GetRequiredService<HealthCheckService>(), ReadinessSnapshot.DefaultMaxAge);

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => readiness.ReadAsync()));

        Assert.Equal(1, counting.Runs);
        Assert.All(answers, report => Assert.Equal(HealthStatus.Healthy, report.Status));
    }

    [Fact]
    public async Task An_answer_that_has_aged_out_is_taken_again()
    {
        var counting = new CountingCheck();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                "counting", counting, HealthStatus.Unhealthy, [HealthResponse.ReadyTag]));

        using var provider = services.BuildServiceProvider();

        // A window of nothing: every read is stale, so a dependency coming back is never masked by a
        // cached answer for longer than the window an operator configured.
        var readiness = new ReadinessSnapshot(
            provider.GetRequiredService<HealthCheckService>(), TimeSpan.Zero);

        await readiness.ReadAsync();
        await readiness.ReadAsync();

        Assert.Equal(2, counting.Runs);
    }

    /// <summary>A dependency check that records how often it was actually asked.</summary>
    private sealed class CountingCheck : IHealthCheck
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);

            // Long enough that every caller in the flood arrives while the first one is still waiting,
            // which is the case the gate exists for.
            await Task.Delay(50, cancellationToken);
            return HealthCheckResult.Healthy();
        }
    }

    private const long ObservabilityOptionsFloor = 1024;

    /// <summary>The check name used by the single-root samplers above.</summary>
    private const string Watched = HealthRegistration.LibraryStorage;

    private static StorageSpaceSampler Sampler(string path, long minimumFreeBytes, DateTimeOffset startedAt) =>
        new([new StorageRoot(Watched, path, minimumFreeBytes)], startedAt);

    private static HealthReport Report(params (string Name, HealthStatus Status)[] entries)
    {
        var dictionary = entries.ToDictionary(
            entry => entry.Name,
            entry => new HealthReportEntry(entry.Status, null, TimeSpan.Zero, null, null),
            StringComparer.Ordinal);

        var worst = entries.Min(entry => entry.Status);
        return new HealthReport(dictionary, worst, TimeSpan.Zero);
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonElement ReadJson(HttpContext context) =>
        JsonDocument.Parse(ReadBody(context)).RootElement;
}
