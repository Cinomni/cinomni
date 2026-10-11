import type { SubtitleSearchSummary, SubtitleSync } from '@/api/types'
import type { Tone } from '@/ui/Badge'

export interface SyncBadge {
  label: string
  tone: Tone
}

/** "+1.24 s" / "−0.30 s": the offset as a viewer would describe it. */
export function formatOffset(offsetMs: number): string {
  const seconds = (Math.abs(offsetMs) / 1000).toFixed(2)
  return `${offsetMs < 0 ? '−' : '+'}${seconds} s`
}

/**
 * "25→23.976" for a subtitle timed for a 25 fps release on a 23.976 fps video, and the reverse. The
 * backend only ever chooses one of these two conversions, so anything else is shown as a plain ratio.
 */
export function formatRatio(ratio: number): string | null {
  if (Math.abs(ratio - 1) < 1e-6) return null
  if (Math.abs(ratio - (25 * 1001) / 24000) < 1e-6) return '25→23.976'
  if (Math.abs(ratio - 24000 / (25 * 1001)) < 1e-6) return '23.976→25'
  return `×${ratio.toFixed(4)}`
}

/**
 * The short timing badge for one search row. The backend decided; this only names the decision. A
 * search still synchronizing has no decision yet, and one with no landed subtitle has no timing at all.
 */
export function syncBadge(search: SubtitleSearchSummary): SyncBadge | null {
  const sync = search.subtitle?.sync
  if (search.state === 'Syncing') return { label: 'Checking timing…', tone: 'info' }
  if (!sync) return null
  return badgeFor(sync)
}

function badgeFor(sync: SubtitleSync): SyncBadge {
  switch (sync.outcome) {
    case 'Applied': {
      const ratio = formatRatio(sync.ratio)
      return {
        label: `Synced ${formatOffset(sync.offsetMs)}${ratio ? ` (${ratio})` : ''}`,
        tone: 'success',
      }
    }
    case 'AlreadyInSync':
      return { label: 'Already in sync', tone: 'success' }
    case 'LowConfidence':
      return { label: 'Original kept: low confidence', tone: 'warning' }
    case 'Failed':
      // A failed re-check keeps whatever was playing, which may be an earlier synchronized copy.
      return {
        label: sync.hasOriginal ? 'Sync failed: previous timing kept' : 'Original kept: sync failed',
        tone: 'warning',
      }
    case 'Unsupported':
      return { label: 'Not synced', tone: 'neutral' }
    case 'Reverted':
      return { label: 'Original restored', tone: 'neutral' }
  }
}
