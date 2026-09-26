# Validation scenarios

Fifteen end-to-end behaviours the MVP is judged by. They are not test names and not a feature list:
each one is a promise about what an administrator or a household viewer experiences, written so it
can be settled by running something rather than by reading code.

The definition of done for the milestone is that all fifteen are **verified in execution** — the
behaviour is exercised against a real PostgreSQL instance and asserted on persisted state, not
asserted about in isolation.

This file is the authoritative in-repository list. `Cinomni.Recovery.Tests.ValidationScenarios`
carries the same fifteen entries as data, and `ValidationScenarioCatalogueTests` fails the build when
the numbered headings below stop matching that data, when the data cites a file that no longer
exists, or when a suite in `Cinomni.Recovery.Tests` claims a scenario number the catalogue does not
have.

What that does **not** cover is the prose under each heading. The suites, test names and scopes cited
in the bullets are matched against the data catalogue by review, not by a test, so a test that is
renamed rather than deleted is drift this file cannot detect on its own. Say precisely what the
build checks, because the whole point of the catalogue is that it does not overstate its own
coverage.

**Reviewed against the suite.** Every file and every test name cited below was checked to exist as
written, and each scope matches the one the data catalogue declares. What stays the product owner's
call is whether the phrasing states the intended requirement — that is a judgement about intent, not
something the repository can settle.

## How coverage is claimed

| Column | Meaning |
| --- | --- |
| **Verified by** | The test that exercises the behaviour. `Cinomni.Recovery.Tests` classes carry a `[Trait("Scenario", "NN")]`. |
| **Scope** | `end-to-end` — the whole chain in one execution. `module` — the behaviour proven inside its owning module against a real database. `unit` — pure state-machine logic, no I/O. |

A scenario covered at `module` or `unit` scope is genuinely verified for what that scope can settle.
Where an end-to-end proof is still missing, the entry says so rather than rounding up.

---

## 1. A movie is acquired without anyone watching

Adding a movie to the catalogue and asking for it is enough. Metadata is fetched, a search runs, one
release is chosen with the reasons for the choice written down, it downloads, it is imported into the
library under a name derived from the catalogue rather than from the torrent, and the title is
reported as available.

- **Verified by** `Cinomni.SeriesSlice.Tests/SeriesVerticalSliceTests.cs`
  (`Adding_a_movie_through_the_same_host_behaves_exactly_as_before`) — scope `end-to-end`
- Also `Cinomni.Decision.Tests/ReleaseEvaluatorTests.cs` for the persisted reasons and
  `Cinomni.Discovery.Tests/DiscoverySearchTests.cs`
  (`Cataloguing_and_monitoring_a_movie_drives_a_search_to_completion`).

## 2. A whole series is acquired from one download

A series monitored in full produces one goal per episode. A single season-pack download satisfies all
of them at once: one download task, one import job, one asset per episode, and each episode reported
available on its own.

- **Verified by** `Cinomni.SeriesSlice.Tests/SeriesVerticalSliceTests.cs`
  (`A_season_pack_is_searched_once_selected_once_downloaded_once_and_satisfies_every_episode`) —
  scope `end-to-end`
- Also `Cinomni.Import.Tests/SeasonPackImportTests.cs` for the per-file asset and event rules.

## 3. Only part of a season is wanted

Monitoring can be applied to some episodes and not others. The sweep composes searches only for the
episodes that are actually monitored; an unmonitored sibling produces no search, no candidate and no
download.

- **Verified by** `Cinomni.SeriesSlice.Tests/SeriesVerticalSliceTests.cs`
  (`Applying_first_season_monitors_only_season_one_and_opens_its_goals`) — scope `end-to-end`
- Also `Cinomni.Monitoring.Tests/SeriesCascadeTests.cs` for every cascade mode and
  `Cinomni.Monitoring.Tests/SeriesSearchSweepTests.cs`
  (`A_sparsely_missing_season_requests_per_episode_searches`) for what the sweep then composes.

## 4. An episode has not aired yet

An episode whose broadcast is in the future is never searched for. It stays monitored and silent
until its air date passes, so indexer quota is not spent looking for something that does not exist.

- **Verified by** `Cinomni.Monitoring.Tests/SeriesSearchSweepTests.cs`
  (`An_unaired_episode_is_never_searched`) — scope `module`

## 5. A better copy replaces the one already there

A title below the quality the profile asks for stays eligible for a better release. When one is
found it is downloaded and imported, the copy it replaces is set aside first, and once the profile's
cutoff is reached the title stops being searched for.

- **Verified by** `Cinomni.Monitoring.Tests/UpgradeLoopTests.cs`,
  `Cinomni.Monitoring.Tests/UpgradeSweepTests.cs` and
  `Cinomni.Import.Tests/UpgradeReplacementTests.cs` — scope `module`

## 6. A download fails

A transfer that fails does not lose the goal. The attempt is closed with the reason recorded, the
goal returns to searching, and the next sweep promotes the next candidate. Only when the configured
number of attempts is spent does the goal give up, and it says so.

