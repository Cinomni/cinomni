import { describe, expect, it } from 'vitest'
import type { SubtitleSearchSummary } from '@/api/types'
import { formatOffset, formatRatio, syncBadge } from './subtitleSync'

function row(overrides: Partial<SubtitleSearchSummary>): SubtitleSearchSummary {
  return {
    id: 'search-1',
    assetId: 'asset-1',
    language: 'en',
    forced: false,
    hearingImpaired: false,
    state: 'Available',
    attempts: 1,
    subtitle: null,
    ...overrides,
  }
}

describe('subtitleSync', () => {
  it('formatOffset_signs_the_offset_in_seconds', () => {
    expect(formatOffset(1240)).toBe('+1.24 s')
    expect(formatOffset(-300)).toBe('−0.30 s')
    expect(formatOffset(0)).toBe('+0.00 s')
  })

  it('formatRatio_names_the_two_pal_conversions_and_nothing_for_same_speed', () => {
    expect(formatRatio(1)).toBeNull()
    expect(formatRatio((25 * 1001) / 24000)).toBe('25→23.976')
    expect(formatRatio(24000 / (25 * 1001))).toBe('23.976→25')
  })

  it('syncBadge_has_nothing_to_say_without_a_landed_subtitle_or_a_decision', () => {
    expect(syncBadge(row({}))).toBeNull()
    expect(syncBadge(row({ subtitle: { id: 's', format: 'Srt', sync: null } }))).toBeNull()
    expect(syncBadge(row({ state: 'Syncing', subtitle: { id: 's', format: 'Srt', sync: null } }))?.label).toBe(
      'Checking timing…',
    )
  })

  it('syncBadge_does_not_claim_the_original_was_kept_when_a_failed_recheck_kept_a_synced_copy', () => {
    const failed = (hasOriginal: boolean) =>
      syncBadge(
        row({
          subtitle: {
            id: 's',
            format: 'Srt',
            sync: { outcome: 'Failed', offsetMs: 0, ratio: 1, score: 0, reason: 'x', hasOriginal, decidedAt: '' },
          },
        }),
      )?.label

    expect(failed(false)).toBe('Original kept: sync failed')
    expect(failed(true)).toBe('Sync failed: previous timing kept')
  })
})
