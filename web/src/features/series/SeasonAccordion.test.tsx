import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, monitoringApi, playbackApi } from '@/api/endpoints'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { aSeason, aTarget, anEpisode } from '@/test/factories'
import { SeasonAccordion } from './SeasonAccordion'
import type { SeasonWithTargets } from './useSeries'

vi.mock('@/api/endpoints', () => ({
  catalogApi: { episodes: vi.fn() },
  monitoringApi: { setMonitored: vi.fn(), setSubtreeMonitored: vi.fn(), searchSeason: vi.fn() },
  playbackApi: { progressFor: vi.fn() },
  libraryApi: { assets: vi.fn() },
}))

const season: SeasonWithTargets = {
  season: aSeason({ number: 3 }),
  target: aTarget({ id: 'season-target', kind: 'Season', seasonNumber: 3, missingCount: 1, totalCount: 2 }),
  targetByEpisodeId: new Map(),
}

beforeEach(() => {
  vi.mocked(catalogApi.episodes).mockResolvedValue([
    anEpisode({ id: 'e1', seasonNumber: 3, number: 1, title: 'The Target' }),
    anEpisode({ id: 'e2', seasonNumber: 3, number: 2, title: 'The Detail' }),
  ])
  vi.mocked(playbackApi.progressFor).mockResolvedValue([])
  vi.mocked(monitoringApi.searchSeason).mockResolvedValue(undefined)
})

describe('SeasonAccordion', () => {
  it('SeasonAccordion_expands_and_loads_episodes_lazily', async () => {
    // Arrange — a collapsed season. A show with twenty of these must not fetch them all up front.
    const user = userEvent.setup()
    renderWithProviders(<SeasonAccordion workId="work-1" season={season} canManage={false} />)

    // Assert — nothing is requested while it is closed.
    expect(catalogApi.episodes).not.toHaveBeenCalled()
    expect(screen.queryByText('The Target')).not.toBeInTheDocument()

    // Act
    await user.click(screen.getByRole('button', { name: /Season 3/ }))

    // Assert — the episodes arrive, and only this season's were asked for.
    expect(await screen.findByText('The Target')).toBeInTheDocument()
    expect(screen.getByText('The Detail')).toBeInTheDocument()
    await waitFor(() => expect(catalogApi.episodes).toHaveBeenCalledTimes(1))
    expect(catalogApi.episodes).toHaveBeenCalledWith('work-1', 3)
  })

  it('reports the season rollup in the header before it is opened', () => {
    renderWithProviders(<SeasonAccordion workId="work-1" season={season} canManage={false} />)

    expect(screen.getByText('1 of 2 in library')).toBeInTheDocument()
    expect(screen.getByText('1 missing')).toBeInTheDocument()
  })

  it('offers no monitoring or search control to a regular account', () => {
    renderWithProviders(<SeasonAccordion workId="work-1" season={season} canManage={false} />)

    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Search Season 3/ })).not.toBeInTheDocument()
  })

  it('SeasonAccordion_explains_a_refused_monitoring_toggle_instead_of_letting_the_switch_snap_back', async () => {
    // Arrange — the toggle is refused server-side. The switch reverts on the next read either way,
    // so without a reason on screen the operator only sees their click being undone.
    const user = userEvent.setup()
    vi.mocked(monitoringApi.setSubtreeMonitored).mockRejectedValue(
      new ApiError(409, 'monitoring.locked', 'This season is locked while a policy is being applied.'),
    )
    renderWithProviders(<SeasonAccordion workId="work-1" season={season} canManage />)

    // Act
    await user.click(screen.getByRole('switch', { name: 'Monitor Season 3 and its episodes' }))

    // Assert
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'This season is locked while a policy is being applied.',
    )
  })

  it('SeasonAccordion_gives_a_failed_episode_load_a_reason_and_a_retry', async () => {
    // Arrange — a failed episode read used to be the same grey sentence as "none have been synced",
    // and only one of the two is worth retrying.
    const user = userEvent.setup()
    vi.mocked(catalogApi.episodes).mockRejectedValue(
      new ApiError(500, 'catalog.episodes_failed', 'The episodes could not be read.'),
    )
    renderWithProviders(<SeasonAccordion workId="work-1" season={season} canManage={false} />)

    // Act
    await user.click(screen.getByRole('button', { name: /Season 3/ }))

    // Assert
    expect(await screen.findByText('The episodes could not be read.')).toBeInTheDocument()
    expect(screen.queryByText(/No episodes have been synced/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Retry/ })).toBeInTheDocument()
  })
})