- **Verified by** `Cinomni.Acquisition.Tests/AcquisitionIntentFsmTests.cs`
  (`A_download_failure_returns_to_searching_and_keeps_the_intent`,
  `The_goal_exhausts_only_after_its_attempts_run_out`) — scope `unit`
- Also `Cinomni.Downloads.Tests/DownloadFlowTests.cs` for the persisted failure and its event.

## 7. An import fails

Content that cannot be matched, or a file operation that cannot be verified, never leaves a partial
file in the library. The reason is persisted and readable afterwards, the goal returns to searching,
and a library root that is not mounted defers the job rather than failing it.

- **Verified by** `Cinomni.Import.Tests/ImportFlowTests.cs`
  (`A_file_operation_that_fails_to_verify_rolls_back_and_fails_the_import`) and
  `Cinomni.Import.Tests/ImportJobFsmTests.cs` — scope `module`

## 8. The file is already there

Landing a file on a path that is already occupied never silently overwrites it. The existing copy is
moved aside as a recorded, recoverable operation before the replacement lands, and a re-drive that
finds its own earlier work leaves it alone instead of filing it in the bin and linking it back.

- **Verified by** `Cinomni.Import.Tests/UpgradeReplacementTests.cs` and
  `Cinomni.Import.Tests/SeasonPackImportTests.cs`
  (`A_second_file_for_the_same_episode_is_refused_instead_of_overwriting_the_first`) — scope `module`

## 9. The torrent's file names say nothing useful

Files are matched by what they contain and by the numbering they carry, not by the release name the
torrent happens to use. The library path is composed from catalogue data, so a download whose folder
and files are named arbitrarily still lands each file at the right place under the right title.

- **Verified by** `Cinomni.Import.Tests/EpisodeFileParserTests.cs`,
  `Cinomni.Import.Tests/LibraryOrganizerTests.cs` and
  `Cinomni.Import.Tests/SeasonPackImportTests.cs` — scope `module`

## 10. A subtitle is missing

An asset registered without one of the languages the household asked for drives a subtitle search of
its own: candidates are scored, the best one over the threshold is fetched and attached, and a search
that finds nothing good enough stays reopenable rather than being recorded as a failure.

- **Verified by** `Cinomni.Subtitles.Tests/SubtitleFlowTests.cs`,
  `Cinomni.Subtitles.Tests/EpisodeSubtitleTests.cs` and
  `Cinomni.Subtitles.Tests/SubtitleSearchFsmTests.cs` — scope `module`

## 11. The client can play the file as it is

A client whose declared capabilities cover the file is given the file. The decision is persisted with
the reason for each property it turned on, and playback position survives — a viewer who comes back
resumes where they stopped.

- **Verified by** `Cinomni.Playback.Tests/PlaybackFlowTests.cs`
  (`A_compatible_client_gets_direct_play`, `Progress_is_tracked_and_resumed`),
  `Cinomni.Playback.Tests/PlaybackPlannerTests.cs` and
  `Cinomni.Playback.Tests/PlaybackProgressTests.cs` — scope `module`
- Resume is proven across sessions, which is what a viewer experiences. Resume across a **process
  restart** is not separately exercised; the position is an ordinary persisted row, so it survives
  for the same reason every other row does.

## 12. The client cannot play the file as it is

An incompatible container, codec or resolution plans a conversion instead, again with the reason for
each decision written down. A conversion that fails is contained: the session records it, nothing
else in the installation changes, and a second attempt is possible.

- **Verified by** `Cinomni.Playback.Tests/PlaybackFlowTests.cs`
  (`An_incompatible_codec_forces_a_transcode`, `An_unsupported_container_forces_a_remux`),
  `Cinomni.Playback.Tests/PlaybackPlannerTests.cs`,
  `Cinomni.Playback.Tests/PlaybackSessionFsmTests.cs` (`Fail_moves_to_failed`) and
  `Cinomni.Playback.Tests/TranscodeLifecycleTests.cs` — scope `module`
- The session records its own failure, and the failure is contained to the conversion's own
  resources: a start that fails gives its slot back and leaves no output
  (`A_transcode_that_cannot_start_gives_its_slot_back_and_leaves_no_output`), and one that fails part
  way is recorded on its job with FFmpeg's last words while every other stream carries on
  (`How_ffmpeg_ended_is_recorded_on_the_job_once`). A conversion interrupted by a **restart** is
  failed and its output removed, or picked up again if it had finished
  (`After_a_restart_…`). What no test asserts is the rest of the installation — Catalog, Library,
  Acquisition — being untouched by a failed conversion; nothing in Playback writes to them, which is
  the reason, not a proof.

## 13. The tunnel stops carrying torrent traffic

An installation that opted into a tunnel stops its transfers when their egress can no longer be
verified — including when the answer simply could not be taken, because an engine that cannot be
reached is not evidence of safety. Holding is not failing: no progress and no checkpoint is lost, the
acquisition goal is not sent back to searching, and the transfers are re-established from where they
were once egress is verified again — from their checkpoints, because the commonest cause of an
outage is the sidecar restarting and a sidecar that comes back holds nothing.

