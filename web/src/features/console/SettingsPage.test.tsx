import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { operationsApi, playbackApi } from '@/api/endpoints'
import type { HardwareReport, OperationsSetting } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { SettingsPage } from './SettingsPage'

vi.mock('@/api/endpoints', () => ({
  operationsApi: {
    settings: vi.fn(),
    updateSetting: vi.fn(),
  },
  // The Playback section opens with the hardware report; its own behaviour is covered in HardwarePanel.test.
  playbackApi: {
    hardware: vi.fn(),
    probeHardware: vi.fn(),
  },
}))

const NO_HARDWARE: HardwareReport = {
  platform: null,
  ffmpegVersion: null,
  probedAt: null,
  hwaccels: [],
  backends: [],
  softwareHevc: false,
  toneMapping: false,
  subtitleOverlay: false,
  tests: [],
}

function aSetting(overrides: Partial<OperationsSetting> = {}): OperationsSetting {
  return {
    key: 'retention.operations.batchSize',
    kind: 'Number',
    isSecret: false,
    configurationPath: 'Retention:BatchSize',
    value: '5000',
    isSet: false,
    source: 'default',
    version: 0,
    defaultValue: '5000',
    ...overrides,
  }
}

const ELEVEN_SETTINGS: OperationsSetting[] = [
  aSetting({ key: 'decision.evaluationRetention', kind: 'Duration', value: '90.00:00:00', defaultValue: '90.00:00:00' }),
  aSetting({ key: 'retention.decision.interval', kind: 'Duration', value: '1.00:00:00', defaultValue: '1.00:00:00' }),
  aSetting({ key: 'metadata.snapshotRetention', kind: 'Duration', value: '30.00:00:00', defaultValue: '30.00:00:00' }),
  aSetting({ key: 'retention.metadata.interval', kind: 'Duration', value: '1.00:00:00', defaultValue: '1.00:00:00' }),
  aSetting({ key: 'backup.keepCount', kind: 'Number', value: '7', defaultValue: '7' }),
  aSetting({ key: 'retention.backup.interval', kind: 'Duration', value: '1.00:00:00', defaultValue: '1.00:00:00' }),
  aSetting({ key: 'retention.operations.outboxRetention', kind: 'Duration', value: '14.00:00:00', defaultValue: '14.00:00:00' }),
  aSetting({
    key: 'retention.operations.completedCommandRetention',
    kind: 'Duration',
    value: '30.00:00:00',
    defaultValue: '30.00:00:00',
  }),
  aSetting({
    key: 'retention.operations.failedCommandRetention',
    kind: 'Duration',
    value: '180.00:00:00',
    defaultValue: '180.00:00:00',
  }),
  aSetting({ key: 'retention.operations.batchSize', kind: 'Number', value: '5000', defaultValue: '5000' }),
  aSetting({ key: 'retention.operations.interval', kind: 'Duration', value: '1.00:00:00', defaultValue: '1.00:00:00' }),
]

beforeEach(() => {
  vi.mocked(operationsApi.settings).mockReset()
  vi.mocked(operationsApi.updateSetting).mockReset()
  vi.mocked(playbackApi.hardware).mockResolvedValue(NO_HARDWARE)
})

