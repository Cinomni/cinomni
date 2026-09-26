import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { acquisitionApi, monitoringApi } from '@/api/endpoints'
import type {
  AcquisitionAttempt,
  AcquisitionIntentDetail as AcquisitionIntentDetailWire,
  AcquisitionIntentSummary,
  StateHistoryEntry,
} from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { IntentDetail } from './IntentDetail'

vi.mock('@/api/endpoints', () => ({
  acquisitionApi: { intent: vi.fn(), retry: vi.fn() },
  monitoringApi: { searchTarget: vi.fn() },
}))

function anIntent(overrides: Partial<AcquisitionIntentSummary> = {}): AcquisitionIntentSummary {
  return {
    id: 'intent-1',
    targetId: 'target-1',
    workId: 'work-1',
    state: 'Searching',
    attemptCount: 1,
    maxAttempts: 3,
    selectedReleaseGuid: null,
    ...overrides,
  }
}

function anAttempt(overrides: Partial<AcquisitionAttempt> = {}): AcquisitionAttempt {
  return {
    id: 'attempt-1',
    ordinal: 1,
    releaseGuid: 'guid-1',
    state: 'FailedDownload',
    startedAt: '2026-07-28T10:00:00Z',
    closedAt: '2026-07-28T10:05:00Z',
    failureReason: null,
    release: null,
    ...overrides,
  }
}

function aHistoryEntry(overrides: Partial<StateHistoryEntry> = {}): StateHistoryEntry {
  return {
    seq: 1,
    from: 'Requested',
    to: 'Searching',
    trigger: 'search-started',
    occurredAt: '2026-07-28T10:00:00Z',
    note: null,
    ...overrides,
  }
}

function aDetail(overrides: Partial<AcquisitionIntentDetailWire> = {}): AcquisitionIntentDetailWire {
  return {
    intent: anIntent(),
    attempts: [],
    history: [],
    ...overrides,
  }
}

