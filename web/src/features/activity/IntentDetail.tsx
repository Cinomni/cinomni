import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { acquisitionApi, monitoringApi } from '@/api/endpoints'
import type {
  AcquisitionAttempt,
  AcquisitionIntentSummary,
  AttemptRelease,
  AttemptState,
  StateHistoryEntry,
} from '@/api/types'
import { errorMessage } from '@/lib/api'
import { formatDateTime } from '@/lib/format'
import { intentTone } from '@/lib/status'
import { ReleaseSource } from '@/features/search/ReleaseSource'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'

const ATTEMPT_TONE: Record<AttemptState, Tone> = {
  Started: 'neutral',
  Downloading: 'info',
  Imported: 'success',
  FailedDownload: 'danger',
  FailedImport: 'danger',
}

/**
 * Why an intent has (or has not) put a title in the library: its state and attempt budget as the
 * backend decided them, the attempt trail as evidence, and the state-history transitions.
 *
 * This never recomputes the intent's state from its attempts — the backend already made that call,
 * and the attempts here are only the record of what it tried.
 */
export function IntentDetail({
  intentId,
  title,
  open,
  onClose,
}: {
  intentId: string
  title: string
  open: boolean
  onClose: () => void
}) {
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['intents', intentId],
    queryFn: () => acquisitionApi.intent(intentId),
    enabled: open,
  })

  return (
    <Modal open={open} onClose={onClose} title={`Attempts — ${title}`} placement="side">
      {isPending ? (
        <LoadingBlock size="md" label="Loading attempts" />
      ) : isError ? (
        <ErrorState
          title="Could not load this intent"
          message={errorMessage(error)}
          onRetry={() => void refetch()}
        />
      ) : (
        <div className="space-y-5">
          <IntentSummary intent={data.intent} />
          <AttemptList attempts={data.attempts} />
          <HistoryList history={data.history} />
        </div>
      )}
    </Modal>
  )
}

/** The states a goal can be retried from: the backend refuses the others, so the button is not offered. */
const RETRYABLE = new Set<AcquisitionIntentSummary['state']>(['Searching', 'Exhausted'])

function IntentSummary({ intent }: { intent: AcquisitionIntentSummary }) {
  const isExhausted = intent.state === 'Exhausted'
  const isAvailable = intent.state === 'Available'
  const isCancelled = intent.state === 'Cancelled'

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={intentTone[intent.state]}>{intent.state}</Badge>
        <span className="text-sm text-muted">
          Attempt {intent.attemptCount} of {intent.maxAttempts}
        </span>
      </div>

      {RETRYABLE.has(intent.state) && <RetryNow intent={intent} />}

      {isCancelled ? (
        <Alert tone="info">This title was removed from the catalog, so nothing more will be tried for it.</Alert>
      ) : isExhausted ? (
        <Alert tone="danger" title="Stopped retrying">
          This intent used all {intent.maxAttempts} of its allowed attempts and will not search again on its
          own. That is different from still being in progress — see the attempts below for what each one
          tried and why it did not land.
        </Alert>
      ) : !isAvailable ? (
        <Alert tone="info">
          Still retrying: {intent.attemptCount} of {intent.maxAttempts} attempts used so far.
        </Alert>
      ) : null}
    </div>
  )
}

/**
 * Try again now: the backend reopens an exhausted goal with a fresh budget, then Monitoring runs a search
 * for its target. In that order — a search for a goal that cannot take a release would find one and drop it.
 */
function RetryNow({ intent }: { intent: AcquisitionIntentSummary }) {
  const queryClient = useQueryClient()
  const retry = useMutation({
    mutationFn: async () => {
      await acquisitionApi.retry(intent.id)
      await monitoringApi.searchTarget(intent.targetId)
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['intents'] })
    },
  })

  return (
    <div className="space-y-2">
      <Button size="sm" loading={retry.isPending} disabled={retry.isSuccess} onClick={() => retry.mutate()}>
        Retry now
      </Button>
      {retry.isSuccess && (
        <p className="text-xs text-muted">
          Searching again. The search runs in the background; this view updates when a release is chosen.
        </p>
      )}
      {retry.isError && <Alert tone="danger">{errorMessage(retry.error, 'Could not retry this title.')}</Alert>}
    </div>
  )
}

/**
 * The most recent attempt is shown first (highest ordinal first). This view exists to answer "why is
 * this title still not here", and that answer is almost always in the latest attempt — an operator
 * should not have to scan past every earlier try to find it.
 */
function AttemptList({ attempts }: { attempts: AcquisitionAttempt[] }) {
  if (attempts.length === 0) {
    return (
      <section>
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Attempts</h3>
        <p className="text-sm text-muted">No attempt has been made yet.</p>
      </section>
    )
  }

  const ordered = [...attempts].sort((a, b) => b.ordinal - a.ordinal)

  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">
        Attempts, most recent first
      </h3>
      <ul className="space-y-2">
        {ordered.map((attempt) => (
          <AttemptCard key={attempt.id} attempt={attempt} />
        ))}
      </ul>
    </section>
  )
}

function AttemptCard({ attempt }: { attempt: AcquisitionAttempt }) {
  return (
    <Card as="li" padding="sm" className="space-y-1.5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-sm font-medium text-fg">Attempt {attempt.ordinal}</span>
        <Badge tone={ATTEMPT_TONE[attempt.state]}>{attempt.state}</Badge>
      </div>
      {attempt.release ? (
        <ReleaseLine release={attempt.release} />
      ) : (
        <p className="break-all font-mono text-xs text-muted">{attempt.releaseGuid}</p>
      )}
      <div className="flex flex-wrap gap-x-4 gap-y-0.5 text-xs text-faint">
        <span>Started {formatDateTime(attempt.startedAt)}</span>
        <span>Closed {attempt.closedAt ? formatDateTime(attempt.closedAt) : '—'}</span>
      </div>
      {attempt.failureReason && (
        // Rendered in full, not truncated: the failure reason is the point of this whole view.
        <p className="whitespace-pre-wrap break-words text-sm text-danger">{attempt.failureReason}</p>
      )}
    </Card>
  )
}

/** What the attempt tried, as the indexer described it: the title, where it came from, and its swarm. */
function ReleaseLine({ release }: { release: AttemptRelease }) {
  return (
    <div className="space-y-0.5">
      <p className="break-words font-mono text-xs text-fg">{release.title}</p>
      <ReleaseSource indexerName={release.indexerName} seeders={release.seeders} leechers={release.leechers} />
    </div>
  )
}

function HistoryList({ history }: { history: StateHistoryEntry[] }) {
  if (history.length === 0) {
    return (
      <section>
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">State history</h3>
        <p className="text-sm text-muted">No state transition has been recorded yet.</p>
      </section>
    )
  }

  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">State history</h3>
      <ol className="space-y-2 border-l border-line pl-3">
        {history.map((entry) => (
          <li key={entry.seq} className="text-sm">
            <span className="text-fg">
              {entry.from} → {entry.to}
            </span>
            <span className="ml-2 text-xs text-faint">
              {entry.trigger} · {formatDateTime(entry.occurredAt)}
            </span>
            {entry.note && <p className="mt-0.5 text-xs text-muted">{entry.note}</p>}
          </li>
        ))}
      </ol>
    </section>
  )
}
