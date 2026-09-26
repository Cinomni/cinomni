import { describe, expect, it, vi } from 'vitest'
import { act } from '@testing-library/react'
import { openRealtimeStream } from '@/lib/realtime'
import { seriesKeys } from '@/features/series/useSeries'
import { renderWithProviders, testQueryClient } from '@/test/render'
import { RealtimeProvider } from './RealtimeProvider'

vi.mock('@/lib/realtime', () => ({ openRealtimeStream: vi.fn(() => () => undefined) }))

describe('RealtimeProvider download progress', () => {
  const frame = {
    id: 'task-1',
    infoHash: 'abc',
    name: 'Arrival.2016.1080p',
    state: 'Downloading',
    progress: 0.5,
    downloadRate: 1,
    uploadRate: 0,
    numPeers: 1,
    numSeeds: 1,
    networkHeld: false,
  }

  it('RealtimeProvider_writes_a_well_formed_snapshot_into_the_downloads_cache', () => {
    const queryClient = testQueryClient()
    renderWithProviders(<RealtimeProvider>{null}</RealtimeProvider>, queryClient)
    const stream = vi.mocked(openRealtimeStream).mock.lastCall?.[0]

    act(() => stream?.onMessage({ topic: 'downloads.progress', payload: [{ ...frame, intentIds: ['i1'] }] }))

    expect(queryClient.getQueryData(['downloads'])).toEqual([{ ...frame, intentIds: ['i1'] }])
  })

  it('RealtimeProvider_keeps_the_last_good_snapshot_when_a_frame_lacks_the_goals_it_serves', () => {
    // Activity walks intentIds to place each transfer under its title; a frame without them would
    // crash that page instead of leaving the previous figures up.
    const queryClient = testQueryClient()
    queryClient.setQueryData(['downloads'], 'last-good')
    renderWithProviders(<RealtimeProvider>{null}</RealtimeProvider>, queryClient)
    const stream = vi.mocked(openRealtimeStream).mock.lastCall?.[0]

    act(() => stream?.onMessage({ topic: 'downloads.progress', payload: [frame] }))

    expect(queryClient.getQueryData(['downloads'])).toBe('last-good')
  })
})

describe('RealtimeProvider library signal', () => {
  it('RealtimeProvider_marks_stale_the_queries_an_import_changes_under_the_keys_they_really_use', () => {
    // Arrange — keys as the pages build them. The map used to name ['seasons'] and ['episodes'],
    // which no query starts with, and left the series page, a played asset and the calendar alone.
    const queryClient = testQueryClient()
    const affected = [
      ['works', null],
      ['work', 'work-1'],
      ['assets', 'work', 'work-1'],
      ['asset', 'asset-1'],
      ['target', 'work', 'work-1'],
      seriesKeys.episodes('work-2', 1),
      seriesKeys.assets('work-2'),
      ['monitoring', 'calendar'],
      ['monitoring', 'missing', 50],
      ['imports'],
      ['subtitles', 'asset', 'asset-1'],
    ] as const
    for (const key of affected) queryClient.setQueryData(key, 'cached')
    queryClient.setQueryData(['users'], 'cached')
    renderWithProviders(<RealtimeProvider>{null}</RealtimeProvider>, queryClient)
    const stream = vi.mocked(openRealtimeStream).mock.lastCall?.[0]

    // Act
    act(() => stream?.onMessage({ topic: 'library', payload: null }))

    // Assert
    for (const key of affected) {
      expect(queryClient.getQueryState(key)?.isInvalidated, JSON.stringify(key)).toBe(true)
    }
    expect(queryClient.getQueryState(['users'])?.isInvalidated).toBe(false)
  })
})