A restart is not a way around the guard. Startup recovery requires a positive, recent verification
rather than the absence of a hold, and takes an observation of its own before it decides, because
the verdict on disk was written by a process that no longer exists.

- **Verified by** `Cinomni.Downloads.Tests/TunnelWatchTests.cs` and
  `Cinomni.Downloads.Tests/TunnelHoldFsmTests.cs` — scope `module`
- The backend's own reaction across a restart is covered by
  `Cinomni.Recovery.Tests/EngineOutageScenarioTests.cs` and
  `Cinomni.Recovery.Tests/StartupEgressVerificationTests.cs` — scope `end-to-end`
- Under `PauseAndAlert` a startup observation that fails without crossing the hold threshold leaves
  the transfers stopped until the next start. That is deliberate — it fails closed — and it is not a
  state any test asserts a way out of.
- The network-layer kill-switch itself (interface binding, routing, packet filtering) is outside the
  backend and is not settled by any test in this repository.

## 14. The process restarts while a download is running

A restart does not cost a transfer. Every download the installation had in flight is handed back to
the engine from its stored checkpoint, no second task is created for the same attempt, the transfer
finishes afterwards and its goal completes normally. Restarting twice changes nothing a second time.

On this path the checkpoint is the only thing that reaches the engine — the download reference is not
sent once resume data exists — so it decides which torrent is *resumed* and never which torrent the
task *is*: an answer that is empty, or that names a different torrent than the task is bound to, is
refused and the task is left as it was. A magnet whose metadata resolves on that very re-add has its
layout recorded and announced there, which is the only moment the platform could capture it.

- **Verified by** `Cinomni.Recovery.Tests/RestartDuringDownloadScenarioTests.cs` and
  `Cinomni.Recovery.Tests/RestartWhileMetadataResolvesTests.cs` — scope `end-to-end`
- Whether libtorrent itself resumes from a `.fastresume` blob written by an earlier session is a
  property of the sidecar, exercised by the PoC and not by any test here. What these suites settle is
  that the platform stores the checkpoint, hands the right one back, and believes the answer only as
  far as it should.

## 15. The process restarts while an import is running

A restart in the middle of landing a season pack never duplicates a file and never loses one. The
files that landed before the interruption stay landed and are not announced again, the ones that did
not are re-driven after the restart, and the job ends with exactly one asset per episode. The
re-drive resolves against the units the acquisition asked for, so a pack that ships more episodes
than were requested still lands only the requested ones.

- **Verified by** `Cinomni.Recovery.Tests/RestartDuringImportScenarioTests.cs` — scope `end-to-end`
- A job that can never land is re-driven five times and then parked for a manual import with the
  reason recorded, rather than being retried at every start for ever.
- The source path a re-drive replays is confined to the configured staging root. The first drive's
  own path — the one that arrives with the completed download — is not separately confined; the
  torrent name it is composed from is sanitised where it is recorded, which closes the reachable
  case rather than the general one.

---

## Recovery cases underneath the fifteen

These are not numbered scenarios. They are the platform properties several of the fifteen rest on,
and they have their own tests because a failure here would show up as an unexplainable failure above.

| Case | Verified by |
| --- | --- |
| A command left running by a crash is requeued and runs once | `Cinomni.Recovery.Tests/PlatformRestartRecoveryTests.cs`, `Cinomni.Operations.Tests/CommandQueueTests.cs` |
| Outbox rows a crash left unpublished are delivered after a restart | `Cinomni.Recovery.Tests/PlatformRestartRecoveryTests.cs` |
| A redelivered event after a restart changes nothing a second time | `Cinomni.Recovery.Tests/PlatformRestartRecoveryTests.cs` |
| A restart re-registers the scheduled jobs without resetting their clock | `Cinomni.Recovery.Tests/PlatformRestartRecoveryTests.cs` |
| A metadata provider that is down backs off instead of corrupting the work | `Cinomni.Metadata.Tests/MetadataFlowTests.cs` |
| An indexer that fails does not fail the search or the other indexers | `Cinomni.Discovery.Tests/DiscoverySearchTests.cs` |
| The engine being unreachable does not take the backend down | `Cinomni.Recovery.Tests/EngineOutageScenarioTests.cs` |
| A restart re-arms nothing until this process has verified egress | `Cinomni.Recovery.Tests/StartupEgressVerificationTests.cs` |
| A tampered or unreadable checkpoint cannot re-point a task at other content | `Cinomni.Recovery.Tests/RestartDuringDownloadScenarioTests.cs` |
| A job that can never land is parked instead of re-driven for ever | `Cinomni.Recovery.Tests/RestartDuringImportScenarioTests.cs` |
| A source path outside the staging root is refused rather than scanned | `Cinomni.Recovery.Tests/RestartDuringImportScenarioTests.cs` |
