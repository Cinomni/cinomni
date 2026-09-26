import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { operationsApi } from '@/api/endpoints'
import type { CommandState, FailedCommand, QueueSnapshot, RetentionWindows, ScheduledJob } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatDateTime, formatRelative } from '@/lib/format'
import { useFallbackRefetchInterval } from '@/realtime/useRealtimeStatus'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Card } from '@/ui/Card'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Segmented } from '@/ui/Segmented'

/**
 * Past this age, an outbox backlog stops looking like ordinary traffic and starts looking like a
 * publisher that has stopped making progress. A backlog can be large simply because a burst of
 * work landed at once — depth alone does not distinguish "busy" from "stuck", but age does: the
 * oldest pending message only gets older than this if nothing has drained the queue in a while.
 */
const STUCK_BACKLOG_AGE_SECONDS = 15 * 60

const SECONDS_PER_MINUTE = 60
const SECONDS_PER_HOUR = 3600
const SECONDS_PER_DAY = 86400

/**
 * A duration in seconds as its two largest human units ("2d 3h", "5m 12s"). `formatDuration` in
 * `lib/format` renders clock time (h:mm:ss) for playback positions; retention windows and backlog
 * ages here can span days, where clock time stops being readable.
 */
function formatSecondsHuman(totalSeconds: number): string {
  if (!Number.isFinite(totalSeconds) || totalSeconds <= 0) return '0s'
  const units: readonly (readonly [string, number])[] = [
    ['d', SECONDS_PER_DAY],
    ['h', SECONDS_PER_HOUR],
    ['m', SECONDS_PER_MINUTE],
    ['s', 1],
  ]
  let remaining = Math.floor(totalSeconds)
  const parts: string[] = []
  for (const [suffix, size] of units) {
    const value = Math.floor(remaining / size)
    if (value > 0) {
      parts.push(`${value}${suffix}`)
      remaining -= value * size
    }
    if (parts.length === 2) break
  }
  return parts.length > 0 ? parts.join(' ') : '0s'
}

// --- Backlog ---------------------------------------------------------------------------------

const COMMAND_STATE_ORDER: readonly CommandState[] = ['Queued', 'Running', 'Completed', 'Failed']
const COMMAND_STATE_TONE: Record<CommandState, Tone> = {
  Queued: 'neutral',
  Running: 'info',
  Completed: 'success',
  Failed: 'danger',
}

function BacklogPanel({ queue }: { queue: QueueSnapshot }) {
  const isStuck = queue.outboxPending > 0 && queue.oldestOutboxAgeSeconds > STUCK_BACKLOG_AGE_SECONDS
  const badgeTone: Tone = queue.outboxPending === 0 ? 'neutral' : isStuck ? 'warning' : 'info'
  const badgeLabel = queue.outboxPending === 0 ? 'Empty' : isStuck ? 'Stuck' : 'Draining'

  const presentStates = COMMAND_STATE_ORDER.filter((state) => queue.commandsByState[state] !== undefined)

  return (
    <Card as="section" className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-medium text-fg">Backlog</h2>
        <Badge tone={badgeTone}>{badgeLabel}</Badge>
      </div>

      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div>
          <dt className="text-faint">Outbox pending</dt>
          <dd className="text-fg">{queue.outboxPending}</dd>
        </div>
        <div>
          <dt className="text-faint">Oldest pending message</dt>
          <dd className={cn('text-fg', isStuck && 'text-warning')}>
            {queue.outboxPending === 0 ? '—' : formatSecondsHuman(queue.oldestOutboxAgeSeconds)}
          </dd>
        </div>
      </dl>

      {presentStates.length > 0 && (
        <div>
          <p className="mb-1.5 text-xs text-faint">Commands by state</p>
          <div className="flex flex-wrap gap-2">
            {presentStates.map((state) => (
              <Badge key={state} tone={COMMAND_STATE_TONE[state]}>
                {state}: {queue.commandsByState[state]}
              </Badge>
            ))}
          </div>
        </div>
      )}

      {isStuck && (
        <Alert tone="warning">
          The oldest pending message has been waiting longer than {formatSecondsHuman(STUCK_BACKLOG_AGE_SECONDS)}.
        </Alert>
      )}

      <p className="text-xs text-faint">
        Depth alone does not tell a burst apart from a stall: a large backlog can simply be a lot of work
        that landed at once and is still draining. Age is what distinguishes them — a pending message past{' '}
        {formatSecondsHuman(STUCK_BACKLOG_AGE_SECONDS)} old means the publisher has stopped making progress
        on it, not that it is merely busy.
      </p>
    </Card>
  )
}