describe('SettingsPage', () => {
  it('SettingsPage_renders_the_eleven_settings_grouped_by_section_with_their_source_badge', async () => {
    // Arrange
    vi.mocked(operationsApi.settings).mockResolvedValue(ELEVEN_SETTINGS)

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert — every section heading and one representative field per section is present.
    expect(await screen.findByRole('heading', { name: 'Decision' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Metadata' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Backup' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Operations' })).toBeInTheDocument()
    expect(screen.getByLabelText('Purge batch size')).toHaveValue(5000)
    expect(screen.getAllByText('Default').length).toBeGreaterThan(0)
    // The catalogue grew, but a key the backend did not return must not be invented.
    expect(screen.queryByRole('heading', { name: 'Security' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Classification region')).not.toBeInTheDocument()
  })

  it('SettingsPage_renders_the_classification_region_and_the_sign_in_throttle_when_the_backend_returns_them', async () => {
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'metadata.contentRatingRegion',
        kind: 'Text',
        value: '',
        defaultValue: '',
        configurationPath: 'Metadata:ContentRatingRegion',
      }),
      aSetting({
        key: 'security.anonymousRateLimitPermits',
        kind: 'Number',
        value: '10',
        defaultValue: '10',
        configurationPath: 'Security:AnonymousRateLimit:Permits',
      }),
      aSetting({
        key: 'security.anonymousRateLimitWindow',
        kind: 'Duration',
        value: '00:01:00',
        defaultValue: '00:01:00',
        configurationPath: 'Security:AnonymousRateLimit:Window',
      }),
    ])
    vi.mocked(operationsApi.updateSetting).mockResolvedValue(
      aSetting({
        key: 'metadata.contentRatingRegion',
        kind: 'Text',
        value: 'ES',
        defaultValue: '',
        isSet: true,
        source: 'database',
        version: 1,
      }),
    )

    renderWithProviders(<SettingsPage />)

    expect(await screen.findByRole('heading', { name: 'Metadata' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Security' })).toBeInTheDocument()
    expect(screen.getByText('Empty means no age classification is read.', { exact: false })).toBeInTheDocument()
    expect(screen.getByLabelText('Sign-in attempts per client')).toHaveValue(10)
    expect(screen.getByText('1 minute to 1 hour', { exact: false })).toBeInTheDocument()

    const region = screen.getByLabelText('Classification region')
    const form = region.closest('form')
    if (!form) throw new Error('expected the region field to be wrapped in its own form')
    await user.type(region, 'ES')
    await user.click(within(form).getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(operationsApi.updateSetting).toHaveBeenCalledWith('metadata.contentRatingRegion', {
        value: 'ES',
        expectedVersion: 0,
      }),
    )
  })

  it('SettingsPage_renders_a_boolean_setting_as_a_checkbox_and_saves_the_toggled_value', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'playback.hardwareTranscodingEnabled',
        kind: 'Boolean',
        value: 'true',
        defaultValue: 'true',
      }),
    ])
    vi.mocked(operationsApi.updateSetting).mockResolvedValue(
      aSetting({
        key: 'playback.hardwareTranscodingEnabled',
        kind: 'Boolean',
        value: 'false',
        defaultValue: 'true',
        isSet: true,
        source: 'database',
        version: 1,
      }),
    )

    // Act
    renderWithProviders(<SettingsPage />)
    expect(await screen.findByRole('heading', { name: 'Playback' })).toBeInTheDocument()
    const checkbox = screen.getByLabelText('Hardware transcoding')
    expect(checkbox).toBeChecked()
    await user.click(checkbox)
    await user.click(screen.getByRole('button', { name: 'Save' }))

    // Assert
    await waitFor(() =>
      expect(operationsApi.updateSetting).toHaveBeenCalledWith('playback.hardwareTranscodingEnabled', {
        value: 'false',
        expectedVersion: 0,
      }),
    )
  })

  it('SettingsPage_renders_an_enum_setting_as_a_select_of_labelled_values_and_saves_the_raw_value', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'playback.hardwareBackend',
        kind: 'Enum',
        value: 'auto',
        defaultValue: 'auto',
        allowedValues: ['auto', 'vaapi', 'qsv', 'nvenc', 'amf'],
      }),
    ])
    vi.mocked(operationsApi.updateSetting).mockResolvedValue(
      aSetting({ key: 'playback.hardwareBackend', kind: 'Enum', value: 'nvenc', isSet: true, source: 'database', version: 1 }),
    )

    // Act
    renderWithProviders(<SettingsPage />)
    const select = await screen.findByRole('combobox', { name: 'Preferred hardware' })
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual([
      'Automatic',
      'VAAPI (AMD / Intel on Linux)',
      'Quick Sync (Intel)',
      'NVENC (NVIDIA)',
      'AMF (AMD on Windows)',
    ])
    expect(select).toHaveValue('auto')
    await user.selectOptions(select, 'NVENC (NVIDIA)')
    const form = select.closest('form')
    if (!form) throw new Error('expected the select to be wrapped in its own form')
    await user.click(within(form).getByRole('button', { name: 'Save' }))

    // Assert — the label is for the operator; the wire carries the backend's own value.
    await waitFor(() =>
      expect(operationsApi.updateSetting).toHaveBeenCalledWith('playback.hardwareBackend', {
        value: 'nvenc',
        expectedVersion: 0,
      }),
    )
  })

  it('SettingsPage_bounds_a_number_setting_by_the_min_and_max_the_backend_sends', async () => {
    // Arrange
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({ key: 'playback.videoQuality', kind: 'Number', value: '23', defaultValue: '23', minValue: 15, maxValue: 40 }),
    ])

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert
    const input = await screen.findByLabelText('Video quality (15–40)')
    expect(input).toHaveAttribute('min', '15')
    expect(input).toHaveAttribute('max', '40')
    expect(input).toHaveValue(23)
  })

  it('SettingsPage_shows_the_hardware_report_at_the_top_of_the_playback_section', async () => {
    // Arrange
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({ key: 'playback.hardwareTranscodingEnabled', kind: 'Boolean', value: 'true', defaultValue: 'true' }),
    ])

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert
    const heading = await screen.findByRole('heading', { name: 'Playback' })
    const card = heading.closest('section') as HTMLElement
    expect(await within(card).findByText('Transcoding hardware')).toBeInTheDocument()
    expect(within(card).getByRole('button', { name: 'Run hardware test' })).toBeInTheDocument()
  })

  it('SettingsPage_shows_a_retryable_error_state_when_the_list_fails_to_load', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings)
      .mockRejectedValueOnce(new ApiError(500, 'internal', 'Could not load settings.'))
      .mockResolvedValueOnce(ELEVEN_SETTINGS)

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert
    expect(await screen.findByText('Could not load settings.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByRole('heading', { name: 'Decision' })).toBeInTheDocument()
  })

  it('SettingsPage_shows_an_empty_state_when_the_backend_returns_no_settings', async () => {
    // Arrange
    vi.mocked(operationsApi.settings).mockResolvedValue([])

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert
    expect(await screen.findByText('No editable settings')).toBeInTheDocument()
  })

  it('SettingsPage_saves_a_changed_value_and_calls_updateSetting_with_the_expected_version', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue(ELEVEN_SETTINGS)
    vi.mocked(operationsApi.updateSetting).mockResolvedValue(
      aSetting({ key: 'retention.operations.batchSize', kind: 'Number', value: '10000', isSet: true, source: 'database', version: 1 }),
    )

    // Act — the page renders all eleven fields at once, most of them editable, so the save button is
    // scoped to this field's own form rather than assumed unique on the page.
    renderWithProviders(<SettingsPage />)
    const input = await screen.findByLabelText('Purge batch size')
    const form = input.closest('form')
    if (!form) throw new Error('expected the batch size field to be wrapped in its own form')
    await user.clear(input)
    await user.type(input, '10000')
    await user.click(within(form).getByRole('button', { name: 'Save' }))

    // Assert
    await waitFor(() =>
      expect(operationsApi.updateSetting).toHaveBeenCalledWith('retention.operations.batchSize', {
        value: '10000',
        expectedVersion: 0,
      }),
    )
  })

  it('SettingsPage_reverts_the_field_to_the_server_value_and_shows_the_error_on_a_rejected_write', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'retention.operations.outboxRetention',
        kind: 'Duration',
        value: '14.00:00:00',
        isSet: false,
        source: 'default',
        version: 0,
      }),
    ])
    vi.mocked(operationsApi.updateSetting).mockRejectedValue(
      new ApiError(400, 'settings.invalid_duration', 'The value is not a valid duration.'),
    )

    // Act
    renderWithProviders(<SettingsPage />)
    const input = await screen.findByLabelText('Outbox retention')
    await user.clear(input)
    await user.type(input, 'not-a-duration')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    // Assert — the error is shown, and the running configuration's value is what the field still shows.
    expect(await screen.findByText('The value is not a valid duration.')).toBeInTheDocument()
    await waitFor(() => expect(input).toHaveValue('14.00:00:00'))
  })

  it('SettingsPage_reloads_the_settings_after_a_version_conflict_so_the_next_save_carries_the_new_version', async () => {
    // Arrange — another operator saved the key after this page loaded.
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings)
      .mockResolvedValueOnce([aSetting({ value: '5000', version: 1 })])
      .mockResolvedValue([aSetting({ value: '7000', version: 2, isSet: true, source: 'database' })])
    vi.mocked(operationsApi.updateSetting).mockRejectedValueOnce(
      new ApiError(409, 'settings.conflict', 'The setting was changed by someone else.'),
    )

    // Act
    renderWithProviders(<SettingsPage />)
    const input = await screen.findByLabelText('Purge batch size')
    await user.clear(input)
    await user.type(input, '6000')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    // Assert — the page says what happened and shows the value (and version) that won.
    expect(await screen.findByText(/changed since the page loaded/)).toBeInTheDocument()
    await waitFor(() => expect(input).toHaveValue(7000))
    expect(operationsApi.settings).toHaveBeenCalledTimes(2)

    vi.mocked(operationsApi.updateSetting).mockResolvedValue(aSetting({ value: '6000', version: 3 }))
    await user.clear(input)
    await user.type(input, '6000')
    await user.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(operationsApi.updateSetting).toHaveBeenLastCalledWith('retention.operations.batchSize', {
        value: '6000',
        expectedVersion: 2,
      }),
    )
  })

  it('SettingsPage_never_renders_a_secret_value_and_starts_its_replace_field_empty', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'decision.evaluationRetention',
        kind: 'Secret',
        isSecret: true,
        value: null,
        isSet: true,
        source: 'database',
        version: 3,
      }),
    ])

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert — no real value ever renders; only the configured/not-set state and a control to replace it.
    expect(await screen.findByText('Configured')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Replace' }))
    const dialog = await screen.findByRole('dialog')
    const replaceInput = within(dialog).getByLabelText('New value')
    expect(replaceInput).toHaveValue('')
    expect(replaceInput).toHaveAttribute('type', 'password')
  })

  it('SettingsPage_offers_to_enter_a_missing_tmdb_key_under_metadata', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'metadata.tmdb.apiKey',
        kind: 'Secret',
        isSecret: true,
        configurationPath: 'Metadata:Tmdb:ApiKey',
        value: null,
        defaultValue: null,
      }),
    ])
    vi.mocked(operationsApi.updateSetting).mockResolvedValue(aSetting({ key: 'metadata.tmdb.apiKey' }))

    // Act
    renderWithProviders(<SettingsPage />)
    const section = await screen.findByRole('heading', { name: 'Metadata' })
    const card = section.closest('section') as HTMLElement
    expect(within(card).getByText('TMDB API key')).toBeInTheDocument()
    expect(within(card).getByText('Not set')).toBeInTheDocument()
    await user.click(within(card).getByRole('button', { name: 'Replace' }))
    await user.type(within(await screen.findByRole('dialog')).getByLabelText('New value'), 'a-new-key')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Replace' }))

    // Assert
    await waitFor(() =>
      expect(operationsApi.updateSetting).toHaveBeenCalledWith('metadata.tmdb.apiKey', {
        value: 'a-new-key',
        expectedVersion: 0,
      }),
    )
  })

  it('SettingsPage_counts_a_secret_supplied_by_the_configuration_file_as_configured', async () => {
    // Arrange — nothing is stored, but the file supplies a key, so the provider works.
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'metadata.tmdb.apiKey',
        kind: 'Secret',
        isSecret: true,
        value: null,
        isSet: false,
        source: 'file',
      }),
    ])

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert
    expect(await screen.findByText('Configured')).toBeInTheDocument()
    expect(screen.queryByText('Not set')).not.toBeInTheDocument()
  })

  it('SettingsPage_disables_editing_and_explains_a_setting_fixed_by_the_environment', async () => {
    // Arrange
    vi.mocked(operationsApi.settings).mockResolvedValue([
      aSetting({
        key: 'backup.keepCount',
        kind: 'Number',
        value: '3',
        isSet: false,
        source: 'environment',
        version: 0,
      }),
    ])

    // Act
    renderWithProviders(<SettingsPage />)

    // Assert
    const input = await screen.findByLabelText('Backups to keep')
    expect(input).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.getByText('Fixed by the environment or command line; not editable here.')).toBeInTheDocument()
  })
})
