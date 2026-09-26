import type { NextUpEpisode, Work } from '@/api/types'
import { formatDuration, formatEpisodeCode, ticksToSeconds } from '@/lib/format'
import { workPath } from '@/lib/routes'

export type PrimaryActionKind = 'resume' | 'play' | 'details'

export interface PrimaryAction {
  kind: PrimaryActionKind
  label: string
  href: string
}

/** Below this, a stored position is a start that never got going, not a place to resume from. */
const MIN_RESUME_SECONDS = 30

function resumable(positionTicks: number | null | undefined): positionTicks is number {
  return positionTicks != null && ticksToSeconds(positionTicks) >= MIN_RESUME_SECONDS
}

/**
 * The one action a title's hero leads with, chosen from state the server already decided: where the
 * viewer stopped, which episode is next, whether a file exists. Resume beats play, play beats reading
 * about it. The label carries the specifics ("Resume · 42:10", "Play S02E03") so the button alone says
 * what will happen.
 */
export function primaryActionFor({
  work,
  playableAssetId,
  resumePositionTicks,
  nextUp,
}: {
  work: Pick<Work, 'id' | 'kind'>
  /** A movie's playable file, when there is one. */
  playableAssetId?: string | null
  /** Where the viewer stopped in that file, if anywhere. */
  resumePositionTicks?: number | null
  /** A series' next episode for this viewer, as the playback module computed it. */
  nextUp?: NextUpEpisode | null
}): PrimaryAction {
  if (work.kind === 'Series' && nextUp) {
    const code = formatEpisodeCode(nextUp.seasonNumber, nextUp.episodeNumber)
    return resumable(nextUp.resumePositionTicks)
      ? { kind: 'resume', label: `Resume ${code}`, href: `/watch/${nextUp.assetId}` }
      : { kind: 'play', label: `Play ${code}`, href: `/watch/${nextUp.assetId}` }
  }
  if (work.kind !== 'Series' && playableAssetId) {
    return resumable(resumePositionTicks)
      ? {
          kind: 'resume',
          label: `Resume · ${formatDuration(ticksToSeconds(resumePositionTicks))}`,
          href: `/watch/${playableAssetId}`,
        }
      : { kind: 'play', label: 'Play', href: `/watch/${playableAssetId}` }
  }
  return { kind: 'details', label: 'View details', href: workPath(work) }
}
