import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi } from '@/api/endpoints'
import type { Work, WorkFacets, WorkPage, WorkPageQuery } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { aCurrentUser, aWork, anAuthContext } from '@/test/factories'
import { renderWithProviders } from '@/test/render'
import { LibraryPage } from './LibraryPage'
import { LIBRARY_PAGE_SIZE } from './useLibraryPages'

vi.mock('@/api/endpoints', () => ({
  catalogApi: { page: vi.fn(), facets: vi.fn(), collections: vi.fn() },
}))
vi.mock('@/auth/useAuth', () => ({ useAuth: vi.fn() }))

function signedInAsAdmin() {
  const value = anAuthContext(
    aCurrentUser({
      id: 'admin-1',
      username: 'root',
      isAdministrator: true,
      role: 'Administrator',
      permissions: { canRequest: true, requestsAutoApproved: true, openRequestLimit: null },
    }),
  )
  vi.mocked(useAuth).mockReturnValue(value)
}

function aPage(items: Work[], total = items.length, offset = 0): WorkPage {
  return { items, total, offset, limit: LIBRARY_PAGE_SIZE }
}

function someFacets(overrides: Partial<WorkFacets> = {}): WorkFacets {
  return { movies: 2, series: 1, genres: [], ...overrides }
}

/** The filters of the most recent page request. */
function lastQuery(): WorkPageQuery {
  const calls = vi.mocked(catalogApi.page).mock.calls
  return calls[calls.length - 1][0]
}

/** Poster links only — excludes the "Add movie" / "Add series" header actions. */
function posterLinks(): HTMLElement[] {
  return screen.getAllByRole('link').filter((link) => {
    const href = link.getAttribute('href') ?? ''
    return href.startsWith('/works/') || href.startsWith('/series/')
  })
}

beforeEach(() => {
  signedInAsAdmin()
  vi.mocked(catalogApi.collections).mockResolvedValue([])
  vi.mocked(catalogApi.facets).mockResolvedValue(someFacets())
  vi.mocked(catalogApi.page).mockResolvedValue(aPage([]))
})

describe('LibraryPage', () => {
  it('LibraryPage_asks_the_server_for_the_first_page_in_title_order_and_renders_it_as_answered', async () => {
    // Arrange — the server's order is the order shown, whatever the titles.
    vi.mocked(catalogApi.page).mockResolvedValue(
      aPage([aWork({ id: 'w2', title: 'Zeta' }), aWork({ id: 'w1', title: 'Alpha' })]),
    )

    // Act
    renderWithProviders(<LibraryPage />)
    await waitFor(() => expect(posterLinks()).toHaveLength(2))

    // Assert
    expect(catalogApi.page).toHaveBeenCalledWith({ sort: 'Title' }, 0, LIBRARY_PAGE_SIZE, expect.anything())
    expect(posterLinks().map((link) => link.textContent)).toEqual([
      expect.stringContaining('Zeta'),
      expect.stringContaining('Alpha'),
    ])
    expect(screen.getByText('3 titles')).toBeInTheDocument()
  })

  it('LibraryPage_kind_filter_asks_for_that_kind_and_counts_from_the_facets', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(catalogApi.page).mockImplementation((query) =>
      Promise.resolve(
        query.kind === 'Movie'
          ? aPage([aWork({ id: 'w1', title: 'Alpha Movie' })])
          : aPage([aWork({ id: 'w1', title: 'Alpha Movie' }), aWork({ id: 'w2', kind: 'Series', title: 'Beta Series' })]),
      ),
    )

    // Act
    renderWithProviders(<LibraryPage />)
    await screen.findByText('Beta Series')
    await user.click(screen.getByRole('button', { name: /^Movies/ }))

    // Assert
    await waitFor(() => expect(screen.queryByText('Beta Series')).not.toBeInTheDocument())
    expect(lastQuery()).toMatchObject({ kind: 'Movie' })
    expect(screen.getByRole('button', { name: /^Movies/ })).toHaveTextContent('2')
  })

  it('LibraryPage_sends_the_chosen_sort_to_the_server', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.page).mockResolvedValue(aPage([aWork({ id: 'w1', title: 'Alpha' })]))
    renderWithProviders(<LibraryPage />)
    await screen.findByText('Alpha')

    await user.selectOptions(screen.getByLabelText('Sort by'), 'year')

    await waitFor(() => expect(lastQuery()).toMatchObject({ sort: 'Year' }))
  })

  it('LibraryPage_searches_titles_on_the_server_once_typing_pauses', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.page).mockResolvedValue(aPage([aWork({ id: 'w1', title: 'Alpha' })]))
    renderWithProviders(<LibraryPage />)
    await screen.findByText('Alpha')

    await user.type(screen.getByLabelText('Filter by title'), '  alien ')

    // Trimmed, and asked for once rather than per keystroke.
    await waitFor(() => expect(lastQuery()).toMatchObject({ q: 'alien' }))
    const searches = vi.mocked(catalogApi.page).mock.calls.filter(([query]) => query.q)
    expect(searches).toHaveLength(1)
  })

  it('LibraryPage_loads_the_next_page_on_request_and_keeps_the_first', async () => {
    // Arrange — two titles on the server, one per page.
    const user = userEvent.setup()
    vi.mocked(catalogApi.page).mockImplementation((_query, offset) =>
      Promise.resolve(
        offset === 0
          ? aPage([aWork({ id: 'w1', title: 'Alpha' })], 2, 0)
          : aPage([aWork({ id: 'w2', title: 'Beta' })], 2, 1),
      ),
    )
    renderWithProviders(<LibraryPage />)
    await screen.findByText('Alpha')
    expect(screen.getByText('1 of 2 titles')).toBeInTheDocument()

    // Act
    await user.click(screen.getByRole('button', { name: 'Show more titles' }))

    // Assert — the next offset is where the first page ended; nothing is left to ask for.
    expect(await screen.findByText('Beta')).toBeInTheDocument()
    expect(screen.getByText('Alpha')).toBeInTheDocument()
    expect(catalogApi.page).toHaveBeenLastCalledWith({ sort: 'Title' }, 1, LIBRARY_PAGE_SIZE, expect.anything())
    expect(screen.queryByRole('button', { name: 'Show more titles' })).not.toBeInTheDocument()
  })

  it('LibraryPage_tells_an_empty_shelf_from_a_search_that_found_nothing', async () => {
    vi.mocked(catalogApi.facets).mockResolvedValue(someFacets({ movies: 0, series: 0 }))
    const { unmount } = renderWithProviders(<LibraryPage />)
    expect(await screen.findByText('No titles yet')).toBeInTheDocument()
    unmount()

    vi.mocked(catalogApi.facets).mockResolvedValue(someFacets())
    const user = userEvent.setup()
    renderWithProviders(<LibraryPage />)
    await user.type(await screen.findByLabelText('Filter by title'), 'zzz')

    expect(await screen.findByText('No matches')).toBeInTheDocument()
  })
})

