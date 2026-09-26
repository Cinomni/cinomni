using System.Text.Json;
using Cinomni.Host.SystemInfo;

namespace Cinomni.Host.Tests;

/// <summary>
/// What this installation says it is running. The route is pinned as user-facing by
/// <see cref="ApiAuthorizationTests"/>; these cover the two things that would otherwise be found out
/// by a reader: that a build nobody stamped says so instead of inventing a commit, and that the four
/// field names the web client binds to are exactly these four.
/// </summary>
public sealed class SystemInfoTests
{
    [Fact]
    public void A_stamped_build_reports_its_version_commit_and_date()
    {
        var build = BuildInfo.From("1.2.3-beta.1+a1b2c3d", new Version(1, 2, 3, 0), "2026-09-01T10:00:00Z");

        Assert.Equal("1.2.3", build.Version);
        Assert.Equal("1.2.3-beta.1+a1b2c3d", build.InformationalVersion);
        Assert.Equal("a1b2c3d", build.Commit);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), build.BuildDate);
    }

    [Fact]
    public void A_development_build_stamps_nothing_beyond_its_version()
    {
        // Every local build, and what the web client renders as an absent row rather than a blank or
        // an error. The two version fields coincide here, which is why the client collapses them.
        var build = BuildInfo.From("1.0.0", new Version(1, 0, 0, 0), buildDate: null);

        Assert.Equal("1.0.0", build.Version);
        Assert.Equal("1.0.0", build.InformationalVersion);
        Assert.Null(build.Commit);
        Assert.Null(build.BuildDate);
    }

    [Theory]
    [InlineData("1.2.3+not.a.revision")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+abc")] // too short to be a revision anybody could look up
    public void Build_metadata_that_is_not_a_revision_is_not_reported_as_one(string informational)
    {
        // The SDK puts SourceRevisionId after the '+', but that field carries whatever else a build
        // chooses to put there. Showing an operator a commit that is not one is worse than showing
        // none: they would go looking for it.
        Assert.Null(BuildInfo.From(informational, new Version(1, 2, 3, 0), null).Commit);
    }

    [Fact]
    public void A_build_date_that_cannot_be_read_is_absent_rather_than_wrong()
    {
        Assert.Null(BuildInfo.From("1.2.3", new Version(1, 2, 3, 0), "last Tuesday").BuildDate);
    }

    [Fact]
    public void The_response_carries_exactly_the_four_fields_the_client_binds_to()
    {
        var body = JsonSerializer.Serialize(
            SystemEndpoints.ToDto(BuildInfo.From("1.2.3+a1b2c3d", new Version(1, 2, 3, 0), "2026-09-01T10:00:00Z")));

        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            ["version", "informationalVersion", "commit", "buildDate"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void This_assembly_reads_its_own_build_without_throwing()
    {
        // Current is a static initialiser behind the route; a reflection mistake here would surface
        // as a TypeInitializationException on the first request rather than at build time.
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Current.Version));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Current.InformationalVersion));
    }
}
