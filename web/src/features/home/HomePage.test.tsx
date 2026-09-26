import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { catalogApi, libraryApi, monitoringApi, playbackApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import { ApiError } from '@/lib/api'
import { secondsToTicks } from '@/lib/format'
import { aCurrentUser, aWork, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { HomePage } from './HomePage'

vi.mock('@/api/endpoints', () => ({
  catalogApi: { list: vi.fn(), get: vi.fn(), importList: vi.fn() },
  libraryApi: { assets: vi.fn() },
  monitoringApi: { calendar: vi.fn() },
  playbackApi: { inProgress: vi.fn(), nextUp: vi.fn() },
}))
vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))

function signedInAsMember(canRequest = true) {
  vi.mocked(useAuth).mockReturnValue(
    anAuthContext(
      aCurrentUser({
        isAdministrator: false,
        role: 'Member',
        permissions: { canRequest, requestsAutoApproved: false, openRequestLimit: null },
      }),
    ),
  )
}

beforeEach(() => {
  signedInAsMember()
  vi.mocked(libraryApi.assets).mockResolvedValue([])
  vi.mocked(monitoringApi.calendar).mockResolvedValue([])
  vi.mocked(playbackApi.inProgress).mockResolvedValue([])
  vi.mocked(catalogApi.get).mockImplementation((id) => Promise.resolve(aWork({ id })))
})

describe('HomePage', () => {
  it('HomePage_leads_with_resume_when_the_viewer_stopped_part_way_through_the_featured_movie', async () => {
    // Arrange
    vi.mocked(catalogApi.list).mockResolvedValue([
      aWork({ id: 'w1', title: 'Arrival', hasAsset: true, backdropUrl: '/backdrop.jpg' }),
    ])
    vi.mocked(libraryApi.assets).mockResolvedValue([
      { id: 'a1', workId: 'w1', state: 'Active', primaryVersionId: null, createdAt: '2026-09-01T00:00:00Z', unitIds: ['w1'] },
    ])
    vi.mocked(playbackApi.inProgress).mockResolvedValue([
      {
        assetId: 'a1',
        positionTicks: secondsToTicks(42 * 60 + 10),
        played: false,
        playCount: 0,
        unitId: 'w1',
        durationTicks: secondsToTicks(84 * 60 + 20),
        workId: 'w1',
        updatedAt: '2026-09-20T21:00:00Z',
      },
    ])

    // Act
    renderWithProviders(<HomePage />)

    // Assert — the hero resumes, and the same title opens the continue-watching row.
    expect(await screen.findByRole('link', { name: 'Resume · 42:10' })).toHaveAttribute('href', '/watch/a1')
    expect(screen.getByRole('link', { name: 'Resume Arrival from 42:10' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Arrival', level: 2 })).toBeInTheDocument()
    // With the runtime known, the tile says how far through the viewer is.
    expect(screen.getByRole('progressbar', { name: 'Arrival progress' })).toHaveAttribute('aria-valuenow', '50')
  })

  it('HomePage_gives_a_member_the_synopsis_from_the_detail_route', async () => {
    // Arrange — the list route carries no synopsis; the detail route does, for any viewer.
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Arrival', overview: null })])
    vi.mocked(catalogApi.get).mockResolvedValue(
      aWork({ id: 'w1', title: 'Arrival', overview: 'A linguist is recruited to talk to visitors.' }),
    )

    // Act
    renderWithProviders(<HomePage />)

    // Assert
    expect(await screen.findByText('A linguist is recruited to talk to visitors.')).toBeInTheDocument()
    expect(catalogApi.get).toHaveBeenCalledWith('w1')
  })

  it('HomePage_offers_details_rather_than_play_for_a_title_not_on_disk', async () => {
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Dune', hasAsset: false })])

    renderWithProviders(<HomePage />)

    expect(await screen.findByRole('link', { name: 'View details' })).toHaveAttribute('href', '/works/w1')
    expect(screen.queryByRole('link', { name: /^Play/ })).not.toBeInTheDocument()
  })

  it('HomePage_keeps_the_page_when_only_the_progress_row_fails', async () => {
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Arrival', hasAsset: true })])
    vi.mocked(libraryApi.assets).mockResolvedValue([
      { id: 'a1', workId: 'w1', state: 'Active', primaryVersionId: null, createdAt: '2026-09-01T00:00:00Z', unitIds: ['w1'] },
    ])
    vi.mocked(playbackApi.inProgress).mockRejectedValue(new ApiError(503, 'playback.unavailable', 'Playback is restarting.'))

    renderWithProviders(<HomePage />)

    // The row says why it is missing; the hero and the rest of the page stay.
    expect(await screen.findByText('Playback is restarting.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Continue watching' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Arrival', level: 2 })).toBeInTheDocument()
  })

  it('HomePage_welcomes_an_empty_library_with_the_step_this_account_can_take', async () => {
    vi.mocked(catalogApi.list).mockResolvedValue([])

    renderWithProviders(<HomePage />)

    expect(await screen.findByRole('heading', { name: 'Your library is empty' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Request a movie' })).toHaveAttribute('href', '/add')
  })

  it('HomePage_never_asks_a_member_for_the_administrator_only_import_list', async () => {
    vi.mocked(catalogApi.list).mockResolvedValue([aWork({ id: 'w1', title: 'Arrival' })])

    renderWithProviders(<HomePage />)
    await screen.findByRole('heading', { name: 'Arrival', level: 2 })

    expect(catalogApi.importList).not.toHaveBeenCalled()
  })
})