describe('LibraryPage reached as Movies or Series', () => {
  it('LibraryPage_locked_to_a_kind_asks_only_for_that_kind_and_shows_no_kind_filter', async () => {
    vi.mocked(catalogApi.page).mockResolvedValue(aPage([aWork({ id: 'w1', title: 'Alpha Movie' })]))

    renderWithProviders(<LibraryPage kind="Movie" />)

    expect(await screen.findByText('Alpha Movie')).toBeInTheDocument()
    expect(lastQuery()).toMatchObject({ kind: 'Movie' })
    expect(catalogApi.facets).toHaveBeenCalledWith(undefined, 'Movie', expect.anything())
    expect(screen.getByRole('heading', { name: 'Movies', level: 1 })).toBeInTheDocument()
    expect(await screen.findByText('2 movies')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Filter by kind' })).not.toBeInTheDocument()
  })

  it('LibraryPage_keeps_filters_folded_until_asked_and_filters_by_availability', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.page).mockResolvedValue(aPage([aWork({ id: 'w1', title: 'On Disk' })]))
    renderWithProviders(<LibraryPage kind="Movie" />)
    await screen.findByText('On Disk')

    const toggle = screen.getByRole('button', { name: 'Filters' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await user.click(toggle)
    await user.click(screen.getByRole('button', { name: 'Not in library' }))

    await waitFor(() => expect(lastQuery()).toMatchObject({ kind: 'Movie', availability: 'None' }))
  })

  it('LibraryPage_offers_the_genres_the_server_counted_and_asks_for_the_chosen_one', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(catalogApi.facets).mockResolvedValue(
      someFacets({
        genres: [
          { genre: 'Science Fiction', count: 2 },
          { genre: 'Crime', count: 1 },
        ],
      }),
    )
    vi.mocked(catalogApi.page).mockResolvedValue(aPage([aWork({ id: 'w1', title: 'Alien' })]))
    renderWithProviders(<LibraryPage kind="Movie" />)
    await screen.findByText('Alien')
    await user.click(screen.getByRole('button', { name: 'Filters' }))

    // Act
    const select = await screen.findByRole('combobox', { name: 'Filter by genre' })
    await user.selectOptions(select, 'Science Fiction')

    // Assert
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual([
      'Any genre',
      'Science Fiction (2)',
      'Crime (1)',
    ])
    await waitFor(() => expect(lastQuery()).toMatchObject({ genre: 'Science Fiction' }))
    expect(screen.getByRole('button', { name: /Filters/ })).toHaveTextContent('1')
  })
})
