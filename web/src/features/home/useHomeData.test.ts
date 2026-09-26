import { describe, expect, it } from 'vitest'
import type { PlaybackProgressView } from '@/api/types'
import { aWork } from '@/test/factories'
import { continueWatching, pickFeatured, progressFraction } from './useHomeData'

function aProgress(
  assetId: string,
  workId: string | null,
  positionTicks: number,
  durationTicks = 0,
): PlaybackProgressView {
  return {
    assetId,
    positionTicks,
    played: false,
    playCount: 0,
    unitId: null,
    durationTicks,
    workId,
    updatedAt: null,
  }
}

describe('continueWatching', () => {
  it('continueWatching_keeps_one_entry_per_title_in_the_servers_order', () => {
    const works = [aWork({ id: 'w1', title: 'Arrival' }), aWork({ id: 'w2', kind: 'Series', title: 'Dark' })]
    // Most recent first, as the server answers: two episodes of the same show, then a film.
    const progress = [aProgress('a2', 'w2', 9_000), aProgress('a3', 'w2', 7_000), aProgress('a1', 'w1', 5_000, 10_000)]

    const items = continueWatching(works, progress)

    // The most recent episode stands for the show; the others keep their place.
    expect(items.map((item) => [item.work.title, item.assetId])).toEqual([
      ['Dark', 'a2'],
      ['Arrival', 'a1'],
    ])
    expect(items[1]).toMatchObject({ positionTicks: 5_000, durationTicks: 10_000 })
  })

  it('skips rows it cannot name: an unknown work, or none recorded', () => {
    const items = continueWatching([aWork({ id: 'w1' })], [aProgress('a1', 'hidden', 5_000), aProgress('a2', null, 5_000)])

    expect(items).toEqual([])
  })
})

describe('progressFraction', () => {
  it('is the share watched, capped at the end, and unknown without a runtime', () => {
    expect(progressFraction({ positionTicks: 250, durationTicks: 1_000 })).toBe(0.25)
    expect(progressFraction({ positionTicks: 1_200, durationTicks: 1_000 })).toBe(1)
    expect(progressFraction({ positionTicks: 250, durationTicks: 0 })).toBeNull()
  })
})

describe('pickFeatured', () => {
  it('pickFeatured_prefers_the_newest_watchable_title_with_a_backdrop', () => {
    // UUIDv7-style ids: a later id is a later addition.
    const works = [
      aWork({ id: '0190-a', title: 'Old and ready', hasAsset: true, backdropUrl: '/b1' }),
      aWork({ id: '0190-c', title: 'New, not downloaded', hasAsset: false, backdropUrl: '/b3' }),
      aWork({ id: '0190-b', title: 'Newer and ready', hasAsset: true, backdropUrl: '/b2' }),
    ]

    expect(pickFeatured(works)?.title).toBe('Newer and ready')
  })

  it('falls back to any backdrop, then to the newest title, then to nothing', () => {
    expect(pickFeatured([aWork({ id: '1', backdropUrl: '/b' }), aWork({ id: '2' })])?.id).toBe('1')
    expect(pickFeatured([aWork({ id: '1' }), aWork({ id: '2' })])?.id).toBe('2')
    expect(pickFeatured([])).toBeNull()
  })
})