// --- Scheduled jobs ----------------------------------------------------------------------------

const JOB_COLUMNS: Column<ScheduledJob>[] = [
  { key: 'name', header: 'Name', render: (job) => job.name },
  { key: 'commandType', header: 'Command', render: (job) => job.commandType },
  { key: 'interval', header: 'Interval', render: (job) => formatSecondsHuman(job.intervalSeconds) },
  {
    key: 'lastRun',
    header: 'Last run',
    render: (job) => (job.lastRun ? formatRelative(job.lastRun) : 'Never run'),
  },
  { key: 'nextDue', header: 'Next due', render: (job) => formatDateTime(job.nextDue) },
  {
    key: 'enabled',
    header: 'Enabled',
    render: (job) => <Badge tone={job.enabled ? 'success' : 'neutral'}>{job.enabled ? 'Enabled' : 'Disabled'}</Badge>,
  },
]

function ScheduledJobsSection() {
  const jobs = useQuery({ queryKey: ['operations', 'jobs'], queryFn: operationsApi.jobs })

  return (
    <Card as="section" className="space-y-3">
      <h2 className="font-medium text-fg">Scheduled jobs</h2>

      {jobs.isPending ? (
        <LoadingBlock size="sm" label="Loading scheduled jobs" />
      ) : jobs.isError ? (
        <ErrorState
          title="Scheduled jobs could not be loaded"
          message={errorMessage(jobs.error)}
          onRetry={() => void jobs.refetch()}
        />
      ) : jobs.data.length === 0 ? (
        <EmptyState title="No scheduled jobs" description="This installation has no recurring job configured." />
      ) : (
        <>
          <DataTable
            columns={JOB_COLUMNS}
            rows={jobs.data}
            rowKey={(job) => job.name}
            caption="Recurring jobs configured on this installation, with when each last ran and is next due."
          />
          <p className="text-xs text-faint">
            This can show when a job last ran, never whether that run succeeded — outcome is not persisted
            anywhere in this system. A recent run only confirms the job fired, not that it finished cleanly.
          </p>
        </>
      )}
    </Card>
  )
}

// --- Failed commands -----------------------------------------------------------------------------

const FAILED_COMMAND_LIMIT_STEPS = [50, 100, 200] as const
type FailedCommandLimit = (typeof FAILED_COMMAND_LIMIT_STEPS)[number]

const FAILED_COMMAND_COLUMNS: Column<FailedCommand>[] = [
  { key: 'commandType', header: 'Command', render: (row) => row.commandType },
  { key: 'attempts', header: 'Attempts', render: (row) => `${row.attempts} of ${row.maxAttempts}` },
  {
    key: 'lastAttempt',
    header: 'Last attempt',
    render: (row) => (row.lastAttemptAt ? formatRelative(row.lastAttemptAt) : '—'),
  },
  {
    key: 'error',
    header: 'Error',
    render: (row) => (
      <p className="max-w-md whitespace-pre-wrap break-words text-fg">{row.error ?? 'No error recorded.'}</p>
    ),
  },
]

