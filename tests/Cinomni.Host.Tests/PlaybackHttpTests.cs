using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cinomni.Catalog;
using Cinomni.Catalog.Contracts;
using Cinomni.Identity;
using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Library;
using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Cinomni.Playback;
using Cinomni.Playback.Api;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Cinomni.Subtitles.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// What the player reads when a session cannot be opened, through the HTTP surface it binds to: the
/// code in <c>error</c> and a sentence it can show in <c>message</c>. The route used to put the
/// sentence in <c>error</c> and send no <c>message</c>, so the player showed the bare status text —
/// and a refusal because the node is busy has to say so, or it reads as a broken file.
/// </summary>
[Trait("Category", "RequiresDatabase")]
public sealed class PlaybackHttpTests
{
    private const string Database = "cinomni_test_host_playback";

    [Fact]
    public async Task A_transcode_past_the_limit_is_a_429_that_says_why()
    {
        var root = Path.Combine(Path.GetTempPath(), "cinomni-playback-http", Guid.NewGuid().ToString("N"));
        await using var app = await StartAsync($"{Database}_limit", root);
        try
        {
            var client = await RequestsHttpTests.SignedInClientAsync(app);

            var first = await client.PostAsJsonAsync("/api/playback/sessions", Transcoding(await RegisterAssetAsync(app)));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var refused = await client.PostAsJsonAsync("/api/playback/sessions", Transcoding(await RegisterAssetAsync(app)));

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal(PlaybackErrors.TranscodeLimit, body.RootElement.GetProperty("error").GetString());
            Assert.StartsWith("You already have", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            // "Not now", with a hint of when: the sweep that reclaims abandoned streams runs this often.
            Assert.Equal(TimeSpan.FromSeconds(30), refused.Headers.RetryAfter?.Delta);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_session_says_why_it_ended()
    {
        var root = Path.Combine(Path.GetTempPath(), "cinomni-playback-http", Guid.NewGuid().ToString("N"));
        await using var app = await StartAsync($"{Database}_end_reason", root);
        try
        {
            var client = await RequestsHttpTests.SignedInClientAsync(app);
            var started = await client.PostAsJsonAsync("/api/playback/sessions", Transcoding(await RegisterAssetAsync(app)));
            using var ticket = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var sessionId = ticket.RootElement.GetProperty("sessionId").GetString();

            using var open = JsonDocument.Parse(await client.GetStringAsync($"/api/playback/sessions/{sessionId}"));
            Assert.Equal(JsonValueKind.Null, open.RootElement.GetProperty("session").GetProperty("endReason").ValueKind);

            var stopped = await client.PostAsync($"/api/playback/sessions/{sessionId}/stop", content: null);
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);

            using var ended = JsonDocument.Parse(await client.GetStringAsync($"/api/playback/sessions/{sessionId}"));
            Assert.Equal("Stopped", ended.RootElement.GetProperty("session").GetProperty("endReason").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task An_asset_that_cannot_be_played_is_a_404_in_the_error_envelope()
    {
        await using var app = await StartAsync($"{Database}_missing", Path.GetTempPath());
        var client = await RequestsHttpTests.SignedInClientAsync(app);

        var missing = await client.PostAsJsonAsync("/api/playback/sessions", Transcoding(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal(PlaybackErrors.AssetNotFound, body.RootElement.GetProperty("error").GetString());
        Assert.Equal("No playable asset was found.", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_playlist_fetched_with_the_query_token_hands_it_on_to_its_segments_and_only_then()
    {
        // Safari plays HLS itself: it fetches the playlist from the URL it was given and each segment by
        // the bare name the playlist lists, without the query. Every segment was a 401.
        var root = Path.Combine(Path.GetTempPath(), "cinomni-playback-http", Guid.NewGuid().ToString("N"));
        await using var app = await StartAsync($"{Database}_native_hls", root);
        try
        {
            var client = await RequestsHttpTests.SignedInClientAsync(app);
            var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
            var started = await client.PostAsJsonAsync("/api/playback/sessions", Transcoding(await RegisterAssetAsync(app)));
            using var ticket = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var manifestUrl = ticket.RootElement.GetProperty("streamUrl").GetString()!;
            var segmentUrl = manifestUrl.Replace("manifest.m3u8", "seg_000.ts", StringComparison.Ordinal);
            var anonymous = app.GetTestClient();

            var native = await anonymous.GetAsync($"{manifestUrl}?access_token={Uri.EscapeDataString(token)}");
            Assert.Equal(HttpStatusCode.OK, native.StatusCode);
            Assert.Contains($"seg_000.ts?access_token={Uri.EscapeDataString(token)}", await native.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.True(native.Headers.CacheControl?.NoStore);

            // The header is what authenticates when both are sent, so the query value proves nothing
            // and is never copied into the answer.
            var withHeader = await client.GetAsync($"{manifestUrl}?access_token=not-the-token");
            var served = await withHeader.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
            Assert.DoesNotContain("access_token", served, StringComparison.Ordinal);
            Assert.Contains("seg_000.ts\n", served, StringComparison.Ordinal);

            // A segment is served as the file it is, ranges included.
            var segment = await anonymous.GetAsync($"{segmentUrl}?access_token={Uri.EscapeDataString(token)}");
            Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            Assert.Equal("bytes", Assert.Single(segment.Headers.AcceptRanges));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The player builds its menus from the ticket: the tracks with a subtitle URL only where one can be
    /// served, the qualities on offer, the one in effect, and where the converted stream's zero sits. A
    /// subtitle track is then fetched as WebVTT with the header, and a choice the file does not have is a
    /// 422 with its code.
    /// </summary>
    [Fact]
    public async Task The_ticket_carries_the_tracks_qualities_and_timeline_the_player_offers()
    {
        var root = Path.Combine(Path.GetTempPath(), "cinomni-playback-http", Guid.NewGuid().ToString("N"));
        await using var app = await StartAsync($"{Database}_choices", root);
        try
        {
            var client = await RequestsHttpTests.SignedInClientAsync(app);
            var assetId = await RegisterAssetAsync(app);
            var tenMinutes = TimeSpan.FromMinutes(10).Ticks;

            var response = await client.PostAsJsonAsync("/api/playback/sessions", new
            {
                assetId,
                capability = new { containers = new[] { "matroska" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" } },
                preferences = new { quality = "720p", startPositionTicks = tenMinutes },
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var ticket = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var body = ticket.RootElement;
            var sessionId = body.GetProperty("sessionId").GetString();
            Assert.Equal("Transcode", body.GetProperty("method").GetString());
            Assert.Equal(720, body.GetProperty("plan").GetProperty("targetMaxHeight").GetInt32());
            Assert.Equal(4000, body.GetProperty("plan").GetProperty("targetBitrateKbps").GetInt32());
            var media = body.GetProperty("media");
            Assert.Equal("720p", media.GetProperty("quality").GetString());
            Assert.Equal(tenMinutes, media.GetProperty("streamOffsetTicks").GetInt64());
            Assert.Equal(TimeSpan.FromHours(2).Ticks, media.GetProperty("durationTicks").GetInt64());
            Assert.Equal(
                ["original", "1080p", "720p", "480p", "360p"],
                media.GetProperty("qualities").EnumerateArray().Select(q => q.GetProperty("id").GetString()));
            var audio = Assert.Single(media.GetProperty("audioTracks").EnumerateArray());
            Assert.Equal("eng", audio.GetProperty("language").GetString());
            var subtitles = media.GetProperty("subtitleTracks").EnumerateArray().ToList();
            Assert.Equal($"/api/playback/sessions/{sessionId}/subtitles/2", subtitles[0].GetProperty("url").GetString());
            Assert.Equal(JsonValueKind.Null, subtitles[1].GetProperty("url").ValueKind);

            var vtt = await client.GetAsync($"/api/playback/sessions/{sessionId}/subtitles/2");
            Assert.Equal(HttpStatusCode.OK, vtt.StatusCode);
            Assert.Equal("text/vtt", vtt.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("WEBVTT", await vtt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/playback/sessions/{sessionId}/subtitles/3")).StatusCode);

            var refused = await client.PostAsJsonAsync("/api/playback/sessions", new
            {
                assetId,
                capability = new { containers = new[] { "matroska" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" } },
                preferences = new { audioStreamIndex = 7 },
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            using var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal(PlaybackErrors.InvalidPreference, envelope.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// What a conversion does beyond the size — tone mapping, a burned-in picture subtitle, folded
    /// audio — is in the plan the player reads, and the subtitle menu says which picture track it can
    /// burn in. A file played as it is reports none of it.
    /// </summary>
    [Fact]
    public async Task The_plan_says_what_a_conversion_does_to_the_picture_the_subtitles_and_the_sound()
    {
        var root = Path.Combine(Path.GetTempPath(), "cinomni-playback-http", Guid.NewGuid().ToString("N"));
        await using var app = await StartAsync($"{Database}_conversion", root);
        try
        {
            var client = await RequestsHttpTests.SignedInClientAsync(app);
            app.Services.GetRequiredService<HardwareCapabilitiesCache>()
                .Publish(new HardwareCapabilities([]) { ToneMapping = true, SubtitleOverlay = true });
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var applied = await scope.ServiceProvider.GetRequiredService<ISettingsAdministration>().ApplyAsync(
                    [SettingChange.Set(TranscodingSettingDefinitions.MaxAudioChannels.Key, "2", 0)], Guid.NewGuid());
                Assert.True(applied.IsSuccess);
            }

            var hdrAsset = await RegisterAssetAsync(app, range: VideoRangeType.Hdr10);

            var converted = await client.PostAsJsonAsync("/api/playback/sessions", new
            {
                assetId = hdrAsset,
                capability = new { containers = new[] { "matroska" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" } },
                preferences = new { subtitleStreamIndex = 3 },
            });

            Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
            using (var ticket = JsonDocument.Parse(await converted.Content.ReadAsStringAsync()))
            {
                var body = ticket.RootElement;
                Assert.Equal("Transcode", body.GetProperty("method").GetString());
                var plan = body.GetProperty("plan");
                Assert.Equal("H264", plan.GetProperty("outputCodec").GetString());
                Assert.True(plan.GetProperty("toneMapped").GetBoolean());
                Assert.Equal(3, plan.GetProperty("burnInSubtitleIndex").GetInt32());
                Assert.Equal(2, plan.GetProperty("audioChannels").GetInt32());
                var media = body.GetProperty("media");
                Assert.Equal(3, media.GetProperty("burnedInSubtitle").GetInt32());
                var tracks = media.GetProperty("subtitleTracks").EnumerateArray()
                    .ToDictionary(t => t.GetProperty("index").GetInt32(), t => t.GetProperty("canBurnIn").GetBoolean());
                Assert.Equal(new Dictionary<int, bool> { [2] = false, [3] = true }, tracks);
            }

            var asIs = await client.PostAsJsonAsync("/api/playback/sessions", new
            {
                assetId = await RegisterAssetAsync(app),
                capability = new { containers = new[] { "matroska" }, videoCodecs = new[] { "h264" }, audioCodecs = new[] { "aac" } },
            });

            Assert.Equal(HttpStatusCode.OK, asIs.StatusCode);
            using (var ticket = JsonDocument.Parse(await asIs.Content.ReadAsStringAsync()))
            {
                var body = ticket.RootElement;
                Assert.Equal("DirectPlay", body.GetProperty("method").GetString());
                var plan = body.GetProperty("plan");
                Assert.Equal(JsonValueKind.Null, plan.GetProperty("outputCodec").ValueKind);
                Assert.False(plan.GetProperty("toneMapped").GetBoolean());
                Assert.Equal(JsonValueKind.Null, plan.GetProperty("burnInSubtitleIndex").ValueKind);
                Assert.Equal(JsonValueKind.Null, plan.GetProperty("audioChannels").ValueKind);
                Assert.Equal(JsonValueKind.Null, body.GetProperty("media").GetProperty("burnedInSubtitle").ValueKind);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// A fragmented-MP4 stream (HEVC for a browser) is an init segment and .m4s segments. Both are served
    /// with their own content types, the query-token playlist carries the token onto the init segment's
    /// map tag too, and nothing but those exact names — no other .mp4, no way out of the directory — is served.
    /// </summary>
    [Fact]
    public async Task A_fragmented_MP4_stream_serves_its_init_and_segments_and_nothing_else()
    {
        var root = Path.Combine(Path.GetTempPath(), "cinomni-playback-http", Guid.NewGuid().ToString("N"));
        await using var app = await StartAsync($"{Database}_fmp4", root);
        try
        {
            var client = await RequestsHttpTests.SignedInClientAsync(app);
            var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
            var started = await client.PostAsJsonAsync("/api/playback/sessions", new
            {
                assetId = await RegisterAssetAsync(app, videoCodec: "hevc"),
                capability = new { containers = new[] { "mp4" }, videoCodecs = new[] { "hevc" }, audioCodecs = new[] { "aac" } },
            });
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            using var ticket = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            Assert.Equal("Remux", ticket.RootElement.GetProperty("method").GetString());
            var sessionId = ticket.RootElement.GetProperty("sessionId").GetString()!;
            var manifestUrl = ticket.RootElement.GetProperty("streamUrl").GetString()!;
            var hls = manifestUrl[..(manifestUrl.LastIndexOf('/') + 1)];

            var init = await client.GetAsync(hls + "init.mp4");
            Assert.Equal(HttpStatusCode.OK, init.StatusCode);
            Assert.Equal("video/mp4", init.Content.Headers.ContentType?.MediaType);
            var segment = await client.GetAsync(hls + "seg_000.m4s");
            Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            Assert.Equal("video/iso.segment", segment.Content.Headers.ContentType?.MediaType);

            // Native HLS: the init segment is named inside a tag, and needs the token as much as a segment.
            var anonymous = app.GetTestClient();
            var escaped = Uri.EscapeDataString(token);
            var playlist = await (await anonymous.GetAsync($"{manifestUrl}?access_token={escaped}")).Content.ReadAsStringAsync();
            Assert.Contains($"#EXT-X-MAP:URI=\"init.mp4?access_token={escaped}\"", playlist, StringComparison.Ordinal);
            Assert.Contains($"seg_000.m4s?access_token={escaped}", playlist, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"{hls}init.mp4?access_token={escaped}")).StatusCode);

            // Files a traversal or a look-alike name would reach, if anything let them through.
            var outputDirectory = Path.Combine(root, sessionId);
            await File.WriteAllBytesAsync(Path.Combine(root, "x.m4s"), new byte[16]);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "other.mp4"), new byte[16]);
            foreach (var hostile in new[] { "..%2Fx.m4s", "..%5Cx.m4s", "%2E%2E%2Fx.m4s", "other.mp4", "init.mp4.tmp" })
            {
                var refused = await client.GetAsync(hls + hostile);
                Assert.True(refused.StatusCode == HttpStatusCode.NotFound, $"{hostile} answered {refused.StatusCode}");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The hardware report is the operator's: a member is refused both reading it and re-running the
    /// test. The operator reads what was last published — backends, codecs, filters and every test — and
    /// a re-run publishes its own result, which the next read returns.
    /// </summary>
    [Fact]
    public async Task The_hardware_report_is_the_operators_and_a_re_run_publishes_its_result()
    {
        await using var app = await StartAsync($"{Database}_hardware", Path.GetTempPath());
        var admin = await RequestsHttpTests.SignedInClientAsync(app);
        var member = await MemberClientAsync(app);
        app.Services.GetRequiredService<HardwareCapabilitiesCache>().Publish(
            new HardwareCapabilities([EncoderBackend.Vaapi], [new DecodeCapability(EncoderBackend.Vaapi, "hevc")])
            {
                EncodableCodecs = [new EncodeCapability(EncoderBackend.Vaapi, VideoOutputCodec.H264)],
                ToneMapping = true,
                SubtitleOverlay = false,
                Report = new HardwareProbeReport(
                    "7.0-startup",
                    "linux/amd64",
                    new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero),
                    ["vaapi"],
                    [new ProbeTestResult(EncoderBackend.Vaapi, ProbeTestKind.Encode, "h264", Passed: true, Failure: null)]),
            });

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/playback/hardware/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/playback/hardware/probe", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetTestClient().GetAsync("/api/playback/hardware/")).StatusCode);

        using (var report = JsonDocument.Parse(await admin.GetStringAsync("/api/playback/hardware/")))
        {
            var body = report.RootElement;
            Assert.Equal("linux/amd64", body.GetProperty("platform").GetString());
            Assert.Equal("7.0-startup", body.GetProperty("ffmpegVersion").GetString());
            Assert.Equal(["vaapi"], body.GetProperty("hwaccels").EnumerateArray().Select(h => h.GetString()));
            var backend = Assert.Single(body.GetProperty("backends").EnumerateArray());
            Assert.Equal("Vaapi", backend.GetProperty("backend").GetString());
            Assert.Equal(["H264"], backend.GetProperty("encodes").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(["hevc"], backend.GetProperty("decodes").EnumerateArray().Select(c => c.GetString()));
            Assert.True(body.GetProperty("toneMapping").GetBoolean());
            Assert.False(body.GetProperty("subtitleOverlay").GetBoolean());
            Assert.False(body.GetProperty("softwareHevc").GetBoolean());
            var test = Assert.Single(body.GetProperty("tests").EnumerateArray());
            Assert.Equal("Encode", test.GetProperty("kind").GetString());
            Assert.True(test.GetProperty("passed").GetBoolean());
        }

        var probed = await admin.PostAsync("/api/playback/hardware/probe", content: null);
        Assert.Equal(HttpStatusCode.OK, probed.StatusCode);
        using (var result = JsonDocument.Parse(await probed.Content.ReadAsStringAsync()))
        {
            Assert.Equal("7.1-canned", result.RootElement.GetProperty("ffmpegVersion").GetString());
        }

        using var after = JsonDocument.Parse(await admin.GetStringAsync("/api/playback/hardware/"));
        var published = after.RootElement;
        Assert.Equal("7.1-canned", published.GetProperty("ffmpegVersion").GetString());
        var nvenc = Assert.Single(published.GetProperty("backends").EnumerateArray());
        Assert.Equal("Nvenc", nvenc.GetProperty("backend").GetString());
        Assert.Equal(["H264", "Hevc"], nvenc.GetProperty("encodes").EnumerateArray().Select(c => c.GetString()));
        Assert.True(published.GetProperty("softwareHevc").GetBoolean());
        Assert.True(published.GetProperty("subtitleOverlay").GetBoolean());
        var failed = published.GetProperty("tests").EnumerateArray().Single(t => !t.GetProperty("passed").GetBoolean());
        Assert.Equal("Decode", failed.GetProperty("kind").GetString());
        Assert.Equal("av1", failed.GetProperty("codec").GetString());
        Assert.Equal("no decoder", failed.GetProperty("failure").GetString());
        Assert.Equal("Nvenc", app.Services.GetRequiredService<HardwareCapabilitiesCache>().Current.AvailableBackends.Single().ToString());
    }

    private static async Task<HttpClient> MemberClientAsync(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var member = await scope.ServiceProvider.GetRequiredService<IUserProvisioning>()
            .CreateUserAsync("household-member", "correct-horse-battery", UserRole.Member);
        Assert.True(member.IsSuccess, member.IsSuccess ? null : member.Error.Message);
        var token = (await scope.ServiceProvider.GetRequiredService<ISessionService>().IssueAsync(member.Value)).Token;

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A client that cannot play the stored h264, so the plan has to transcode.</summary>
    private static object Transcoding(Guid assetId) => new
    {
        assetId,
        capability = new { containers = new[] { "matroska" }, videoCodecs = new[] { "vp9" }, audioCodecs = new[] { "aac" } },
    };

    private static async Task<Guid> RegisterAssetAsync(
        WebApplication app, string videoCodec = "h264", VideoRangeType? range = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var work = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync($"Limit {Guid.NewGuid():N}", 2024, []);
        var assetId = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<ILibraryCommands>().RegisterMediaAssetAsync(
            new RegisterMediaAssetRequest(
                assetId,
                WorkId: work.Value.Value,
                TargetIds: [Guid.NewGuid()],
                FullPath: $"/data/library/Limit/{assetId:N}.mkv",
                Size: 2_000_000_000,
                Container: "matroska",
                Streams:
                [
                    new MediaStreamInput(0, MediaStreamType.Video, videoCodec, null, null, 1920, 1080, null, range, true, false),
                    new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
                    new MediaStreamInput(2, MediaStreamType.Subtitle, "subrip", "eng", null, null, null, null, null, false, false),
                    new MediaStreamInput(3, MediaStreamType.Subtitle, "hdmv_pgs_subtitle", "eng", null, null, null, null, null, false, false),
                ],
                DurationSeconds: 7200,
                Bitrate: 20_000_000));
        return assetId;
    }

    /// <summary>Identity, Catalog, Library and Playback over real HTTP, with FFmpeg faked out.</summary>
    private static async Task<WebApplication> StartAsync(string database, string transcodeRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOperations(
            $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev");
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddCatalogModule();
        builder.Services.AddLibraryModule();
        builder.Services.AddPlaybackModule();
        builder.Services.AddSingleton(new PlaybackOptions { TranscodeRoot = transcodeRoot, MaxTranscodesPerAccount = 1 });
        builder.Services.AddSingleton<IMediaEncoder, IdleEncoder>();
        builder.Services.AddSingleton<ISubtitleConverter, CannedSubtitleConverter>();
        builder.Services.AddSingleton<ISubtitleQuery, NoSidecars>();
        builder.Services.AddSingleton<IHardwareCapabilityProbe, CannedProbe>();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
            await app.Services.MigrateCatalogAsync();
            await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<PlaybackDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPlaybackEndpoints();
        app.MapPlaybackHardwareEndpoints();
        await app.StartAsync();
        return app;
    }

    /// <summary>Hands back a manifest path and a process that never ends on its own.</summary>
    private sealed class IdleEncoder : IMediaEncoder
    {
        public async Task<TranscodeOutput> StartHlsAsync(TranscodeRequest request, CancellationToken cancellationToken = default)
        {
            // What FFmpeg has written a few seconds in: a playlist naming one segment, and the segment —
            // in fragmented MP4, an init segment the playlist maps and a .m4s, when the plan asked for it.
            Directory.CreateDirectory(request.OutputDirectory);
            var manifest = Path.Combine(request.OutputDirectory, "manifest.m3u8");
            if (request.Fmp4Segments)
            {
                await File.WriteAllTextAsync(
                    manifest,
                    "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.000000,\nseg_000.m4s\n",
                    cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(request.OutputDirectory, "init.mp4"), new byte[64], cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(request.OutputDirectory, "seg_000.m4s"), new byte[128], cancellationToken);
            }
            else
            {
                await File.WriteAllTextAsync(
                    manifest, "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.000000,\nseg_000.ts\n", cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(request.OutputDirectory, "seg_000.ts"), new byte[188], cancellationToken);
            }

            return new TranscodeOutput(manifest, request.Backend, new IdleProcess());
        }
    }

    /// <summary>What an operator's re-run of the hardware test finds: NVENC encoding both codecs, and one failed decode.</summary>
    private sealed class CannedProbe : IHardwareCapabilityProbe
    {
        public Task<HardwareCapabilities> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareCapabilities([EncoderBackend.Nvenc], [new DecodeCapability(EncoderBackend.Nvenc, "h264")])
            {
                EncodableCodecs =
                [
                    new EncodeCapability(EncoderBackend.Nvenc, VideoOutputCodec.H264),
                    new EncodeCapability(EncoderBackend.Nvenc, VideoOutputCodec.Hevc),
                ],
                SoftwareHevc = true,
                ToneMapping = false,
                SubtitleOverlay = true,
                Report = new HardwareProbeReport(
                    "7.1-canned",
                    "linux/amd64",
                    new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero),
                    ["cuda"],
                    [
                        new ProbeTestResult(EncoderBackend.Nvenc, ProbeTestKind.Encode, "hevc", Passed: true, Failure: null),
                        new ProbeTestResult(EncoderBackend.Nvenc, ProbeTestKind.Decode, "av1", Passed: false, Failure: "no decoder"),
                    ]),
            });
    }

    private sealed class CannedSubtitleConverter : ISubtitleConverter
    {
        public Task<string?> ExtractToWebVttAsync(string mediaPath, int streamIndex, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n");

        public Task<string?> AssToWebVttAsync(string assText, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoSidecars : ISubtitleQuery
    {
        public Task<IReadOnlyList<SubtitleSearchSummary>> ListForAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubtitleSearchSummary>>([]);

        public Task<SubtitleSearchDetail?> GetAsync(SubtitleSearchId searchId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SubtitleSearchDetail?>(null);

        public Task<IReadOnlyList<SubtitleAssetSummary>> ListObtainedAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubtitleAssetSummary>>([]);
    }

    private sealed class IdleProcess : IRunningTranscode
    {
        public bool HasExited => false;

        public int? ProcessId => null;

        public DateTimeOffset? StartedAt => null;

        public int? ExitCode => null;

        public string Diagnostics => string.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
