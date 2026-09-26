import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { discoveryApi } from '@/api/endpoints'
import type { IndexerCatalogEntry, IndexerSummary, IndexerTestResult } from '@/api/types'
import { formatDateTime } from '@/lib/format'
import { renderWithProviders } from '@/test/render'
import { IndexersPage } from './IndexersPage'

vi.mock('@/api/endpoints', () => ({
  discoveryApi: {
    indexers: vi.fn(),
    indexerCatalog: vi.fn(),
    installCatalogIndexer: vi.fn(),
    testCatalogIndexer: vi.fn(),
    catalogSources: vi.fn(),
    addCatalogSource: vi.fn(),
    refreshCatalogSource: vi.fn(),
    updateCatalogSource: vi.fn(),
    removeCatalogSource: vi.fn(),
    addIndexer: vi.fn(),
    setIndexerSettings: vi.fn(),
    testIndexer: vi.fn(),
    setCapabilities: vi.fn(),
    setCredential: vi.fn(),
    clearCredential: vi.fn(),
    setIndexerEnabled: vi.fn(),
    setIndexerPriority: vi.fn(),
    deleteIndexer: vi.fn(),
    definitions: vi.fn(),
    uploadDefinition: vi.fn(),
    validateDefinition: vi.fn(),
  },
}))

/** Every per-indexer action but enable and priority lives behind the row's actions menu. */
async function openIndexerAction(user: ReturnType<typeof userEvent.setup>, action: string): Promise<void> {
  const [trigger] = await screen.findAllByRole('button', { name: /^Actions for / })
  if (!trigger) throw new Error('No indexer actions menu rendered.')
  await user.click(trigger)
  await user.click(screen.getByRole('menuitem', { name: action }))
}

function anIndexer(overrides: Partial<IndexerSummary> = {}): IndexerSummary {
  return {
    id: 'indexer-1',
    name: 'Example Indexer',
    protocol: 'Torznab',
    catalogKey: null,
    catalogVersion: null,
    catalogSourceId: null,
    baseUrl: 'https://indexer.example/api',
    priority: 25,
    enabled: true,
    settings: {
      minimumSeeders: null,
      preferMagnet: true,
      queryLimit: null,
      grabLimit: null,
      limitsUnit: 'Day',
      useFlareSolverr: false,
    },
    lastTestedAt: null,
    lastTestSucceeded: null,
    lastTestCode: null,
    lastTestMessage: null,
    definitionId: null,
    credentialUsername: null,
    credentialState: 'None',
    declaresLogin: false,
    sessionState: 'None',
    lastLoginAt: null,
    capabilities: {
      supportsMovieSearch: true,
      supportsTvSearch: true,
      movieCategories: [2000, 2010],
      tvCategories: [5000],
      movieSearchParams: ['imdbid'],
      tvSearchParams: ['tvdbid', 'season'],
    },
    ...overrides,
  }
}

function catalogEntry(overrides: Partial<IndexerCatalogEntry> = {}): IndexerCatalogEntry {
  return {
    key: 'example-public',
    sourceId: 'source-1',
    sourceName: 'Example Catalog',
    version: 1,
    name: 'Example Public',
    description: 'A public indexer published by an example catalog.',
    protocol: 'Definition',
    releaseProtocol: 'Torrent',
    baseUrls: ['https://indexer.example/', 'https://mirror.indexer.example/'],
    requiresFlareSolverr: true,
    installedIndexerId: null,
    defaultPriority: 20,
    defaultSettings: {
      minimumSeeders: 2,
      preferMagnet: true,
      queryLimit: 100,
      grabLimit: null,
      limitsUnit: 'Day',
      useFlareSolverr: true,
    },
    ...overrides,
  }
}

const successfulTest: IndexerTestResult = {
  succeeded: true,
  code: 'discovery.indexer_test_succeeded',
  message: 'The indexer returned candidates.',
  candidateCount: 4,
  durationMs: 125,
  testedAt: '2026-08-22T12:00:00Z',
  authenticated: null,
}

