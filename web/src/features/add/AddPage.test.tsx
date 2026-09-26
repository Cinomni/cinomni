import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, metadataApi, monitoringApi, requestsApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import type { MetadataCandidate, Work } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { aCurrentUser, aWork, anAuthContext } from '@/test/factories'
import { AddMoviePage } from './AddMoviePage'
import { AddSeriesPage } from './AddSeriesPage'

vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))
vi.mock('@/api/endpoints', () => ({
  catalogApi: { add: vi.fn(), addSeries: vi.fn(), getByExternalId: vi.fn() },
  metadataApi: { search: vi.fn(), refresh: vi.fn() },
  monitoringApi: { applyPolicy: vi.fn() },
  requestsApi: { submit: vi.fn() },
}))

function signedInAsAdmin() {
  const value = anAuthContext(aCurrentUser({
      id: 'admin-1',
      username: 'admin',
      isAdministrator: true,
      role: 'Administrator',
      permissions: { canRequest: true, requestsAutoApproved: true, openRequestLimit: null },
    }))
  vi.mocked(useAuth).mockReturnValue(value)
}

function aCandidate(overrides: Partial<MetadataCandidate> = {}): MetadataCandidate {
  return {
    provider: 'tmdb',
    kind: 'Movie',
    externalId: '603',
    title: 'The Matrix',
    year: 1999,
    overview: 'A hacker learns the truth.',
    tvdbId: null,
    imdbId: null,
    tmdbId: null,
    ...overrides,
  }
}

async function search(user: ReturnType<typeof userEvent.setup>, term: string) {
  await user.type(screen.getByLabelText('Search by title'), term)
  await user.click(screen.getByRole('button', { name: 'Search' }))
}

beforeEach(() => {
  signedInAsAdmin()
  vi.mocked(monitoringApi.applyPolicy).mockResolvedValue({ targetId: 'target-1' })
  vi.mocked(metadataApi.refresh).mockResolvedValue(undefined)
})

function signedInAsMember() {
  vi.mocked(useAuth).mockReturnValue(
    anAuthContext(aCurrentUser({ id: 'member-1', username: 'nadia' })),
  )
}

describe('AddMoviePage request quota', () => {
  it('AddMoviePage_shows_the_quota_refusal_with_the_numbers_the_server_reported', async () => {
    // Arrange — the server counts the open requests and says so in the message; the client neither
    // keeps that count nor re-derives it, so what reaches the row has to be the server's own words.
    const user = userEvent.setup()
    signedInAsMember()
    vi.mocked(metadataApi.search).mockResolvedValue([aCandidate()])
    vi.mocked(catalogApi.getByExternalId).mockResolvedValue(null)
    vi.mocked(requestsApi.submit).mockRejectedValue(
      new ApiError(
        409,
        'requests.quota_exceeded',
        'You already have 3 of 3 requests open. One has to be decided or arrive before you can ask for another.',
      ),
    )

    // Act
    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')
    await user.click(await screen.findByRole('button', { name: 'Request' }))

    // Assert — the numbers survive to the screen, rather than a generic "could not submit".
    expect(await screen.findByText(/You already have 3 of 3 requests open/)).toBeInTheDocument()
  })
})

describe('Add pages request kind', () => {
  it('AddMoviePage_requests_a_film_as_a_movie', async () => {
    const user = userEvent.setup()
    signedInAsMember()
    vi.mocked(metadataApi.search).mockResolvedValue([aCandidate()])
    vi.mocked(catalogApi.getByExternalId).mockResolvedValue(null)
    vi.mocked(requestsApi.submit).mockResolvedValue({ requestId: 'req-1' })

    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')
    await user.click(await screen.findByRole('button', { name: 'Request' }))

    expect(requestsApi.submit).toHaveBeenCalledWith(expect.objectContaining({ externalId: '603', kind: 'Movie' }))
  })

  it('AddSeriesPage_requests_a_show_as_a_series', async () => {
    // Arrange — without the kind the server catalogued every request as a movie and fetched the show's
    // id from the film endpoint, where TMDB numbers an unrelated title the same.
    const user = userEvent.setup()
    signedInAsMember()
    vi.mocked(metadataApi.search).mockResolvedValue([
      aCandidate({ kind: 'Series', externalId: '1399', title: 'Game of Thrones', year: 2011 }),
    ])
    vi.mocked(catalogApi.getByExternalId).mockResolvedValue(null)
    vi.mocked(requestsApi.submit).mockResolvedValue({ requestId: 'req-2' })

    renderWithProviders(<AddSeriesPage />)
    await search(user, 'Thrones')
    await user.click(await screen.findByRole('button', { name: 'Request' }))

    expect(requestsApi.submit).toHaveBeenCalledWith(expect.objectContaining({ externalId: '1399', kind: 'Series' }))
  })
})

