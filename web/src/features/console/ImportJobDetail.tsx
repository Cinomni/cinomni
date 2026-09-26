import { useQuery } from '@tanstack/react-query'
import { importsApi } from '@/api/endpoints'
import type {
  ImportFileMatch,
  ImportFileMatchState,
  ImportFileOperation,
  ImportJobState,
  ImportJobSummary,
  ImportOperationState,
  StateHistoryEntry,
} from '@/api/types'
import { errorMessage } from '@/lib/api'
import { formatBytes, formatDateTime, formatEpisodeCode } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { DataTable, type Column } from '@/ui/DataTable'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'

/** A job's own lifecycle state. Exported so the jobs list on `ImportsPage` uses the same mapping. */
export const IMPORT_JOB_STATE_TONE: Record<ImportJobState, Tone> = {
  Pending: 'neutral',
  Matching: 'info',
  Deciding: 'info',
  Operating: 'info',
  Probing: 'info',
  Registered: 'success',
  Rejected: 'danger',
  Unmatched: 'warning',
}

const FILE_MATCH_STATE_TONE: Record<ImportFileMatchState, Tone> = {
  Planned: 'neutral',
  Operated: 'info',
  Probed: 'info',
  Registered: 'success',
  Failed: 'danger',
  Unresolved: 'warning',
}

const OPERATION_STATE_TONE: Record<ImportOperationState, Tone> = {
  Planned: 'neutral',
  Executing: 'info',
  Verified: 'success',
  RolledBack: 'warning',
  Failed: 'danger',
}

/** `S03E04`, `S03E04, S03E05` for a multi-episode file, or `S03` alone, or a dash when unmatched. */
function fileEpisodeLabel(file: ImportFileMatch): string {
  const seasonNumber = file.seasonNumber
  if (seasonNumber == null) return '—'
  if (file.episodeNumbers.length === 0) return formatEpisodeCode(seasonNumber)
  return file.episodeNumbers.map((episodeNumber) => formatEpisodeCode(seasonNumber, episodeNumber)).join(', ')
}

function PathCell({ path }: { path: string }) {
  return (
    <span className="block truncate font-mono text-xs" title={path}>
      {path}
    </span>
  )
}

const FILE_COLUMNS: Column<ImportFileMatch>[] = [
  { key: 'seq', header: 'Seq', width: '3.5rem', render: (file) => file.seq },
  { key: 'episode', header: 'Episode', width: '8rem', render: (file) => fileEpisodeLabel(file) },
  { key: 'source', header: 'Source', width: '18rem', render: (file) => <PathCell path={file.sourcePath} /> },
  {
    key: 'target',
    header: 'Target',
    width: '18rem',
    render: (file) => (file.targetPath ? <PathCell path={file.targetPath} /> : <span className="text-faint">—</span>),
  },
  { key: 'size', header: 'Size', align: 'end', render: (file) => formatBytes(file.size) },
  {
    key: 'state',
    header: 'State',
    render: (file) => <Badge tone={FILE_MATCH_STATE_TONE[file.state]}>{file.state}</Badge>,
  },
  {
    key: 'reason',
    header: 'Reason',
    width: '16rem',
    render: (file) => (file.reason ? <span className="text-fg">{file.reason}</span> : <span className="text-faint">—</span>),
  },
]

const OPERATION_COLUMNS: Column<ImportFileOperation>[] = [
  { key: 'seq', header: 'Seq', width: '3.5rem', render: (operation) => operation.seq },
  { key: 'type', header: 'Type', render: (operation) => <Badge tone="neutral">{operation.type}</Badge> },
  { key: 'from', header: 'From', width: '16rem', render: (operation) => <PathCell path={operation.from} /> },
  { key: 'to', header: 'To', width: '16rem', render: (operation) => <PathCell path={operation.to} /> },
  {
    key: 'state',
    header: 'State',
    render: (operation) => <Badge tone={OPERATION_STATE_TONE[operation.state]}>{operation.state}</Badge>,
  },
  {
    key: 'verified',
    header: 'Verified',
    render: (operation) => (
      <Badge tone={operation.verified ? 'success' : 'warning'}>
        {operation.verified ? 'Verified' : 'Unverified'}
      </Badge>
    ),
  },
]

/**
 * One import job: its summary, the three trails `GET /api/imports/{id}` returns, and nothing this
 * client concludes on its own — every state and verdict shown here is what the Import module already
 * decided.
 *
 * `jobId` is always a real id; the caller mounts this component only once a job is selected, the same
 * convention `IntentDetail` uses, so `open` only ever toggles visibility of an id that is already known.
 */
export function ImportJobDetail({ jobId, open, onClose }: { jobId: string; open: boolean; onClose: () => void }) {
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['imports', jobId],
    queryFn: () => importsApi.get(jobId),
    enabled: open,
  })

  return (
    <Modal open={open} onClose={onClose} title="Import job" className="max-w-4xl">
      {isPending ? (
        <LoadingBlock size="md" label="Loading import job" />
      ) : isError ? (
        <ErrorState
          title="Could not load this import job"
          message={errorMessage(error)}
          onRetry={() => void refetch()}
        />
      ) : (
        <div className="space-y-5">
          <JobSummary job={data.job} />
          <FilesSection files={data.files} />
          <OperationsSection operations={data.operations} />
          <HistorySection history={data.history} />
        </div>
      )}
    </Modal>
  )
}

function JobSummary({ job }: { job: ImportJobSummary }) {
  const isMultiFile = job.fileCount > 1

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={IMPORT_JOB_STATE_TONE[job.state]}>{job.state}</Badge>
        <span className="text-sm text-muted">
          {job.fileCount} file{job.fileCount === 1 ? '' : 's'}
        </span>
      </div>

      <p className="break-all font-mono text-xs text-muted">{job.sourcePath}</p>

      {job.reason && <p className="text-sm text-fg">{job.reason}</p>}

      {isMultiFile ? (
        <Alert tone="info">
          This job landed {job.fileCount} files — a season pack still lands as one job. The target path and
          asset this job summary carries name only the first of them; see the files trail below for what
          happened to every one.
        </Alert>
      ) : (
        job.targetPath && <p className="break-all font-mono text-xs text-faint">→ {job.targetPath}</p>
      )}
    </div>
  )
}

function FilesSection({ files }: { files: ImportFileMatch[] }) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Files</h3>
      <DataTable
        columns={FILE_COLUMNS}
        rows={files}
        rowKey={(file) => String(file.seq)}
        caption="Every file this job matched, with its target and per-file state."
        empty={<p className="text-sm text-muted">No file has been matched yet.</p>}
      />
    </section>
  )
}

function OperationsSection({ operations }: { operations: ImportFileOperation[] }) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">File operations</h3>
      <p className="mb-2 text-xs text-faint">
        Type is what the platform actually did to the file, corrected after the fact if the plan changed —
        a hardlink the filesystem refuses lands here as a copy. Verified is separate from that: it only
        turns on once the result was confirmed on disk, so a row can show a type while still Unverified.
      </p>
      <DataTable
        columns={OPERATION_COLUMNS}
        rows={operations}
        rowKey={(operation) => String(operation.seq)}
        caption="Recoverable file operations this job performed, with what was verified on disk."
        empty={<p className="text-sm text-muted">No file operation has run yet.</p>}
      />
    </section>
  )
}

function HistorySection({ history }: { history: StateHistoryEntry[] }) {
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