describe('IntentDetail', () => {
  it('marks an exhausted intent as stopped retrying, not merely another badge value', async () => {
    // Arrange — Exhausted means the backend will not try again on its own; that must read as a
    // different situation from an intent that is simply mid-retry.
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({ intent: anIntent({ state: 'Exhausted', attemptCount: 3, maxAttempts: 3 }) }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    expect(await screen.findByText('Stopped retrying')).toBeInTheDocument()
    expect(screen.queryByText(/still retrying/i)).not.toBeInTheDocument()
  })

  it('presents an intent still within its attempt budget as still retrying, not stopped', async () => {
    // Arrange
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({ intent: anIntent({ state: 'Searching', attemptCount: 1, maxAttempts: 3 }) }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    expect(await screen.findByText(/still retrying/i)).toBeInTheDocument()
    expect(screen.queryByText('Stopped retrying')).not.toBeInTheDocument()
  })

  it('renders a failed attempt’s failure reason in full rather than dropping it', async () => {
    // Arrange — this view exists to answer "why is this title still not here"; the reason is the point.
    const reason = 'No release met the profile: every candidate scored below the minimum format score.'
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({
        intent: anIntent({ attemptCount: 1, maxAttempts: 3 }),
        attempts: [anAttempt({ ordinal: 1, state: 'FailedDownload', failureReason: reason })],
      }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    expect(await screen.findByText(reason)).toBeInTheDocument()
  })

  it('does not render a failure reason paragraph for an attempt that has none', async () => {
    // Arrange
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({
        intent: anIntent({ attemptCount: 1, maxAttempts: 3 }),
        attempts: [anAttempt({ ordinal: 1, state: 'Downloading', closedAt: null, failureReason: null })],
      }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    expect(await screen.findByText('Attempt 1')).toBeInTheDocument()
    expect(screen.queryByText(/No release met/)).not.toBeInTheDocument()
  })

  it('orders attempts with the most recent one first', async () => {
    // Arrange — the answer to "why is this still missing" is almost always in the latest attempt.
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({
        intent: anIntent({ attemptCount: 2, maxAttempts: 3 }),
        attempts: [
          anAttempt({ id: 'attempt-1', ordinal: 1, releaseGuid: 'guid-first' }),
          anAttempt({ id: 'attempt-2', ordinal: 2, releaseGuid: 'guid-second' }),
        ],
      }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    const releases = await screen.findAllByText(/^guid-(first|second)$/)
    expect(releases[0]).toHaveTextContent('guid-second')
    expect(releases[1]).toHaveTextContent('guid-first')
  })

  it('shows the state-history trail', async () => {
    // Arrange
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({
        intent: anIntent(),
        history: [aHistoryEntry({ from: 'Planned', to: 'Searching', trigger: 'search-started' })],
      }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    expect(await screen.findByText('Planned → Searching')).toBeInTheDocument()
  })

  it('shows a retryable error state when the intent cannot be loaded', async () => {
    // Arrange
    vi.mocked(acquisitionApi.intent).mockRejectedValue(new Error('network down'))

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Arrival" open onClose={() => {}} />)

    // Assert
    expect(await screen.findByText('Could not load this intent')).toBeInTheDocument()
  })

  it('IntentDetail_names_the_release_an_attempt_tried_with_its_indexer_and_swarm', async () => {
    // Arrange
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({
        attempts: [
          anAttempt({
            releaseGuid: 'definition:D8DE5001',
            release: { title: 'World.War.Z.2013.1080p', indexerName: 'Example Tracker', seeders: 304, leechers: 12 },
          }),
        ],
      }),
    )

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="World War Z" open onClose={() => {}} />)

    // Assert — what was tried, where it came from and how healthy it was; not an opaque hash.
    expect(await screen.findByText('World.War.Z.2013.1080p')).toBeInTheDocument()
    expect(screen.getByText(/Example Tracker/)).toBeInTheDocument()
    expect(screen.getByText(/304 seeders/)).toBeInTheDocument()
    expect(screen.getByText(/12 leechers/)).toBeInTheDocument()
  })

  it('IntentDetail_falls_back_to_the_release_id_for_an_attempt_recorded_before_titles_were_kept', async () => {
    vi.mocked(acquisitionApi.intent).mockResolvedValue(aDetail({ attempts: [anAttempt({ releaseGuid: 'guid-old' })] }))

    renderWithProviders(<IntentDetail intentId="intent-1" title="Film" open onClose={() => {}} />)

    expect(await screen.findByText('guid-old')).toBeInTheDocument()
  })

  it('IntentDetail_retries_an_exhausted_goal_and_asks_for_a_search_of_its_target', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(acquisitionApi.intent).mockResolvedValue(
      aDetail({ intent: anIntent({ state: 'Exhausted', attemptCount: 3, maxAttempts: 3 }) }),
    )
    vi.mocked(acquisitionApi.retry).mockResolvedValue(anIntent({ state: 'Searching', maxAttempts: 8 }))
    vi.mocked(monitoringApi.searchTarget).mockResolvedValue(undefined)

    // Act
    renderWithProviders(<IntentDetail intentId="intent-1" title="Film" open onClose={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Retry now' }))

    // Assert — reopen first, then search: a search for a goal that cannot take a release finds nothing.
    await waitFor(() => expect(monitoringApi.searchTarget).toHaveBeenCalledWith('target-1'))
    expect(acquisitionApi.retry).toHaveBeenCalledWith('intent-1')
    expect(vi.mocked(acquisitionApi.retry).mock.invocationCallOrder[0]).toBeLessThan(
      vi.mocked(monitoringApi.searchTarget).mock.invocationCallOrder[0],
    )
    expect(await screen.findByText(/Searching again/)).toBeInTheDocument()
  })

  it('IntentDetail_offers_no_retry_while_a_download_is_in_flight', async () => {
    vi.mocked(acquisitionApi.intent).mockResolvedValue(aDetail({ intent: anIntent({ state: 'Downloading' }) }))

    renderWithProviders(<IntentDetail intentId="intent-1" title="Film" open onClose={() => {}} />)

    expect(await screen.findByText('Downloading')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Retry now' })).not.toBeInTheDocument()
  })
})
