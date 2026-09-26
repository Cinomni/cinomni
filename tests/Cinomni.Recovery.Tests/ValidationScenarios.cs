namespace Cinomni.Recovery.Tests;

/// <summary>
/// One of the fifteen validation scenarios the MVP is judged by, as data.
/// </summary>
/// <param name="Number">Its position in the catalogue, 1 to 15.</param>
/// <param name="Name">A short title, matching the heading in <c>tests/VALIDATION-SCENARIOS.md</c>.</param>
/// <param name="Statement">
/// What the product must do, in one sentence. Deliberately written as an observable outcome rather
/// than as a description of the code that produces it.
/// </param>
/// <param name="VerifiedBy">
/// The test files that exercise it, relative to <c>tests/</c>. Every one is checked to exist, so a
/// suite that is renamed or deleted fails this catalogue instead of quietly leaving a scenario
/// unproven.
/// </param>
/// <param name="Scope">How far the proof reaches — see <see cref="ScenarioScope"/>.</param>
public sealed record ValidationScenario(
    int Number,
    string Name,
    string Statement,
    IReadOnlyList<string> VerifiedBy,
    ScenarioScope Scope);

/// <summary>How far a scenario's proof reaches. Ordered weakest to strongest.</summary>
public enum ScenarioScope
{
    /// <summary>Pure state-machine logic, no I/O. Settles the rules, not the wiring.</summary>
    Unit = 1,

    /// <summary>The behaviour inside its owning module, against a real database.</summary>
    Module = 2,

    /// <summary>The whole chain in one execution, against a real database.</summary>
    EndToEnd = 3,
}

/// <summary>
/// The authoritative in-repository catalogue of the fifteen validation scenarios.
/// <para>
/// It exists here, as code, so that "which scenarios are covered" is a question the build can answer.
/// <c>tests/VALIDATION-SCENARIOS.md</c> is the same list for a human reader, and
/// <see cref="ValidationScenarioCatalogueTests"/> fails when its headings disagree with this data or
/// when a file cited <em>here</em> no longer exists.
/// </para>
/// <para>
/// The bullets under each heading in that document — the test names it quotes and the scope it
/// claims — are not parsed by anything. They are kept true by review, so a rename that leaves the
/// file in place is the one drift the build cannot see.
/// </para>
/// <para>
/// The wording is Cinomni's own, written from the requirement each scenario states. It is not a
/// translation of any design document. The citations were reviewed against the suite; whether the
/// phrasing states the intended requirement remains the product owner's call.
/// </para>
/// </summary>
public static class ValidationScenarios
{
    public const int ExpectedCount = 15;

