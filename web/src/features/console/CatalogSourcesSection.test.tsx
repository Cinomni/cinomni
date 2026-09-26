import { beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'
import type { QueryClient } from '@tanstack/react-query'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { discoveryApi } from '@/api/endpoints'
import type { CatalogSource } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { CatalogSourcesSection } from './CatalogSourcesSection'
import { catalogSourcesQueryKey, indexerCatalogQueryKey } from './indexerCatalogQueries'

vi.mock('@/api/endpoints', () => ({
  discoveryApi: {
    catalogSources: vi.fn(),
    addCatalogSource: vi.fn(),
    refreshCatalogSource: vi.fn(),
    updateCatalogSource: vi.fn(),
    removeCatalogSource: vi.fn(),
  },
}))

function aSource(overrides: Partial<CatalogSource> = {}): CatalogSource {
  return {
    id: 'source-1',
    name: 'Example Catalog',
    url: 'https://catalog.example/indexers.json',
    enabled: true,
    createdAt: '2026-09-01T00:00:00Z',
    lastRefreshedAt: '2026-09-20T08:30:00Z',
    lastRefreshSucceeded: true,
    lastRefreshCode: 'discovery.catalog_source.refreshed',
    lastRefreshMessage: 'The manifest was read.',
    entryCount: 3,
    ...overrides,
  }
}

/** Both the source list and the entries it publishes must be refetched after any source mutation. */
function expectCatalogInvalidated(spy: MockInstance<QueryClient['invalidateQueries']>): void {
  expect(spy).toHaveBeenCalledWith({ queryKey: catalogSourcesQueryKey })
  expect(spy).toHaveBeenCalledWith({ queryKey: indexerCatalogQueryKey })
}

async function openSourceAction(user: ReturnType<typeof userEvent.setup>, sourceName: string, action: string) {
  await user.click(await screen.findByRole('button', { name: `Source actions for ${sourceName}` }))
  await user.click(screen.getByRole('menuitem', { name: action }))
}

beforeEach(() => {
  vi.mocked(discoveryApi.catalogSources).mockResolvedValue([aSource()])
})

describe('CatalogSourcesSection', () => {
  it('CatalogSourcesSection_lists_each_source_with_its_refresh_state', async () => {
    vi.mocked(discoveryApi.catalogSources).mockResolvedValue([
      aSource(),
      aSource({
        id: 'source-2',
        name: 'Broken Catalog',
        url: 'https://broken.catalog.example/manifest.json',
        lastRefreshSucceeded: false,
        lastRefreshCode: 'discovery.catalog_source.invalid_manifest',
        lastRefreshMessage: 'The manifest is not valid JSON.',
        entryCount: 0,
      }),
      aSource({
        id: 'source-3',
        name: 'Paused Catalog',
        url: 'https://paused.catalog.example/manifest.json',
        enabled: false,
        lastRefreshedAt: null,
        lastRefreshSucceeded: null,
        lastRefreshCode: null,
        lastRefreshMessage: null,
        entryCount: 1,
      }),
    ])

    renderWithProviders(<CatalogSourcesSection />)

    const list = within(await screen.findByRole('list', { name: 'Catalog sources' }))
    expect(list.getAllByRole('listitem')).toHaveLength(3)
    expect(list.getByText('https://catalog.example/indexers.json')).toBeInTheDocument()
    expect(list.getByText('3 indexers')).toBeInTheDocument()
    expect(list.getByText('Refreshed')).toBeInTheDocument()
    // A failed refresh says so in words and carries the server's own explanation.
    expect(list.getByText('Refresh failed')).toBeInTheDocument()
    expect(list.getByText('The manifest is not valid JSON.')).toBeInTheDocument()
    expect(list.getByText('Not refreshed yet')).toBeInTheDocument()
    expect(list.getByText('1 indexer')).toBeInTheDocument()
    expect(list.getByRole('switch', { name: 'Use Paused Catalog' })).toHaveAttribute('aria-checked', 'false')
    expect(list.getByText('Not used')).toBeInTheDocument()
  })

  it('CatalogSourcesSection_empty_state_explains_what_a_catalog_source_is', async () => {
    vi.mocked(discoveryApi.catalogSources).mockResolvedValue([])
    const user = userEvent.setup()

    renderWithProviders(<CatalogSourcesSection />)

    expect(await screen.findByText('No catalog sources')).toBeInTheDocument()
    expect(screen.getByText(/publishes indexer definitions you choose to trust\. Add one/)).toBeInTheDocument()
    expect(screen.getByText(/Manual setup/)).toBeInTheDocument()
    const addButtons = screen.getAllByRole('button', { name: 'Add catalog source' })
    expect(addButtons).toHaveLength(2)
    await user.click(addButtons[1])
    expect(await screen.findByRole('dialog', { name: 'Add catalog source' })).toBeInTheDocument()
  })

  it('CatalogSourcesSection_shows_an_actionable_load_error', async () => {
    vi.mocked(discoveryApi.catalogSources).mockRejectedValue(new Error('Sources unavailable'))

    renderWithProviders(<CatalogSourcesSection />)

    expect(await screen.findByText('Sources unavailable')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('CatalogSourcesSection_add_sends_the_trimmed_request_and_reports_the_published_count', async () => {
    vi.mocked(discoveryApi.catalogSources).mockResolvedValue([])
    vi.mocked(discoveryApi.addCatalogSource).mockResolvedValue(aSource({ name: 'Household Catalog', entryCount: 5 }))
    const user = userEvent.setup()
    const { queryClient } = renderWithProviders(<CatalogSourcesSection />)
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    await user.click((await screen.findAllByRole('button', { name: 'Add catalog source' }))[0])
    const dialog = within(await screen.findByRole('dialog', { name: 'Add catalog source' }))
    expect(dialog.getByLabelText('Name')).toHaveFocus()
    await user.type(dialog.getByLabelText('Name'), '  Household Catalog ')
    await user.type(dialog.getByLabelText('URL'), ' https://catalog.example/indexers.json ')
    await user.click(dialog.getByRole('button', { name: 'Add source' }))

    await waitFor(() =>
      expect(discoveryApi.addCatalogSource).toHaveBeenCalledWith({
        name: 'Household Catalog',
        url: 'https://catalog.example/indexers.json',
      }),
    )
    expect(await screen.findByText('Household Catalog was added')).toBeInTheDocument()
    expect(screen.getByText(/It publishes 5 indexers/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Add catalog source' })).not.toBeInTheDocument()
    expectCatalogInvalidated(invalidate)
  })

  it('CatalogSourcesSection_add_keeps_a_source_whose_first_refresh_failed_and_says_why', async () => {
    vi.mocked(discoveryApi.catalogSources).mockResolvedValue([])
    vi.mocked(discoveryApi.addCatalogSource).mockResolvedValue(
      aSource({
        name: 'Unreachable Catalog',
        lastRefreshSucceeded: false,
        lastRefreshCode: 'discovery.catalog_source.fetch_failed',
        lastRefreshMessage: 'The catalog could not be fetched.',
        entryCount: 0,
      }),
    )
    const user = userEvent.setup()
    renderWithProviders(<CatalogSourcesSection />)

    await user.click((await screen.findAllByRole('button', { name: 'Add catalog source' }))[0])
    const dialog = within(await screen.findByRole('dialog', { name: 'Add catalog source' }))
    await user.type(dialog.getByLabelText('Name'), 'Unreachable Catalog')
    await user.type(dialog.getByLabelText('URL'), 'https://catalog.example/missing.json')
    await user.click(dialog.getByRole('button', { name: 'Add source' }))

    const notice = await screen.findByRole('alert')
    expect(within(notice).getByText('Unreachable Catalog was added, but its first refresh failed')).toBeInTheDocument()
    expect(within(notice).getByText('The catalog could not be fetched.')).toBeInTheDocument()
  })

  it('CatalogSourcesSection_add_rejects_a_malformed_url_before_calling_the_API', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CatalogSourcesSection />)

    await user.click(await screen.findByRole('button', { name: 'Add catalog source' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add catalog source' }))
    await user.type(dialog.getByLabelText('Name'), 'Typo')
    await user.type(dialog.getByLabelText('URL'), 'catalog.example/indexers.json')
    await user.click(dialog.getByRole('button', { name: 'Add source' }))

    expect(await dialog.findByText('Enter a full address, starting with https://.')).toBeInTheDocument()
    expect(dialog.getByLabelText('URL')).toHaveAttribute('aria-invalid', 'true')
    expect(discoveryApi.addCatalogSource).not.toHaveBeenCalled()
  })

  it('CatalogSourcesSection_add_shows_the_server_message_when_the_source_is_refused', async () => {
    vi.mocked(discoveryApi.addCatalogSource).mockRejectedValue(
      new ApiError(409, 'discovery.catalog_source.duplicate_url', 'A catalog source with this URL already exists.'),
    )
    const user = userEvent.setup()
    renderWithProviders(<CatalogSourcesSection />)

    await user.click(await screen.findByRole('button', { name: 'Add catalog source' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add catalog source' }))
    await user.type(dialog.getByLabelText('Name'), 'Again')
    await user.type(dialog.getByLabelText('URL'), 'https://catalog.example/indexers.json')
    await user.click(dialog.getByRole('button', { name: 'Add source' }))

    expect(await dialog.findByText('A catalog source with this URL already exists.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Add catalog source' })).toBeInTheDocument()
  })

  it('CatalogSourcesSection_refresh_calls_the_source_and_refetches_sources_and_entries', async () => {
    vi.mocked(discoveryApi.refreshCatalogSource).mockResolvedValue(aSource())
    const user = userEvent.setup()
    const { queryClient } = renderWithProviders(<CatalogSourcesSection />)
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    await user.click(await screen.findByRole('button', { name: 'Refresh Example Catalog' }))

    await waitFor(() => expect(discoveryApi.refreshCatalogSource).toHaveBeenCalledWith('source-1'))
    await waitFor(() => expectCatalogInvalidated(invalidate))
  })

  it('CatalogSourcesSection_refresh_error_is_shown_on_the_row', async () => {
    vi.mocked(discoveryApi.refreshCatalogSource).mockRejectedValue(
      new ApiError(404, 'discovery.catalog_source.not_found', 'That catalog source no longer exists.'),
    )
    const user = userEvent.setup()
    renderWithProviders(<CatalogSourcesSection />)

    await user.click(await screen.findByRole('button', { name: 'Refresh Example Catalog' }))

    expect(await screen.findByText('That catalog source no longer exists.')).toBeInTheDocument()
  })

  it('CatalogSourcesSection_toggle_and_rename_send_the_complete_update', async () => {
    vi.mocked(discoveryApi.updateCatalogSource).mockResolvedValue(aSource())
    const user = userEvent.setup()
    renderWithProviders(<CatalogSourcesSection />)

    await user.click(await screen.findByRole('switch', { name: 'Use Example Catalog' }))
    await waitFor(() =>
      expect(discoveryApi.updateCatalogSource).toHaveBeenCalledWith('source-1', { name: 'Example Catalog', enabled: false }),
    )

    await openSourceAction(user, 'Example Catalog', 'Edit name')
    const dialog = within(await screen.findByRole('dialog', { name: 'Rename Example Catalog' }))
    const name = dialog.getByLabelText('Name')
    await user.clear(name)
    await user.type(name, 'Shared Catalog')
    await user.click(dialog.getByRole('button', { name: 'Save name' }))

    await waitFor(() =>
      expect(discoveryApi.updateCatalogSource).toHaveBeenLastCalledWith('source-1', { name: 'Shared Catalog', enabled: true }),
    )
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Rename Example Catalog' })).not.toBeInTheDocument())
  })

  it('CatalogSourcesSection_removes_only_after_confirming_that_installed_indexers_are_kept', async () => {
    vi.mocked(discoveryApi.removeCatalogSource).mockResolvedValue(undefined)
    const user = userEvent.setup()
    const { queryClient } = renderWithProviders(<CatalogSourcesSection />)
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    await openSourceAction(user, 'Example Catalog', 'Remove')

    const dialog = within(await screen.findByRole('dialog', { name: 'Remove Example Catalog?' }))
    expect(dialog.getByText(/are kept and keep working/)).toBeInTheDocument()
    expect(discoveryApi.removeCatalogSource).not.toHaveBeenCalled()

    await user.click(dialog.getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog', { name: 'Remove Example Catalog?' })).not.toBeInTheDocument()
    expect(discoveryApi.removeCatalogSource).not.toHaveBeenCalled()

    await openSourceAction(user, 'Example Catalog', 'Remove')
    const confirm = within(await screen.findByRole('dialog', { name: 'Remove Example Catalog?' }))
    await user.click(confirm.getByRole('button', { name: 'Remove source' }))

    await waitFor(() => expect(discoveryApi.removeCatalogSource).toHaveBeenCalledWith('source-1'))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Remove Example Catalog?' })).not.toBeInTheDocument())
    expectCatalogInvalidated(invalidate)
  })
})