describe('AddMoviePage duplicate detection', () => {
  it('AddMoviePage_presents_a_candidate_already_in_the_catalog_as_present_rather_than_addable', async () => {
    // Arrange
    const user = userEvent.setup()
    const candidate = aCandidate()
    const existing: Work = aWork({ id: 'work-42', kind: 'Movie', title: 'The Matrix' })
    vi.mocked(metadataApi.search).mockResolvedValue([candidate])
    vi.mocked(catalogApi.getByExternalId).mockResolvedValue(existing)

    // Act
    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')

    // Assert — the row reads as already in the library, links to it, and offers no Add control.
    expect(await screen.findByText('In library')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'View' })).toHaveAttribute('href', '/works/work-42')
    expect(screen.queryByRole('button', { name: 'Add' })).not.toBeInTheDocument()
    expect(catalogApi.getByExternalId).toHaveBeenCalledWith('Tmdb', '603', expect.anything(), 'Movie')
  })

  it('AddMoviePage_makes_the_library_and_the_already_here_answer_stale_after_adding', async () => {
    // Arrange — the lookup is cached for minutes; without an invalidation the row would go on
    // offering Add for the title just added, and the library list would not show it.
    const user = userEvent.setup()
    vi.mocked(metadataApi.search).mockResolvedValue([aCandidate()])
    vi.mocked(catalogApi.getByExternalId).mockResolvedValue(null)
    vi.mocked(catalogApi.add).mockResolvedValue({ workId: 'work-7' })
    const { queryClient } = renderWithProviders(<AddMoviePage />)
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    await search(user, 'Matrix')

    // Act
    await user.click(await screen.findByRole('button', { name: 'Add' }))

    // Assert
    await vi.waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['catalog', 'work-by-external'] }),
    )
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['works'] })
  })

  it('AddMoviePage_still_offers_to_add_when_the_duplicate_lookup_fails', async () => {
    // Arrange — a failed lookup proves nothing about whether the title exists, so it must not block
    // adding and must not be reported as either present or confirmed new.
    const user = userEvent.setup()
    const candidate = aCandidate()
    vi.mocked(metadataApi.search).mockResolvedValue([candidate])
    vi.mocked(catalogApi.getByExternalId).mockRejectedValue(new Error('network down'))
    vi.mocked(catalogApi.add).mockResolvedValue({ workId: 'work-99' })

    // Act
    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')
    const addButton = await screen.findByRole('button', { name: 'Add' })
    await user.click(addButton)

    // Assert
    expect(screen.queryByText('In library')).not.toBeInTheDocument()
    expect(catalogApi.add).toHaveBeenCalledWith({
      title: 'The Matrix',
      year: 1999,
      externalIds: [{ provider: 'Tmdb', value: '603' }],
    })
  })

  it('AddMoviePage_offers_the_normal_add_control_before_the_duplicate_lookup_settles', async () => {
    // Arrange — the lookup must not block the row while it is still in flight.
    const user = userEvent.setup()
    const candidate = aCandidate()
    vi.mocked(metadataApi.search).mockResolvedValue([candidate])
    vi.mocked(catalogApi.getByExternalId).mockReturnValue(new Promise(() => {}))

    // Act
    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')

    // Assert
    expect(await screen.findByRole('button', { name: 'Add' })).toBeInTheDocument()
    expect(screen.queryByText('In library')).not.toBeInTheDocument()
  })
})

describe('Add pages search failure', () => {
  const noProvider = new ApiError(
    503,
    'metadata.no_provider',
    'Searching for movies needs TMDB, and no API key is configured for it. An administrator can add one under Settings.',
  )

  it('AddMoviePage_says_which_provider_is_missing_and_sends_an_administrator_to_where_its_key_goes', async () => {
    // Arrange — the server names the provider; the page must pass that on rather than guess.
    const user = userEvent.setup()
    vi.mocked(metadataApi.search).mockRejectedValue(noProvider)

    // Act
    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')

    // Assert
    expect(await screen.findByText('No metadata provider configured')).toBeInTheDocument()
    expect(screen.getByText(/needs TMDB, and no API key is configured/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Settings > Metadata' })).toHaveAttribute(
      'href',
      '/console/settings#settings-metadata',
    )
    expect(screen.queryByText('Search failed')).not.toBeInTheDocument()
  })

  it('AddSeriesPage_keeps_the_configuration_hint_from_a_member_who_cannot_act_on_it', async () => {
    // Arrange
    const user = userEvent.setup()
    signedInAsMember()
    vi.mocked(metadataApi.search).mockRejectedValue(noProvider)

    // Act
    renderWithProviders(<AddSeriesPage />)
    await search(user, 'Frontier')

    // Assert — the fact reaches everyone; server configuration is an administrator's business.
    expect(await screen.findByText('No metadata provider configured')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Settings > Metadata' })).not.toBeInTheDocument()
  })

  it('AddMoviePage_reports_an_unreachable_provider_as_a_failed_search_not_a_missing_one', async () => {
    // Arrange — a network failure is not a configuration problem and must not be described as one.
    const user = userEvent.setup()
    vi.mocked(metadataApi.search).mockRejectedValue(new TypeError('Failed to fetch'))

    // Act
    renderWithProviders(<AddMoviePage />)
    await search(user, 'Matrix')

    // Assert
    expect(await screen.findByText('Search failed')).toBeInTheDocument()
    expect(screen.queryByText(/configured/)).not.toBeInTheDocument()
  })
})
