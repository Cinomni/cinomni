import { describe, expect, it } from 'vitest'
import type { NextUpEpisode } from '@/api/types'
import { secondsToTicks } from '@/lib/format'
import { primaryActionFor } from './primaryAction'

const movie = { id: 'movie-1', kind: 'Movie' as const }
const series = { id: 'series-1', kind: 'Series' as const }

function aNextUp(overrides: Partial<NextUpEpisode> = {}): NextUpEpisode {
  return {
    workId: 'series-1',
    unitId: 'episode-1',
    assetId: 'asset-9',
    seasonNumber: 2,
    episodeNumber: 3,
    episodeTitle: null,
    resumePositionTicks: 0,
    ...overrides,
  }
}

describe('primaryActionFor', () => {
  it('primaryActionFor_resumes_a_movie_the_viewer_stopped_part_way_through', () => {
    const action = primaryActionFor({
      work: movie,
      playableAssetId: 'asset-1',
      resumePositionTicks: secondsToTicks(42 * 60 + 10),
    })

    expect(action).toEqual({ kind: 'resume', label: 'Resume · 42:10', href: '/watch/asset-1' })
  })

  it('plays from the start when the stored position is only a false start', () => {
    const action = primaryActionFor({ work: movie, playableAssetId: 'asset-1', resumePositionTicks: secondsToTicks(5) })

    expect(action).toEqual({ kind: 'play', label: 'Play', href: '/watch/asset-1' })
  })

  it('offers the details page, not a play button, when there is no file', () => {
    const action = primaryActionFor({ work: movie, playableAssetId: null })

    expect(action).toEqual({ kind: 'details', label: 'View details', href: '/works/movie-1' })
  })

  it('names the next episode of a series, and resumes it when it was started', () => {
    expect(primaryActionFor({ work: series, nextUp: aNextUp() })).toEqual({
      kind: 'play',
      label: 'Play S02E03',
      href: '/watch/asset-9',
    })
    expect(
      primaryActionFor({ work: series, nextUp: aNextUp({ resumePositionTicks: secondsToTicks(600) }) }).label,
    ).toBe('Resume S02E03')
  })

  it('never plays a series from a movie-shaped asset id', () => {
    const action = primaryActionFor({ work: series, playableAssetId: 'asset-1', nextUp: null })

    expect(action.kind).toBe('details')
    expect(action.href).toBe('/series/series-1')
  })
})
