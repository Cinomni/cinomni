import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, libraryApi, monitoringApi, playbackApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { aCurrentUser, aTargetTree, aWork, anAuthContext } from '@/test/factories'
import { SeriesDetailPage } from './SeriesDetailPage'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))
vi.mock('@/api/endpoints', () => ({
  catalogApi: { get: vi.fn(), seasons: vi.fn(), episodes: vi.fn() },
  metadataApi: { refresh: vi.fn() },
  monitoringApi: {
    targetsForWork: vi.fn(),
    setMonitored: vi.fn(),
    setSubtreeMonitored: vi.fn(),
    searchSeason: vi.fn(),
    applyPolicy: vi.fn(),
  },
  libraryApi: { assets: vi.fn() },
  playbackApi: { nextUp: vi.fn(), progressFor: vi.fn() },
}))

function signedInAsAdministrator() {
  const value = anAuthContext(aCurrentUser({
      id: 'u-0',
      username: 'operator',
      isAdministrator: true,
      role: 'Administrator',
      permissions: { canRequest: true, requestsAutoApproved: true, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
}

function signedInAsMember() {
  const value = anAuthContext(aCurrentUser({
      id: 'u-1',
      username: 'someone',
      isAdministrator: false,
      role: 'Member',
      permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
}

beforeEach(() => {
  signedInAsMember()
  vi.mocked(catalogApi.get).mockResolvedValue(aWork({ id: 'series-1', kind: 'Series', title: 'A Show' }))
  vi.mocked(monitoringApi.targetsForWork).mockResolvedValue(aTargetTree())
  vi.mocked(libraryApi.assets).mockResolvedValue([])
  vi.mocked(playbackApi.nextUp).mockResolvedValue(undefined)
})

describe('SeriesDetailPage', () => {
  it('SeriesDetailPage_reports_a_failed_seasons_fetch_as_an_error_not_as_no_seasons', async () => {
    // Arrange — the seasons request fails; an API outage must not read as "this show has no seasons".
    vi.mocked(catalogApi.seasons).mockRejectedValue(
      new ApiError(500, 'seasons.failed', 'The seasons could not be read.'),
    )

    // Act
    renderWithProviders(<SeriesDetailPage />)

    // Assert — a distinct, retryable error branch, not the empty-state copy.
    expect(await screen.findByText('Seasons could not be loaded')).toBeInTheDocument()
    expect(screen.getByText('The seasons could not be read.')).toBeInTheDocument()
    expect(screen.queryByText('No seasons yet')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('SeriesDetailPage_shows_the_empty_state_when_the_seasons_request_actually_succeeds_with_none', async () => {
    // Arrange — a genuinely empty tree, to contrast with the error branch above.
    vi.mocked(catalogApi.seasons).mockResolvedValue([])

    // Act
    renderWithProviders(<SeriesDetailPage />)

    // Assert
    expect(await screen.findByText('No seasons yet')).toBeInTheDocument()
    expect(screen.queryByText('Seasons could not be loaded')).not.toBeInTheDocument()
  })

  it('SeriesDetailPage_explains_a_refused_monitoring_policy_the_picker_deliberately_swallowed', async () => {
    // Arrange — the picker catches the rejection so its modal stays on the operator's choice, which
    // means this page is the only surface left that can say why nothing happened.
    const user = userEvent.setup()
    signedInAsAdministrator()
    vi.mocked(catalogApi.seasons).mockResolvedValue([])
    vi.mocked(monitoringApi.applyPolicy).mockRejectedValue(
      new ApiError(409, 'monitoring.policy_in_flight', 'A policy is already being applied to this show.'),
    )
    renderWithProviders(<SeriesDetailPage />)

    // Act — pick a policy other than the one in force, then confirm it.
    await user.selectOptions(await screen.findByLabelText('Monitoring'), 'Pilot')
    await user.click(screen.getByRole('button', { name: 'Apply' }))
    await user.click(await screen.findByRole('button', { name: 'Apply monitoring change' }))

    // Assert
    expect(await screen.findByText('A policy is already being applied to this show.')).toBeInTheDocument()
  })
})