    public static IReadOnlyList<ValidationScenario> All { get; } =
    [
        new(1,
            "A movie is acquired without anyone watching",
            "Cataloguing and monitoring a movie is enough for it to be searched, chosen with the "
            + "reasons recorded, downloaded, imported and reported available.",
            [
                "Cinomni.SeriesSlice.Tests/SeriesVerticalSliceTests.cs",
                "Cinomni.Decision.Tests/ReleaseEvaluatorTests.cs",
                "Cinomni.Discovery.Tests/DiscoverySearchTests.cs",
            ],
            ScenarioScope.EndToEnd),

        new(2,
            "A whole series is acquired from one download",
            "A single season-pack download satisfies every episode goal it covers, landing one asset "
            + "per episode and announcing each on its own.",
            [
                "Cinomni.SeriesSlice.Tests/SeriesVerticalSliceTests.cs",
                "Cinomni.Import.Tests/SeasonPackImportTests.cs",
            ],
            ScenarioScope.EndToEnd),

        new(3,
            "Only part of a season is wanted",
            "The sweep composes searches only for the episodes actually monitored; an unmonitored "
            + "sibling produces no search and no download.",
            [
                "Cinomni.SeriesSlice.Tests/SeriesVerticalSliceTests.cs",
                "Cinomni.Monitoring.Tests/SeriesCascadeTests.cs",
                "Cinomni.Monitoring.Tests/SeriesSearchSweepTests.cs",
            ],
            ScenarioScope.EndToEnd),

        new(4,
            "An episode has not aired yet",
            "An episode whose broadcast is in the future is never searched for, and stays monitored "
            + "and silent until its air date passes.",
            ["Cinomni.Monitoring.Tests/SeriesSearchSweepTests.cs"],
            ScenarioScope.Module),

        new(5,
            "A better copy replaces the one already there",
            "A title below the profile's cutoff stays eligible, a better release replaces it with the "
            + "previous copy set aside first, and reaching the cutoff stops the searching.",
            [
                "Cinomni.Monitoring.Tests/UpgradeLoopTests.cs",
                "Cinomni.Monitoring.Tests/UpgradeSweepTests.cs",
                "Cinomni.Import.Tests/UpgradeReplacementTests.cs",
            ],
            ScenarioScope.Module),

        new(6,
            "A download fails",
            "A failed transfer closes its attempt with the reason recorded and returns the goal to "
            + "searching; only a spent attempt budget gives the goal up, and it says so.",
            [
                "Cinomni.Acquisition.Tests/AcquisitionIntentFsmTests.cs",
                "Cinomni.Downloads.Tests/DownloadFlowTests.cs",
            ],
            ScenarioScope.Unit),

        new(7,
            "An import fails",
            "Content that cannot be matched, or an operation that cannot be verified, leaves no "
            + "partial file, records why, and returns the goal to searching.",
            [
                "Cinomni.Import.Tests/ImportFlowTests.cs",
                "Cinomni.Import.Tests/ImportJobFsmTests.cs",
            ],
            ScenarioScope.Module),

        new(8,
            "The file is already there",
            "A landing on an occupied path sets the existing copy aside as a recorded, recoverable "
            + "operation before the replacement lands, and never silently overwrites it.",
            [
                "Cinomni.Import.Tests/UpgradeReplacementTests.cs",
                "Cinomni.Import.Tests/SeasonPackImportTests.cs",
            ],
            ScenarioScope.Module),

        new(9,
            "The torrent's file names say nothing useful",
            "Files are matched by content and numbering rather than by the release name, and the "
            + "library path is composed from catalogue data.",
            [
                "Cinomni.Import.Tests/EpisodeFileParserTests.cs",
                "Cinomni.Import.Tests/LibraryOrganizerTests.cs",
                "Cinomni.Import.Tests/SeasonPackImportTests.cs",
            ],
            ScenarioScope.Module),

        new(10,
            "A subtitle is missing",
            "An asset missing a wanted language drives its own subtitle search; nothing over the "
            + "threshold leaves the search reopenable rather than failed.",
            [
                "Cinomni.Subtitles.Tests/SubtitleFlowTests.cs",
                "Cinomni.Subtitles.Tests/EpisodeSubtitleTests.cs",
                "Cinomni.Subtitles.Tests/SubtitleSearchFsmTests.cs",
            ],
            ScenarioScope.Module),

        new(11,
            "The client can play the file as it is",
            "A capable client is given the file, the decision is persisted with a reason per property, "
            + "and playback position survives so a viewer resumes where they stopped.",
            [
                "Cinomni.Playback.Tests/PlaybackFlowTests.cs",
                "Cinomni.Playback.Tests/PlaybackPlannerTests.cs",
                "Cinomni.Playback.Tests/PlaybackProgressTests.cs",
            ],
            ScenarioScope.Module),

        new(12,
            "The client cannot play the file as it is",
            "An incompatible file plans a conversion with its reasons recorded, and a conversion that "
            + "fails is contained to its own session.",
            [
                "Cinomni.Playback.Tests/PlaybackFlowTests.cs",
                "Cinomni.Playback.Tests/PlaybackPlannerTests.cs",
                "Cinomni.Playback.Tests/PlaybackSessionFsmTests.cs",
                "Cinomni.Playback.Tests/TranscodeLifecycleTests.cs",
            ],
            ScenarioScope.Module),

        new(13,
            "The tunnel stops carrying torrent traffic",
            "Transfers stop when their egress cannot be verified, lose no progress or checkpoint, do "
            + "not fail their acquisition goal, and are put back on the network only once egress is "
            + "verified again — including after a restart, which requires a verification of its own.",
            [
                "Cinomni.Downloads.Tests/TunnelWatchTests.cs",
                "Cinomni.Downloads.Tests/TunnelHoldFsmTests.cs",
                "Cinomni.Recovery.Tests/EngineOutageScenarioTests.cs",
                "Cinomni.Recovery.Tests/StartupEgressVerificationTests.cs",
            ],
            ScenarioScope.EndToEnd),

        new(14,
            "The process restarts while a download is running",
            "Every in-flight transfer is handed back to the engine from its stored checkpoint, no "
            + "second task is created, and the download finishes and completes its goal afterwards. "
            + "The checkpoint decides which torrent is resumed, never which torrent the task is.",
            [
                "Cinomni.Recovery.Tests/RestartDuringDownloadScenarioTests.cs",
                "Cinomni.Recovery.Tests/RestartWhileMetadataResolvesTests.cs",
            ],
            ScenarioScope.EndToEnd),

        new(15,
            "The process restarts while an import is running",
            "A restart mid-import duplicates no file and loses none: what landed stays landed and is "
            + "not announced again, what did not is re-driven against the units the acquisition asked "
            + "for, and the job ends with one asset per unit.",
            ["Cinomni.Recovery.Tests/RestartDuringImportScenarioTests.cs"],
            ScenarioScope.EndToEnd),
    ];
}
