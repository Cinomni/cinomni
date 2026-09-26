import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { monitoringApi } from '@/api/endpoints'
import type { MonitoredTargetTree } from '@/api/types'
import { renderWithProviders, testQueryClient } from '@/test/render'
import { aTarget, aTargetTree, anEpisode } from '@/test/factories'
import { EpisodeRow } from './EpisodeRow'
import { seriesKeys } from './useSeries'

vi.mock('@/api/endpoints', () => ({
  monitoringApi: { setMonitored: vi.fn(), targetsForWork: vi.fn() },
}))

const episode = anEpisode({ id: 'e-4', seasonNumber: 3, number: 4, title: 'Hamsterdam' })
const target = aTarget({ id: 'target-e4', targetRef: 'e-4', seasonNumber: 3, episodeNumber: 4, monitored: false })

beforeEach(() => {
  vi.mocked(monitoringApi.setMonitored).mockResolvedValue(undefined)
})

describe('EpisodeRow', () => {
  it('EpisodeRow_toggle_calls_set_monitored_and_invalidates_the_season_key', async () => {
    // Arrange — the row's own season subtree is what a toggle may invalidate; the other seasons of a
    // twenty-season show must stay in cache.
    const user = userEvent.setup()
    const queryClient = testQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    renderWithProviders(
      <EpisodeRow workId="work-1" episode={episode} target={target} canManage />,
      queryClient,
    )

    // Act
    await user.click(screen.getByRole('switch', { name: 'Monitor S03E04' }))

    // Assert
    await waitFor(() => expect(monitoringApi.setMonitored).toHaveBeenCalledWith('target-e4', true))
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['series', 'work-1', 'season', 3] }),
    )
    expect(invalidate).toHaveBeenCalledWith({ queryKey: seriesKeys.season('work-1', 3) })
  })

  it('applies the new state to the cached tree before the server answers', async () => {
    const user = userEvent.setup()
    const queryClient = testQueryClient()
    queryClient.setQueryData(
      seriesKeys.targets('work-1'),
      aTargetTree({
        seasons: [{ target: aTarget({ id: 'season-3', kind: 'Season', seasonNumber: 3 }), episodes: [target] }],
      }),
    )
    let resolveServer: () => void = () => undefined
    vi.mocked(monitoringApi.setMonitored).mockReturnValue(
      new Promise<void>((resolve) => {
        resolveServer = resolve
      }),
    )

    renderWithProviders(<EpisodeRow workId="work-1" episode={episode} target={target} canManage />, queryClient)
    await user.click(screen.getByRole('switch', { name: 'Monitor S03E04' }))

    // The cache carries the optimistic value while the request is still in flight.
    await waitFor(() => {
      const tree = queryClient.getQueryData<MonitoredTargetTree>(seriesKeys.targets('work-1'))
      expect(tree?.seasons[0].episodes[0].monitored).toBe(true)
    })
    resolveServer()
  })

  it('rolls the cached tree back when the server refuses', async () => {
    const user = userEvent.setup()
    const queryClient = testQueryClient()
    queryClient.setQueryData(
      seriesKeys.targets('work-1'),
      aTargetTree({
        seasons: [{ target: aTarget({ id: 'season-3', kind: 'Season', seasonNumber: 3 }), episodes: [target] }],
      }),
    )
    vi.mocked(monitoringApi.setMonitored).mockRejectedValue(new Error('forbidden'))

    renderWithProviders(<EpisodeRow workId="work-1" episode={episode} target={target} canManage />, queryClient)
    await user.click(screen.getByRole('switch', { name: 'Monitor S03E04' }))

    await waitFor(() => {
      const tree = queryClient.getQueryData<MonitoredTargetTree>(seriesKeys.targets('work-1'))
      expect(tree?.seasons[0].episodes[0].monitored).toBe(false)
    })
  })

  it('offers no toggle to a regular account and reports the episode state', () => {
    renderWithProviders(<EpisodeRow workId="work-1" episode={episode} target={target} canManage={false} />)

    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
    expect(screen.getByText('S03E04')).toBeInTheDocument()
    expect(screen.getByText('Missing')).toBeInTheDocument()
  })

  it('marks an episode that has not aired yet as unaired rather than missing', () => {
    const future = new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString().slice(0, 10)
    renderWithProviders(
      <EpisodeRow
        workId="work-1"
        episode={anEpisode({ airDate: future })}
        target={null}
        canManage={false}
      />,
    )

    expect(screen.getByText('Unaired')).toBeInTheDocument()
  })
})