beforeEach(() => {
  vi.mocked(discoveryApi.indexers).mockResolvedValue([anIndexer()])
  vi.mocked(discoveryApi.indexerCatalog).mockResolvedValue([])
  vi.mocked(discoveryApi.catalogSources).mockResolvedValue([])
  vi.mocked(discoveryApi.setCapabilities).mockResolvedValue(undefined)
  vi.mocked(discoveryApi.definitions).mockResolvedValue([])
})

describe('IndexersPage', () => {
  it('IndexersPage_disables_reorders_and_deletes_only_after_confirmation', async () => {
    const user = userEvent.setup()
    vi.mocked(discoveryApi.setIndexerEnabled).mockResolvedValue(undefined)
    vi.mocked(discoveryApi.setIndexerPriority).mockResolvedValue(undefined)
    vi.mocked(discoveryApi.deleteIndexer).mockResolvedValue(undefined)
    renderWithProviders(<IndexersPage />)

    expect(screen.queryByText(/do not exist anywhere in this system/i)).not.toBeInTheDocument()
    await user.click(await screen.findByRole('switch', { name: 'Enable Example Indexer' }))
    await waitFor(() => expect(discoveryApi.setIndexerEnabled).toHaveBeenCalledWith('indexer-1', false))

    const priority = screen.getByRole('spinbutton', { name: 'Priority for Example Indexer' })
    await user.clear(priority)
    await user.type(priority, '0')
    await user.tab()
    expect(discoveryApi.setIndexerPriority).not.toHaveBeenCalled()
    expect(await screen.findByText('Priority must be a whole number from 1 to 50.')).toBeInTheDocument()

    await user.clear(priority)
    await user.type(priority, '7')
    await user.tab()
    await waitFor(() => expect(discoveryApi.setIndexerPriority).toHaveBeenCalledWith('indexer-1', 7))

    await openIndexerAction(user, 'Delete')
    expect(discoveryApi.deleteIndexer).not.toHaveBeenCalled()
    const dialog = within(await screen.findByRole('dialog', { name: 'Delete Example Indexer?' }))
    expect(dialog.getByText(/definition document/i)).toBeInTheDocument()
    await user.click(dialog.getByRole('button', { name: 'Delete indexer' }))
    await waitFor(() => expect(discoveryApi.deleteIndexer).toHaveBeenCalledWith('indexer-1'))
  })

  it('IndexersPage_capabilities_form_rejects_a_non_integer_category_before_calling_the_API', async () => {
    // Arrange
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Edit capabilities')
    const movieCategories = await screen.findByLabelText('Movie categories')
    await user.clear(movieCategories)
    await user.type(movieCategories, '2000, not-a-number')

    // Act
    await user.click(screen.getByRole('button', { name: 'Save capabilities' }))

    // Assert — a visible field error, and no request built from an unparsed value.
    expect(
      await screen.findByText('Movie categories must be a comma-separated list of whole numbers.'),
    ).toBeInTheDocument()
    expect(discoveryApi.setCapabilities).not.toHaveBeenCalled()
  })

  it('IndexersPage_capabilities_form_sends_the_complete_shape_on_a_valid_submit', async () => {
    // Arrange — every field the request accepts, including ones left untouched, must still be sent:
    // an omitted field would keep the backend's previous value instead of what this form shows.
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Edit capabilities')
    const movieCategories = await screen.findByLabelText('Movie categories')
    await user.clear(movieCategories)
    await user.type(movieCategories, '2000, 2010, 2020')
    await user.click(screen.getByLabelText('Supports TV search'))

    // Act
    await user.click(screen.getByRole('button', { name: 'Save capabilities' }))

    // Assert
    await waitFor(() =>
      expect(discoveryApi.setCapabilities).toHaveBeenCalledWith('indexer-1', {
        supportsMovieSearch: true,
        supportsTvSearch: false,
        movieCategories: [2000, 2010, 2020],
        tvCategories: [5000],
        movieSearchParams: ['imdbid'],
        tvSearchParams: ['tvdbid', 'season'],
      }),
    )
  })

  it('IndexersPage_add_indexer_form_requires_a_definition_for_the_definition_protocol', async () => {
    // Arrange
    vi.mocked(discoveryApi.definitions).mockResolvedValue([
      { id: 'def-1', name: 'Example Tracker', schemaVersion: 1, contentHash: 'abc', createdAt: '2026-01-01T00:00:00Z' },
    ])
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)
    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))
    await user.click(dialog.getByRole('button', { name: 'Manual' }))
    await user.type(dialog.getByLabelText('Name'), 'Example')
    await user.type(dialog.getByLabelText('Base URL'), 'https://tracker.example/')
    await user.selectOptions(dialog.getByLabelText('Protocol'), 'Definition')

    // Act — submit the "Add indexer" form without picking a definition.
    await user.click(dialog.getByRole('button', { name: 'Add indexer' }))

    // Assert — a visible field error, and no request built without a definition to reference.
    expect(await dialog.findByText('Select the definition this indexer runs on.')).toBeInTheDocument()
    expect(discoveryApi.addIndexer).not.toHaveBeenCalled()
  })

  it('IndexersPage_add_indexer_form_sends_the_selected_definition_id', async () => {
    // Arrange
    vi.mocked(discoveryApi.definitions).mockResolvedValue([
      { id: 'def-1', name: 'Example Tracker', schemaVersion: 1, contentHash: 'abc', createdAt: '2026-01-01T00:00:00Z' },
    ])
    vi.mocked(discoveryApi.addIndexer).mockResolvedValue({ indexerId: 'indexer-2' })
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)
    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))
    await user.click(dialog.getByRole('button', { name: 'Manual' }))
    await user.type(dialog.getByLabelText('Name'), 'Example')
    await user.type(dialog.getByLabelText('Base URL'), 'https://tracker.example/')
    await user.selectOptions(dialog.getByLabelText('Protocol'), 'Definition')
    await user.selectOptions(await dialog.findByLabelText('Definition'), 'def-1')

    // Act
    await user.click(dialog.getByRole('button', { name: 'Add indexer' }))

    // Assert
    await waitFor(() =>
      expect(discoveryApi.addIndexer).toHaveBeenCalledWith({
        name: 'Example',
        baseUrl: 'https://tracker.example/',
        protocol: 'Definition',
        priority: 25,
        definitionId: 'def-1',
        credential: null,
      }),
    )
  })

  it('IndexersPage_add_indexer_sends_the_login_in_the_same_request', async () => {
    // A private indexer created and then keyed in a second call could exist, briefly or for good,
    // without the credential it needs; the credential rides on the add itself.
    vi.mocked(discoveryApi.addIndexer).mockResolvedValue({ indexerId: 'indexer-2' })
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)
    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))
    await user.click(dialog.getByRole('button', { name: 'Manual' }))
    await user.type(dialog.getByLabelText('Name'), 'Proxied')
    await user.type(dialog.getByLabelText('Base URL'), 'https://proxy.example/torznab')
    await user.click(dialog.getByRole('button', { name: 'Username & password' }))
    await user.type(dialog.getByLabelText(/Username/), '  operator ')
    await user.type(dialog.getByLabelText(/Password/), 'hunter2')

    await user.click(dialog.getByRole('button', { name: 'Add indexer' }))

    await waitFor(() =>
      expect(discoveryApi.addIndexer).toHaveBeenCalledWith(
        expect.objectContaining({
          protocol: 'Torznab',
          credential: { secret: 'hunter2', username: 'operator' },
        }),
      ),
    )
  })

  it('IndexersPage_catalog_shows_the_source_of_an_entry_and_prevents_a_duplicate_install', async () => {
    vi.mocked(discoveryApi.indexerCatalog).mockResolvedValue([
      catalogEntry({ installedIndexerId: 'indexer-1' }),
    ])
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))

    expect(await dialog.findByText('Example Public')).toBeInTheDocument()
    expect(dialog.getByText('From Example Catalog')).toBeInTheDocument()
    expect(dialog.getByText('FlareSolverr required')).toBeInTheDocument()
    expect(dialog.getByText('Installed')).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Already installed' })).toBeDisabled()
  })

  it('IndexersPage_catalog_shows_an_actionable_load_error', async () => {
    vi.mocked(discoveryApi.indexerCatalog).mockRejectedValue(new Error('Catalog unavailable'))
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))

    expect(await dialog.findByText('Catalog unavailable')).toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })

  it('IndexersPage_catalog_install_uses_the_source_route_server_defaults_and_the_complete_request_shape', async () => {
    vi.mocked(discoveryApi.indexerCatalog).mockResolvedValue([catalogEntry()])
    vi.mocked(discoveryApi.installCatalogIndexer).mockResolvedValue({ indexerId: 'indexer-2' })
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))
    await user.click(await dialog.findByRole('button', { name: 'Set up Example Public from Example Catalog' }))
    expect(dialog.getByLabelText('Name')).toHaveValue('Example Public')
    expect(dialog.getByLabelText('Base URL')).toHaveValue('https://indexer.example/')
    expect(dialog.getByLabelText('Priority')).toHaveValue('20')
    expect(dialog.getByLabelText('Use FlareSolverr')).toBeChecked()
    expect(dialog.getByLabelText('Use FlareSolverr')).toBeDisabled()

    await user.click(dialog.getByRole('button', { name: 'Install' }))

    await waitFor(() => expect(discoveryApi.installCatalogIndexer).toHaveBeenCalledWith('source-1', 'example-public', {
      name: 'Example Public',
      baseUrl: 'https://indexer.example/',
      priority: 20,
      settings: {
        minimumSeeders: 2,
        preferMagnet: true,
        queryLimit: 100,
        grabLimit: null,
        limitsUnit: 'Day',
        useFlareSolverr: true,
      },
    }))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add indexer' })).not.toBeInTheDocument())
  })

  it('IndexersPage_catalog_rejects_invalid_numeric_settings_before_install', async () => {
    vi.mocked(discoveryApi.indexerCatalog).mockResolvedValue([catalogEntry()])
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))
    await user.click(await dialog.findByRole('button', { name: 'Set up Example Public from Example Catalog' }))
    await user.clear(dialog.getByLabelText('Daily query limit'))
    await user.type(dialog.getByLabelText('Daily query limit'), '0')
    await user.click(dialog.getByRole('button', { name: 'Install' }))

    expect(await dialog.findByText('Query limit must be a positive whole number, or blank.')).toBeInTheDocument()
    expect(discoveryApi.installCatalogIndexer).not.toHaveBeenCalled()
  })

  it('IndexersPage_catalog_tests_the_unsaved_draft_and_clears_a_stale_result_after_editing', async () => {
    vi.mocked(discoveryApi.indexerCatalog).mockResolvedValue([catalogEntry()])
    vi.mocked(discoveryApi.testCatalogIndexer).mockResolvedValue(successfulTest)
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))
    await user.click(await dialog.findByRole('button', { name: 'Set up Example Public from Example Catalog' }))
    await user.clear(dialog.getByLabelText('Name'))
    await user.type(dialog.getByLabelText('Name'), 'My indexer')
    await user.click(dialog.getByRole('button', { name: 'Test' }))

    await waitFor(() => expect(discoveryApi.testCatalogIndexer).toHaveBeenCalledWith(
      'source-1',
      'example-public',
      expect.objectContaining({ name: 'My indexer' }),
    ))
    expect(await dialog.findByText('Test succeeded')).toBeInTheDocument()
    await user.type(dialog.getByLabelText('Name'), ' updated')
    expect(dialog.queryByText('Test succeeded')).not.toBeInTheDocument()
  })

  it('IndexersPage_settings_saves_the_complete_shape_and_tests_the_configured_indexer', async () => {
    vi.mocked(discoveryApi.setIndexerSettings).mockResolvedValue(undefined)
    vi.mocked(discoveryApi.testIndexer).mockResolvedValue(successfulTest)
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await openIndexerAction(user, 'Settings')
    const dialog = within(await screen.findByRole('dialog', { name: /Settings/ }))
    await user.clear(dialog.getByLabelText('Minimum seeders'))
    await user.type(dialog.getByLabelText('Minimum seeders'), '5')
    await user.click(dialog.getByLabelText('Prefer magnet links'))
    await user.click(dialog.getByRole('button', { name: 'Test' }))
    await waitFor(() => expect(discoveryApi.testIndexer).toHaveBeenCalledWith('indexer-1'))
    expect(await dialog.findByText('Test succeeded')).toBeInTheDocument()

    await user.click(dialog.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(discoveryApi.setIndexerSettings).toHaveBeenCalledWith('indexer-1', {
      minimumSeeders: 5,
      preferMagnet: false,
      queryLimit: null,
      grabLimit: null,
      limitsUnit: 'Day',
      useFlareSolverr: false,
    }))
  })

  it('IndexersPage_credential_modal_starts_empty_and_never_shows_a_stored_secret', async () => {
    // Arrange — an indexer that already has one. The API never sends the value, and the form must
    // not invent a placeholder for it either: an operator replaces it or leaves it alone.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ credentialState: 'Readable', credentialUsername: null }),
    ])

    // Act
    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Replace credential')

    // Assert
    const field = await screen.findByLabelText('API key')
    expect(field).toHaveValue('')
    expect(field).toHaveAttribute('type', 'password')
    expect(screen.getByText(/never shown here/i)).toBeInTheDocument()
  })

  it('IndexersPage_credential_modal_can_store_a_username_and_password_for_torznab', async () => {
    // A Torznab endpoint behind an authenticating proxy takes an account rather than a key.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([anIndexer({ protocol: 'Torznab' })])
    vi.mocked(discoveryApi.setCredential).mockResolvedValue(undefined)

    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Add credential')
    await user.click(screen.getByRole('button', { name: 'Username & password' }))
    await user.type(screen.getByLabelText(/Username/), 'operator')
    await user.type(screen.getByLabelText(/Password/), 'hunter2')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(discoveryApi.setCredential).toHaveBeenCalledWith('indexer-1', { secret: 'hunter2', username: 'operator' }),
    )
  })

  it('IndexersPage_credential_modal_sends_no_username_for_an_api_key', async () => {
    // The API key mode is the default for Torznab and must not offer a username to send.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([anIndexer({ protocol: 'Torznab' })])
    vi.mocked(discoveryApi.setCredential).mockResolvedValue(undefined)

    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Add credential')
    expect(screen.queryByLabelText('Username')).not.toBeInTheDocument()
    await user.type(screen.getByLabelText('API key'), 's3cr3t')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(discoveryApi.setCredential).toHaveBeenCalledWith('indexer-1', { secret: 's3cr3t', username: null }),
    )
  })

  it('IndexersPage_shows_whether_an_indexer_has_a_credential_without_revealing_it', async () => {
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ id: 'a', name: 'With key', credentialState: 'Readable', credentialUsername: 'operator' }),
      anIndexer({ id: 'b', name: 'Without key', credentialState: 'None' }),
    ])

    renderWithProviders(<IndexersPage />)

    expect(await screen.findByText('Configured')).toBeInTheDocument()
    expect(screen.getByText('None')).toBeInTheDocument()
    // The username is an identifier and is meant to be legible; nothing else about the credential is.
    expect(screen.getByText('operator')).toBeInTheDocument()
  })

  it('IndexersPage_does_not_report_a_credential_this_installation_cannot_read_as_configured', async () => {
    // Arrange — the ciphertext is intact but was written under a master key this host no longer has.
    // "There are bytes in the row" is not "this installation can use them", and only the second one
    // authenticates a search.
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ id: 'a', name: 'Rotated key', credentialState: 'MasterKeyChanged', credentialUsername: 'operator' }),
      anIndexer({ id: 'b', name: 'Working key', credentialState: 'Readable' }),
    ])

    // Act
    renderWithProviders(<IndexersPage />)

    // Assert — one badge each, and the unreadable one is not dressed up as the working one.
    expect(await screen.findByText('Unreadable — key changed')).toBeInTheDocument()
    expect(screen.getAllByText('Configured')).toHaveLength(1)
    // What it costs the operator, said on the page: that indexer is being queried without the account.
    const warning = within(screen.getByRole('alert'))
    expect(warning.getByText(/already queried unauthenticated/i)).toBeInTheDocument()
    expect(warning.getByText('Rotated key')).toBeInTheDocument()
    expect(warning.getByText(/stored under a different master key/i)).toBeInTheDocument()
  })

  it('IndexersPage_names_the_reason_a_stored_credential_cannot_be_read', async () => {
    // Arrange — the three failure classes are different deployment facts with different remedies,
    // so the console must not flatten them into one "error".
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ id: 'a', name: 'No key here', credentialState: 'MasterKeyMissing' }),
      anIndexer({ id: 'b', name: 'Damaged row', credentialState: 'Corrupt' }),
    ])

    // Act
    renderWithProviders(<IndexersPage />)

    // Assert
    expect(await screen.findByText('Unreadable — no master key')).toBeInTheDocument()
    expect(screen.getByText('Unreadable — corrupt')).toBeInTheDocument()
    expect(screen.getByText(/no master key is loaded/i)).toBeInTheDocument()
    expect(screen.getByText(/fails authentication under the master key this installation holds/i)).toBeInTheDocument()
  })

  it('IndexersPage_still_offers_replacing_a_credential_that_cannot_be_read', async () => {
    // A credential exists in every failure state, so the replace/remove affordances must stay — and
    // the modal must keep saying the stored secret is never shown, because it still never is.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ credentialState: 'MasterKeyMissing', credentialUsername: null }),
    ])

    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Replace credential')

    const dialog = within(await screen.findByRole('dialog', { name: /Credential for/ }))
    expect(dialog.getByRole('button', { name: 'Remove' })).toBeInTheDocument()
    expect(dialog.getByLabelText('API key')).toHaveValue('')
    expect(dialog.getByText(/never shown here/i)).toBeInTheDocument()
    expect(dialog.getByText(/cannot be read on this installation/i)).toBeInTheDocument()
  })

  it('IndexersPage_definitions_section_lists_uploaded_definitions', async () => {
    // Arrange
    vi.mocked(discoveryApi.definitions).mockResolvedValue([
      { id: 'def-1', name: 'Example Tracker', schemaVersion: 1, contentHash: 'abc123', createdAt: '2026-01-01T00:00:00Z' },
    ])

    // Act
    renderWithProviders(<IndexersPage />)

    // Assert
    expect(await screen.findByText('Example Tracker')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Upload definition' })).toBeInTheDocument()
  })

  it('IndexersPage_shows_whether_a_declared_login_actually_produced_a_session', async () => {
    // Arrange — a session is not a credential: it is what the declared login did with one. The badge
    // says what exists now, not a promise the site cannot revoke.
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ id: 'a', name: 'Torznab', protocol: 'Torznab', sessionState: 'None' }),
      anIndexer({ id: 'b', name: 'Waiting', sessionState: 'NotLoggedIn' }),
      anIndexer({ id: 'c', name: 'Kept', sessionState: 'Active', lastLoginAt: '2026-09-01T10:00:00Z' }),
      anIndexer({ id: 'd', name: 'Rejected', sessionState: 'Failed' }),
    ])

    // Act
    renderWithProviders(<IndexersPage />)

    // Assert
    expect(await screen.findByText('Signed out')).toBeInTheDocument()
    expect(screen.getByText('Signed in')).toBeInTheDocument()
    expect(screen.getByText('Sign-in failed')).toBeInTheDocument()
    // "No login" appears once for the Torznab indexer, which declares none; the badge is the only
    // session claim it makes.
    expect(screen.getAllByText('No login')).toHaveLength(1)
    // The captured-at timestamp is the honest "signed in at" an operator can compare against now.
    expect(screen.getByText(formatDateTime('2026-09-01T10:00:00Z'))).toBeInTheDocument()
  })

  it('IndexersPage_does_not_call_a_missing_credential_no_login', async () => {
    // Arrange — both rows report sessionState 'None', and that is the whole point: one declares no
    // login, the other declares one it has no usable credential for. Only `declaresLogin` separates
    // them, and they need opposite things from the operator.
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ id: 'a', name: 'Public', protocol: 'Torznab', sessionState: 'None', declaresLogin: false }),
      anIndexer({
        id: 'b',
        name: 'Private',
        protocol: 'Definition',
        sessionState: 'None',
        declaresLogin: true,
        credentialState: 'None',
      }),
    ])

    // Act
    renderWithProviders(<IndexersPage />)

    // Assert — the one that needs a credential says so, and does not claim it needs no login.
    expect(await screen.findByText('No usable credential')).toBeInTheDocument()
    expect(screen.getAllByText('No login')).toHaveLength(1)
  })

  it('IndexersPage_settings_offers_the_solver_browser_to_an_indexer_that_signs_in', async () => {
    // Arrange — a private tracker behind a browser challenge needs both: the browser solves the
    // challenge and the session travels on requests carrying the clearance.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([
      anIndexer({ protocol: 'Definition', declaresLogin: true, sessionState: 'Active' }),
    ])

    // Act
    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Settings')

    // Assert
    const useFlareSolverr = await screen.findByRole('checkbox', { name: 'Use FlareSolverr' })
    expect(useFlareSolverr).toBeEnabled()
    expect(screen.queryByText(/cannot carry the session’s cookies/i)).not.toBeInTheDocument()
  })

  it('IndexersPage_settings_leaves_the_solver_browser_to_the_operator_when_no_login_is_declared', async () => {
    // Arrange — the contrast case: without a declared login there is nothing to lock, and the
    // operator keeps the choice.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([anIndexer({ declaresLogin: false })])

    // Act
    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Settings')

    // Assert
    expect(await screen.findByRole('checkbox', { name: 'Use FlareSolverr' })).toBeEnabled()
  })

  it('IndexersPage_credential_modal_says_saving_resets_the_session', async () => {
    // The copy has to promise exactly what happens: a saved credential wipes the stored session, and
    // the next search — not the save — is what signs in with it.
    const user = userEvent.setup()
    vi.mocked(discoveryApi.indexers).mockResolvedValue([anIndexer({ protocol: 'Definition' })])

    renderWithProviders(<IndexersPage />)
    await openIndexerAction(user, 'Add credential')

    expect(await screen.findByText(/saving a credential resets the session/i)).toBeInTheDocument()
    expect(screen.getByText(/the next search signs in with it/i)).toBeInTheDocument()
  })

  it('IndexersPage_settings_test_says_whether_it_searched_authenticated', async () => {
    // Arrange — the definition declares a login and the sign-in worked, so the result says so; the
    // reverse case is what tells an operator their password expired without opening the logs.
    vi.mocked(discoveryApi.setIndexerSettings).mockResolvedValue(undefined)
    vi.mocked(discoveryApi.testIndexer).mockResolvedValue({ ...successfulTest, authenticated: true })
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await openIndexerAction(user, 'Settings')
    const dialog = within(await screen.findByRole('dialog', { name: /Settings/ }))
    await user.click(dialog.getByRole('button', { name: 'Test' }))

    // The verdict rides at the end of the result line, next to the candidate count it qualifies.
    expect(await dialog.findByText(/· Signed in$/)).toBeInTheDocument()

    vi.mocked(discoveryApi.testIndexer).mockResolvedValue({ ...successfulTest, authenticated: false })
    await user.click(dialog.getByRole('button', { name: 'Test' }))

    expect(await dialog.findByText(/· Not signed in$/)).toBeInTheDocument()
  })

  it('IndexersPage_catalog_without_sources_explains_them_and_offers_manual_setup', async () => {
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))

    expect(await dialog.findByText('No catalog sources')).toBeInTheDocument()
    expect(dialog.getByText(/A catalog source is a URL that publishes indexer definitions you choose to trust/)).toBeInTheDocument()
    await user.click(dialog.getByRole('button', { name: 'Use Manual setup' }))
    expect(dialog.getByLabelText('Base URL')).toHaveAttribute('type', 'url')
    expect(dialog.getByRole('button', { name: 'Manual' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('IndexersPage_catalog_with_sources_but_no_entries_says_the_catalog_is_empty', async () => {
    vi.mocked(discoveryApi.catalogSources).mockResolvedValue([
      {
        id: 'source-1',
        name: 'Example Catalog',
        url: 'https://catalog.example/indexers.json',
        enabled: false,
        createdAt: '2026-09-01T00:00:00Z',
        lastRefreshedAt: null,
        lastRefreshSucceeded: null,
        lastRefreshCode: null,
        lastRefreshMessage: null,
        entryCount: 0,
      },
    ])
    const user = userEvent.setup()
    renderWithProviders(<IndexersPage />)

    await user.click(await screen.findByRole('button', { name: 'Add indexer' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Add indexer' }))

    expect(await dialog.findByText('The catalog is empty')).toBeInTheDocument()
    expect(dialog.queryByText('No catalog sources')).not.toBeInTheDocument()
  })
})
