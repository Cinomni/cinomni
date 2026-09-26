import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { downloadsApi } from '@/api/endpoints'
import type { DownloadFile, FilePriorityLevel, StateHistoryEntry } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { formatBytes, formatDateTime } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { DataTable, type Column } from '@/ui/DataTable'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'

const PRIORITY_OPTIONS: readonly FilePriorityLevel[] = ['Skip', 'Normal', 'High', 'Top']

/** The last path segment, so a per-row priority control has a short accessible name distinct from its siblings. */
function fileName(path: string): string {
  const segments = path.split(/[/\\]/)
  return segments[segments.length - 1] || path
}

/**
 * The files a download task carries — size and per-file priority — plus the transitions its state
 * machine has recorded. Priority edits are batched locally: sending one changes what the engine
 * fetches next, so this waits for an explicit Save rather than firing on every selection change.
 */
export function DownloadDetail({
  taskId,
  title,
  open,
  onClose,
}: {
  taskId: string
  title: string
  open: boolean
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<Record<string, FilePriorityLevel>>({})

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['downloads', taskId],
    queryFn: () => downloadsApi.get(taskId),
    enabled: open,
  })

  const save = useMutation({
    mutationFn: () => downloadsApi.setPriorities(taskId, { priorities: draft }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['downloads', taskId] })
      setDraft({})
    },
  })

  const dirty = Object.keys(draft).length > 0

  return (
    <Modal open={open} onClose={onClose} title={`Files — ${title}`} placement="side">
      {isPending ? (
        <LoadingBlock size="md" label="Loading download detail" />
      ) : isError ? (
        <ErrorState
          title="Could not load this download"
          message={errorMessage(error)}
          onRetry={() => void refetch()}
        />
      ) : (
        <div className="space-y-5">
          <FileList
            files={data.files}
            draft={draft}
            onChange={(index, priority) =>
              setDraft((prev) => ({ ...prev, [String(index)]: priority }))
            }
          />

          {save.isError && <Alert tone="danger">{errorMessage(save.error)}</Alert>}

          <div className="flex items-center justify-between gap-3">
            <p className="text-xs text-faint">
              {dirty
                ? 'Unsaved priority changes — the engine will not see them until you save.'
                : 'Priorities match what the engine is using now.'}
            </p>
            <Button size="sm" disabled={!dirty} loading={save.isPending} onClick={() => save.mutate()}>
              Save priorities
            </Button>
          </div>

          <HistoryList history={data.history} />
        </div>
      )}
    </Modal>
  )
}

function FileList({
  files,
  draft,
  onChange,
}: {
  files: DownloadFile[]
  draft: Record<string, FilePriorityLevel>
  onChange: (index: number, priority: FilePriorityLevel) => void
}) {
  if (files.length === 0) {
    return (
      <section>
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Files</h3>
        <p className="text-sm text-muted">No file list has been reported for this task yet.</p>
      </section>
    )
  }

  const columns: Column<DownloadFile>[] = [
    {
      key: 'path',
      header: 'File',
      render: (file) => (
        <span className="block max-w-[16rem] truncate" title={file.path}>
          {file.path}
        </span>
      ),
    },
    {
      key: 'size',
      header: 'Size',
      align: 'end',
      render: (file) => formatBytes(file.size),
    },
    {
      key: 'priority',
      header: 'Priority',
      render: (file) => (
        <Select
          label={fileName(file.path)}
          value={draft[String(file.index)] ?? file.priority}
          onChange={(event) => onChange(file.index, event.target.value as FilePriorityLevel)}
        >
          {PRIORITY_OPTIONS.map((level) => (
            <option key={level} value={level}>
              {level}
            </option>
          ))}
        </Select>
      ),
    },
  ]

  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">Files</h3>
      <DataTable
        columns={columns}
        rows={files}
        rowKey={(file) => String(file.index)}
        caption="Files in this download, with size and download priority"
      />
    </section>
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