function FailedCommandsSection() {
  const [limit, setLimit] = useState<FailedCommandLimit>(FAILED_COMMAND_LIMIT_STEPS[0])

  const failedCommands = useQuery({
    queryKey: ['operations', 'commands', 'failed', limit],
    queryFn: () => operationsApi.failedCommands(limit),
  })

  return (
    <Card as="section" className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="font-medium text-fg">Failed commands</h2>
        <Segmented
          label="Show up to"
          options={FAILED_COMMAND_LIMIT_STEPS.map((step) => ({ value: String(step), label: String(step) }))}
          value={String(limit)}
          onChange={(value) => setLimit(Number(value) as FailedCommandLimit)}
        />
      </div>

      {failedCommands.isPending ? (
        <LoadingBlock size="sm" label="Loading failed commands" />
      ) : failedCommands.isError ? (
        <ErrorState
          title="Failed commands could not be loaded"
          message={errorMessage(failedCommands.error)}
          onRetry={() => void failedCommands.refetch()}
        />
      ) : failedCommands.data.length === 0 ? (
        <EmptyState
          title="No failed commands"
          description="Nothing on the queue has exhausted its retry attempts."
        />
      ) : (
        <>
          {failedCommands.data.length === limit && (
            <Alert tone="info">Showing the most recent {limit} failed commands — there may be more.</Alert>
          )}
          <DataTable
            columns={FAILED_COMMAND_COLUMNS}
            rows={failedCommands.data}
            rowKey={(row) => row.id}
            caption="Commands that exhausted their retry attempts, newest failure first."
          />
        </>
      )}
    </Card>
  )
}

// --- Retention -------------------------------------------------------------------------------

function RetentionPanel({ retention }: { retention: RetentionWindows }) {
  return (
    <Card as="section" className="space-y-3">
      <h2 className="font-medium text-fg">Retention</h2>
      <p className="text-sm text-muted">
        What this installation is configured to purge automatically and how often the sweep runs — this is
        the configured policy, not a record of what has actually happened to any row.
      </p>

      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div>
          <dt className="text-faint">Outbox messages kept for</dt>
          <dd className="text-fg">{formatSecondsHuman(retention.outboxRetentionSeconds)}</dd>
        </div>
        <div>
          <dt className="text-faint">Completed commands kept for</dt>
          <dd className="text-fg">{formatSecondsHuman(retention.completedCommandRetentionSeconds)}</dd>
        </div>
        <div>
          <dt className="text-faint">Failed commands kept for</dt>
          <dd className="text-fg">{formatSecondsHuman(retention.failedCommandRetentionSeconds)}</dd>
        </div>
        <div>
          <dt className="text-faint">Sweep interval</dt>
          <dd className="text-fg">{formatSecondsHuman(retention.intervalSeconds)}</dd>
        </div>
        <div>
          <dt className="text-faint">Rows removed per sweep</dt>
          <dd className="text-fg">{retention.batchSize}</dd>
        </div>
      </dl>
    </Card>
  )
}

// --- Page --------------------------------------------------------------------------------------

/**
 * Whether the operations spine — the outbox publisher, the recurring jobs, the command queue — is
 * actually moving. Every read here is presentation only: the page shows what the backend already
 * decided and recorded, and states plainly where the backend records nothing (a job's outcome) or
 * only a bounded slice (the failed-command list), rather than implying more than the data supports.
 */
/**
 * Matches the sampler's own cadence on the server. The queue snapshot is not free — the command half
 * is a grouped count over every row a retention window keeps — and the backend deliberately pays that
 * on a slow timer rather than on a caller's. Polling faster than the sampler would spend the cost
 * twice for a number that cannot have changed more often than it is measured.
 */
const QUEUE_REFRESH_MS = 15_000

export function OperationsPage() {
  const queueRefetchInterval = useFallbackRefetchInterval(QUEUE_REFRESH_MS)

  const queue = useQuery({
    queryKey: ['operations', 'queue'],
    queryFn: operationsApi.queue,
    refetchInterval: queueRefetchInterval,
  })

  const retention = useQuery({ queryKey: ['operations', 'retention'], queryFn: operationsApi.retention })

  return (
    <div className="space-y-4">
      {queue.isPending ? (
        <LoadingBlock size="sm" label="Loading backlog" />
      ) : queue.isError ? (
        <ErrorState
          title="Backlog could not be loaded"
          message={errorMessage(queue.error)}
          onRetry={() => void queue.refetch()}
        />
      ) : (
        <BacklogPanel queue={queue.data} />
      )}

      <ScheduledJobsSection />
      <FailedCommandsSection />

      {retention.isPending ? (
        <LoadingBlock size="sm" label="Loading retention configuration" />
      ) : retention.isError ? (
        <ErrorState
          title="Retention configuration could not be loaded"
          message={errorMessage(retention.error)}
          onRetry={() => void retention.refetch()}
        />
      ) : (
        <RetentionPanel retention={retention.data} />
      )}
    </div>
  )
}
