import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { importsApi } from '@/api/endpoints'
import type { PathRepairEntry, PathRepairOutcome } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'

const PREVIEW_QUERY_KEY = ['imports', 'path-repair']

const OUTCOME_TONE: Record<PathRepairOutcome, Tone> = {
  Repairable: 'warning',
  Repaired: 'success',
  Announced: 'info',
  Blocked: 'danger',
  Absent: 'neutral',
}

// Repaired never comes back from the preview — Classify() on the server only ever produces the other
// four while nothing has moved yet. It is only listed here so this map stays exhaustive over the wire
// type, and so a row still reads sensibly on the rare chance a run and its preview race.
const OUTCOME_LABEL: Record<PathRepairOutcome, string> = {
  Repairable: 'Needs repair',
  Repaired: 'Repaired',
  Announced: 'Record out of date',
  Blocked: 'Blocked',
  Absent: 'Not on disk',
}

function PathCell({ path }: { path: string }) {
  return (
    <span className="block truncate font-mono text-xs" title={path}>
      {path}
    </span>
  )
}

const ENTRY_COLUMNS: Column<PathRepairEntry>[] = [
  { key: 'from', header: 'From', width: '20rem', render: (entry) => <PathCell path={entry.from} /> },
  { key: 'to', header: 'To', width: '20rem', render: (entry) => <PathCell path={entry.to} /> },
  {
    key: 'outcome',
    header: 'Outcome',
    render: (entry) => <Badge tone={OUTCOME_TONE[entry.outcome]}>{OUTCOME_LABEL[entry.outcome]}</Badge>,
  },
  { key: 'sidecars', header: 'Sidecars', align: 'end', render: (entry) => entry.sidecars },
]

/**
 * Repairs the library names an earlier build wrote when its sanitiser was very nearly a no-op. The
 * preview below is a dry run — `GET /api/imports/path-repair` computed it without touching a single
 * file — and running the repair is a deliberate, confirmed, administrator-only action: nothing in this
 * installation runs it on its own, because renaming files in a library that already works is the
 * owner's call.
 */
export function PathRepairPanel() {
  const queryClient = useQueryClient()
  const [confirming, setConfirming] = useState(false)
  const [queuedRunId, setQueuedRunId] = useState<string | null>(null)

  const preview = useQuery({
    queryKey: PREVIEW_QUERY_KEY,
    queryFn: importsApi.pathRepairPreview,
  })

  const run = useMutation({
    mutationFn: () => importsApi.runPathRepair(),
    onMutate: () => setQueuedRunId(null),
    onSuccess: (accepted) => {
      setQueuedRunId(accepted.runId)
      setConfirming(false)
      // A receipt, not a result: the pass has not run yet, so re-asking the preview right away will
      // usually look unchanged. Invalidating is still correct — it is what picks up a repair that was
      // already queued and finished by the time this page is looked at again.
      void queryClient.invalidateQueries({ queryKey: PREVIEW_QUERY_KEY })
    },
  })

  return (
    <Card as="section" className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="font-medium text-fg">Library path repair</h2>
          <p className="mt-1 text-sm text-muted">
            Renames stored files whose name an earlier build wrote unsanitised. The list below is a
            preview — it is computed without touching anything on disk.
          </p>
        </div>
        <Button size="sm" disabled={!preview.data || preview.data.repairable === 0} onClick={() => setConfirming(true)}>
          Run repair
        </Button>
      </div>

      {queuedRunId && (
        <Alert tone="info" title="Repair queued">
          Run {queuedRunId} was queued on the command queue. Nothing has been renamed yet — the pass runs in
          the background, and this preview will not reflect it until that pass has actually run.
        </Alert>
      )}

      {preview.isPending ? (
        <LoadingBlock size="sm" label="Loading the repair preview" />
      ) : preview.isError ? (
        <ErrorState
          title="Could not load the repair preview"
          message={errorMessage(preview.error)}
          onRetry={() => void preview.refetch()}
        />
      ) : preview.data.entries.length === 0 ? (
        <EmptyState
          title="Nothing to repair"
          description="Every stored path already matches the current naming rules."
        />
      ) : (
        <>
          <div className="flex flex-wrap gap-2">
            <Badge tone={preview.data.repairable > 0 ? 'warning' : 'neutral'}>
              {preview.data.repairable} repairable
            </Badge>
            <Badge tone={preview.data.blocked > 0 ? 'danger' : 'neutral'}>{preview.data.blocked} blocked</Badge>
          </div>

          <DataTable
            columns={ENTRY_COLUMNS}
            rows={preview.data.entries}
            rowKey={(entry) => entry.assetId}
            caption="Library paths this repair pass would change, with the outcome the last preview computed for each."
          />

          <p className="text-xs text-faint">
            Needs repair means this pass would rename the file. Record out of date means the file is
            already at the corrected name and only the library's own record still points at the old one —
            running the pass only updates that record, it does not move anything. Blocked means a different
            file already occupies the corrected name — repair never overwrites, so a blocked entry is left
            exactly as it is until the conflicting name is resolved by hand. Not on disk means neither path
            currently exists, so this pass leaves it alone.
          </p>
        </>
      )}

      <RunRepairModal
        open={confirming}
        repairable={preview.data?.repairable ?? 0}
        blocked={preview.data?.blocked ?? 0}
        pending={run.isPending}
        error={run.isError ? errorMessage(run.error) : null}
        onConfirm={() => run.mutate()}
        onClose={() => setConfirming(false)}
      />
    </Card>
  )
}

function RunRepairModal({
  open,
  repairable,
  blocked,
  pending,
  error,
  onConfirm,
  onClose,
}: {
  open: boolean
  repairable: number
  blocked: number
  pending: boolean
  error: string | null
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Modal open={open} onClose={onClose} title="Run library path repair?">
      <div className="space-y-3 text-sm">
        <p className="text-fg">
          This renames {repairable} file{repairable === 1 ? '' : 's'} in your working library to their
          sanitised names.
        </p>
        <p className="text-muted">
          It never overwrites: if the sanitised name is already taken by a different file, that entry stays
          reported blocked and is left alone rather than replaced.
          {blocked > 0 &&
            ` ${blocked} entr${blocked === 1 ? 'y is' : 'ies are'} already blocked and will still be blocked after this run.`}
        </p>
        <p className="text-muted">
          Nothing in this installation runs this pass on its own — renaming files in a library that already
          works is your call, which is why it waits for this confirmation.
        </p>
      </div>

      {error && (
        <Alert tone="danger" className="mt-3">
          {error}
        </Alert>
      )}

      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={onClose} disabled={pending}>
          Cancel
        </Button>
        <Button type="button" variant="danger" loading={pending} disabled={pending} onClick={onConfirm}>
          Run repair
        </Button>
      </div>
    </Modal>
  )
}
