import type { Tone } from '@/ui/Badge'
import type {
  DownloadState,
  IntentState,
  MediaRequestStatus,
  SubtitleState,
  Verdict,
  Work,
} from '@/api/types'

/**
 * How much of a title is actually on disk. A series is almost never all-or-nothing, so a boolean
 * would turn green on its first episode — the middle state is the whole point.
 */
export type Availability = 'none' | 'partial' | 'complete'

export const availabilityTone: Record<Availability, Tone> = {
  none: 'neutral',
  partial: 'warning',
  complete: 'success',
}

/** Dot colour for the compact indicator on a poster. */
export const availabilityDot: Record<Availability, string> = {
  none: 'bg-faint',
  partial: 'bg-warning',
  complete: 'bg-success',
}

export const availabilityLabel: Record<Availability, string> = {
  none: 'Not downloaded',
  partial: 'Partly in library',
  complete: 'In library',
}

/**
 * A work's availability from its rollup counters. A movie (and a series whose structure has not been
 * synced yet, where the counters are still 0) falls back to the boolean the movie slice shipped.
 */
export function availabilityOf(work: Work): Availability {
  if (work.kind !== 'Series' || work.episodeCount === 0) {
    return work.hasAsset ? 'complete' : 'none'
  }
  if (work.availableEpisodeCount <= 0) return 'none'
  return work.availableEpisodeCount >= work.episodeCount ? 'complete' : 'partial'
}

/** What a single episode row reports: on disk, wanted but absent, or not broadcast yet. */
export type EpisodeAvailability = 'InLibrary' | 'Missing' | 'Unaired'

export const episodeAvailabilityTone: Record<EpisodeAvailability, Tone> = {
  InLibrary: 'success',
  Missing: 'warning',
  Unaired: 'neutral',
}

export const episodeAvailabilityLabel: Record<EpisodeAvailability, string> = {
  InLibrary: 'In library',
  Missing: 'Missing',
  Unaired: 'Unaired',
}

export const downloadTone: Record<DownloadState, Tone> = {
  Queued: 'neutral',
  ResolvingMetadata: 'info',
  Checking: 'info',
  Downloading: 'info',
  Completed: 'success',
  Seeding: 'success',
  Paused: 'warning',
  Removed: 'neutral',
  Error: 'danger',
}

export const intentTone: Record<IntentState, Tone> = {
  Requested: 'neutral',
  Planned: 'neutral',
  Searching: 'info',
  CandidateSelected: 'info',
  Downloading: 'info',
  Importing: 'info',
  Available: 'success',
  Exhausted: 'danger',
  Cancelled: 'neutral',
}

export const requestTone: Record<MediaRequestStatus, Tone> = {
  Pending: 'warning',
  Approved: 'info',
  Rejected: 'neutral',
  Available: 'success',
}

export const verdictTone: Record<Verdict, Tone> = {
  Accepted: 'success',
  RejectedPermanent: 'danger',
  RejectedTemporary: 'warning',
}

/**
 * A verdict in words. The temporary one is the interesting case: the release was not wrong, it just
 * was not better than what is already there — and something better may simply not be posted yet.
 */
export const verdictLabel: Record<Verdict, string> = {
  Accepted: 'Accepted',
  RejectedPermanent: 'Rejected',
  RejectedTemporary: 'Not yet',
}

export const subtitleTone: Record<SubtitleState, Tone> = {
  Requested: 'neutral',
  Searching: 'info',
  Evaluated: 'info',
  Downloading: 'info',
  Syncing: 'info',
  NotFound: 'warning',
  Available: 'success',
}

/**
 * The shared state vocabulary of the media surfaces. Every state has its own glyph shape (see
 * `StatusGlyph`), so meaning never rests on color alone and the same word means the same thing on the
 * home page, a poster and an episode row.
 */
export type MediaState =
  | 'available'
  | 'partial'
  | 'notInLibrary'
  | 'missing'
  | 'upcoming'
  | 'queued'
  | 'downloading'
  | 'failed'
  | 'watched'
  | 'inProgress'
  | 'monitored'

export const mediaStateTone: Record<MediaState, Tone> = {
  available: 'success',
  partial: 'success',
  notInLibrary: 'neutral',
  missing: 'warning',
  upcoming: 'neutral',
  queued: 'neutral',
  downloading: 'info',
  failed: 'danger',
  watched: 'neutral',
  inProgress: 'accent',
  monitored: 'neutral',
}

export const mediaStateFromAvailability: Record<Availability, MediaState> = {
  none: 'notInLibrary',
  partial: 'partial',
  complete: 'available',
}

export const mediaStateFromEpisode: Record<EpisodeAvailability, MediaState> = {
  InLibrary: 'available',
  Missing: 'missing',
  Unaired: 'upcoming',
}

/** The five stages a title travels to reach the library, as the household reads them. */
export const PIPELINE_STAGES = ['Search', 'Grab', 'Download', 'Import', 'Library'] as const
export type PipelineStage = (typeof PIPELINE_STAGES)[number]

/**
 * Where an acquisition intent stands on that path. A presentation of the state the backend already
 * decided — this never infers a state, it only places the one it was given on the line:
 * `reached` is the furthest stage entered, `status` says whether it is under way, done or stuck there.
 */
export interface PipelinePosition {
  reached: number
  status: 'waiting' | 'active' | 'done' | 'failed'
}

export const intentPipeline: Record<IntentState, PipelinePosition> = {
  Requested: { reached: 0, status: 'waiting' },
  Planned: { reached: 0, status: 'waiting' },
  Searching: { reached: 0, status: 'active' },
  CandidateSelected: { reached: 1, status: 'active' },
  Downloading: { reached: 2, status: 'active' },
  Importing: { reached: 3, status: 'active' },
  Available: { reached: 4, status: 'done' },
  Exhausted: { reached: 0, status: 'failed' },
  Cancelled: { reached: 0, status: 'waiting' },
}

/** An intent's state in words a viewer understands, not the enum name. */
export const intentLabel: Record<IntentState, string> = {
  Requested: 'Waiting to search',
  Planned: 'Waiting to search',
  Searching: 'Searching for a release',
  CandidateSelected: 'Release chosen',
  Downloading: 'Downloading',
  Importing: 'Importing into the library',
  Available: 'In library',
  Exhausted: 'No acceptable release found',
  Cancelled: 'Removed from the catalog',
}

/** A transfer's state in words, and the shared state it maps to for its glyph. */
export const downloadLabel: Record<DownloadState, string> = {
  Queued: 'Queued',
  ResolvingMetadata: 'Fetching torrent details',
  Checking: 'Checking files',
  Downloading: 'Downloading',
  Completed: 'Completed',
  Seeding: 'Seeding',
  Paused: 'Paused',
  Removed: 'Removed',
  Error: 'Failed',
}

export const downloadMediaState: Record<DownloadState, MediaState> = {
  Queued: 'queued',
  ResolvingMetadata: 'queued',
  Checking: 'downloading',
  Downloading: 'downloading',
  Completed: 'available',
  Seeding: 'available',
  Paused: 'queued',
  Removed: 'notInLibrary',
  Error: 'failed',
}
