import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { catalogApi, libraryApi, monitoringApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { aCurrentUser, aWork, anAuthContext } from '@/test/factories'
import { WorkDetailPage } from './WorkDetailPage'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))
vi.mock('@/api/endpoints', () => ({
  catalogApi: { get: vi.fn() },
  libraryApi: { assets: vi.fn(), asset: vi.fn() },
  metadataApi: { refresh: vi.fn(), snapshot: vi.fn(), selectArtwork: vi.fn() },
  monitoringApi: { targetForWork: vi.fn(), setMonitored: vi.fn(), applyPolicy: vi.fn() },
  decisionApi: { search: vi.fn(), grab: vi.fn() },
  subtitlesApi: { forAsset: vi.fn(), search: vi.fn(), download: vi.fn() },
}))

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

const NOT_FOUND_COPY = 'This title could not be found.'

beforeEach(() => {
  signedInAsMember()
  vi.mocked(libraryApi.assets).mockResolvedValue([])
  // The API answers 404 for a work with no target; the page turns that into "not monitored".
  vi.mocked(monitoringApi.targetForWork).mockRejectedValue(
    new ApiError(404, 'monitoring.no_target', 'No target.'),
  )
})

describe('WorkDetailPage', () => {
  it('WorkDetailPage_reports_an_unreachable_api_as_a_failure_not_as_a_missing_title', async () => {
    // Arrange — the catalog is unreachable. Telling the household the film does not exist would be
    // a different, and wrong, statement about their own library.
    vi.mocked(catalogApi.get).mockRejectedValue(
      new ApiError(503, 'catalog.unavailable', 'The catalog is temporarily unavailable.'),
    )

    // Act
    renderWithProviders(<WorkDetailPage />)

    // Assert
    expect(await screen.findByText('This title could not be loaded')).toBeInTheDocument()
    expect(screen.getByText('The catalog is temporarily unavailable.')).toBeInTheDocument()
    expect(screen.queryByText(NOT_FOUND_COPY)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('WorkDetailPage_still_reports_a_404_as_a_missing_title', async () => {
    // Arrange — a title hidden by a collection this account was not granted answers exactly like one
    // that does not exist, and it must keep doing so: an error state here would confirm it exists.
    vi.mocked(catalogApi.get).mockRejectedValue(new ApiError(404, 'catalog.not_found', 'Not found.'))

    // Act
    renderWithProviders(<WorkDetailPage />)

    // Assert
    expect(await screen.findByText(NOT_FOUND_COPY)).toBeInTheDocument()
    expect(screen.queryByText('This title could not be loaded')).not.toBeInTheDocument()
  })

  it('WorkDetailPage_renders_the_movie_once_the_catalog_answers', async () => {
    // Arrange — the success path, so the two failure branches above are not the only ones pinned.
    vi.mocked(catalogApi.get).mockResolvedValue(aWork({ id: 'work-1', kind: 'Movie', title: 'Arrival' }))

    // Act
    renderWithProviders(<WorkDetailPage />)

    // Assert
    expect(await screen.findByText('Arrival')).toBeInTheDocument()
    expect(screen.queryByText(NOT_FOUND_COPY)).not.toBeInTheDocument()
  })
})
