using Cinomni.Notifications.Application;
using Cinomni.Notifications.Contracts;
using Cinomni.Notifications.Messaging;

namespace Cinomni.Notifications.Tests;

/// <summary>Pure unit tests for the notification message composer (no database, no I/O).</summary>
public sealed class NotificationComposerTests
{
    [Fact]
    public void Media_available_is_a_success_titled_by_the_work()
    {
        var composed = NotificationComposer.Compose(NotificationKind.MediaAvailable, "The Matrix", null);

        Assert.Equal("media-available", composed.Type);
        Assert.Equal(NotificationSeverity.Success, composed.Severity);
        Assert.Equal("The Matrix is ready to watch", composed.Title);
    }

    [Fact]
    public void Acquisition_failed_is_an_error_carrying_the_reason()
    {
        var composed = NotificationComposer.Compose(NotificationKind.AcquisitionFailed, "Dune", "No release met the quality profile.");

        Assert.Equal(NotificationSeverity.Error, composed.Severity);
        Assert.Equal("Couldn't acquire Dune", composed.Title);
        Assert.Equal("No release met the quality profile.", composed.Body);
    }

    [Fact]
    public void Acquisition_failed_falls_back_when_no_reason_is_given()
    {
        var composed = NotificationComposer.Compose(NotificationKind.AcquisitionFailed, "Dune", null);

        Assert.Equal("No acceptable release was found.", composed.Body);
    }

    [Fact]
    public void Provider_degraded_is_a_warning_naming_the_provider()
    {
        var composed = NotificationComposer.Compose(NotificationKind.ProviderDegraded, "Dune", "tmdb");

        Assert.Equal(NotificationSeverity.Warning, composed.Severity);
        Assert.Equal("Metadata provider tmdb is degraded", composed.Title);
        Assert.Contains("Dune", composed.Body);
    }

    [Fact]
    public void A_new_request_is_an_info_carrying_who_asked_for_what()
    {
        var composed = NotificationComposer.Compose(NotificationKind.MediaRequested, null, "alice requested Dune (2021).");

        Assert.Equal("media-requested", composed.Type);
        Assert.Equal(NotificationSeverity.Info, composed.Severity);
        Assert.Equal("A title is waiting for approval", composed.Title);
        Assert.Equal("alice requested Dune (2021).", composed.Body);
    }

    [Fact]
    public void A_missing_work_title_degrades_gracefully()
    {
        var composed = NotificationComposer.Compose(NotificationKind.MediaAvailable, null, null);

        Assert.Equal("A title is ready to watch", composed.Title);
    }
}
