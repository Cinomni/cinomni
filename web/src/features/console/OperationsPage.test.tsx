import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { operationsApi } from '@/api/endpoints'
import type { FailedCommand, QueueSnapshot, RetentionWindows, ScheduledJob } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { OperationsPage } from './OperationsPage'

vi.mock('@/api/endpoints', () => ({
  operationsApi: {
    queue: vi.fn(),
    jobs: vi.fn(),
    failedCommands: vi.fn(),
    retention: vi.fn(),
  },
}))

function aQueueSnapshot(overrides: Partial<QueueSnapshot> = {}): QueueSnapshot {
  return {
    outboxPending: 3,
    oldestOutboxAgeSeconds: 12,
    commandsByState: { Queued: 2, Running: 1, Completed: 40, Failed: 1 },
    ...overrides,
  }
}

function aScheduledJob(overrides: Partial<ScheduledJob> = {}): ScheduledJob {
  return {
    name: 'outbox-publisher',
    commandType: 'PublishOutboxBatch',
    intervalSeconds: 30,
    lastRun: '2026-07-30T09:58:00Z',
    nextDue: '2026-07-30T10:00:30Z',
    enabled: true,
    ...overrides,
  }
}

function aFailedCommand(overrides: Partial<FailedCommand> = {}): FailedCommand {
  return {
    id: 'cmd-1',
    commandType: 'ImportFile',
    attempts: 5,
    maxAttempts: 5,
    lastAttemptAt: '2026-07-30T09:00:00Z',
    error: 'Timed out probing media info',
    ...overrides,
  }
}

function aRetentionWindows(overrides: Partial<RetentionWindows> = {}): RetentionWindows {
  return {
    outboxRetentionSeconds: 3600,
    completedCommandRetentionSeconds: 172800,
    failedCommandRetentionSeconds: 604800,
    batchSize: 100,
    intervalSeconds: 300,
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(operationsApi.queue).mockResolvedValue(aQueueSnapshot())
  vi.mocked(operationsApi.jobs).mockResolvedValue([aScheduledJob()])
  vi.mocked(operationsApi.failedCommands).mockResolvedValue([aFailedCommand()])
  vi.mocked(operationsApi.retention).mockResolvedValue(aRetentionWindows())
})

describe('OperationsPage', () => {
  it('OperationsPage_states_the_cap_when_the_failed_commands_page_comes_back_full', async () => {
    // Arrange — the endpoint only ever takes a limit, never an offset: a full page is genuinely
    // ambiguous between "that is everything" and "there is more past the cap", so the copy must
    // never claim completeness.
    const fullPage = Array.from({ length: 50 }, (_, index) => aFailedCommand({ id: `cmd-${index}` }))
    vi.mocked(operationsApi.failedCommands).mockResolvedValue(fullPage)

    // Act
    renderWithProviders(<OperationsPage />)

    // Assert
    expect(
      await screen.findByText('Showing the most recent 50 failed commands — there may be more.'),
    ).toBeInTheDocument()
  })

  it('OperationsPage_does_not_claim_a_cap_when_the_failed_commands_page_is_not_full', async () => {
    vi.mocked(operationsApi.failedCommands).mockResolvedValue([aFailedCommand()])

    renderWithProviders(<OperationsPage />)

    expect(await screen.findByText('Timed out probing media info')).toBeInTheDocument()
    expect(screen.queryByText(/there may be more/i)).not.toBeInTheDocument()
  })

  it('OperationsPage_raising_the_failed_commands_step_re_queries_with_the_new_limit', async () => {
    const user = userEvent.setup()
    renderWithProviders(<OperationsPage />)

    await screen.findByText('Timed out probing media info')
    await user.click(screen.getByRole('button', { name: '100' }))

    await waitFor(() => expect(operationsApi.failedCommands).toHaveBeenCalledWith(100))
  })

  it('OperationsPage_scheduled_jobs_table_renders_without_claiming_an_outcome', async () => {
    // Arrange — whether a job's last run succeeded is not persisted anywhere in this system, so the
    // table must show only that it ran, never a success/failure verdict for that run.
    renderWithProviders(<OperationsPage />)

    // Assert
    expect(await screen.findByText('outbox-publisher')).toBeInTheDocument()
    expect(screen.getByText('PublishOutboxBatch')).toBeInTheDocument()
    expect(
      screen.getByText(/never whether that run succeeded/i),
    ).toBeInTheDocument()
    // Only the configured enabled/disabled state is a badge on this row — never a success verdict.
    expect(screen.queryByText('Succeeded')).not.toBeInTheDocument()
    expect(screen.queryByText('Failed')).not.toBeInTheDocument()
  })

  it('OperationsPage_marks_a_backlog_stuck_once_the_oldest_message_passes_the_age_threshold', async () => {
    // Arrange — depth alone cannot tell a burst apart from a stall; age is the signal, so an old
    // message must read as a warning even when the pending count itself is unremarkable.
    vi.mocked(operationsApi.queue).mockResolvedValue(
      aQueueSnapshot({ outboxPending: 5, oldestOutboxAgeSeconds: 20 * 60 }),
    )

    renderWithProviders(<OperationsPage />)

    expect(await screen.findByText('Stuck')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })

  it('OperationsPage_reads_a_fresh_backlog_as_draining_not_stuck', async () => {
    vi.mocked(operationsApi.queue).mockResolvedValue(
      aQueueSnapshot({ outboxPending: 40, oldestOutboxAgeSeconds: 5 }),
    )

    renderWithProviders(<OperationsPage />)

    expect(await screen.findByText('Draining')).toBeInTheDocument()
    expect(screen.queryByText('Stuck')).not.toBeInTheDocument()
  })

  it('OperationsPage_renders_retention_windows_in_human_units_from_seconds', async () => {
    vi.mocked(operationsApi.retention).mockResolvedValue(
      aRetentionWindows({ completedCommandRetentionSeconds: 172800 }),
    )

    renderWithProviders(<OperationsPage />)

    expect(await screen.findByText('2d')).toBeInTheDocument()
  })

  it('OperationsPage_surfaces_a_retry_on_a_failed_backlog_read', async () => {
    vi.mocked(operationsApi.queue).mockRejectedValue(new Error('network down'))

    renderWithProviders(<OperationsPage />)

    expect(await screen.findByText('network down')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
  })
})
