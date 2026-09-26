import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { discoveryApi } from '@/api/endpoints'
import type {
  DefinitionFieldIssue,
  IndexerDefinitionSummary,
  ReleaseCandidate,
  ValidateIndexerDefinitionResponse,
} from '@/api/types'
import { errorMessage } from '@/lib/api'
import { formatBytes } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { Modal } from '@/ui/Modal'
import { PlusIcon } from '@/ui/icons'
import { TextArea } from '@/ui/TextArea'
import { TextField } from '@/ui/TextField'

/** Shared with `AddIndexerModal`'s definition picker, so both read the same cached list. */
export const indexerDefinitionsQueryKey = ['indexer-definitions']

/**
 * Uploaded declarative indexer definitions (scraped HTML/JSON sites with neither a Torznab nor
 * a Newznab API — see `IndexerDefinitionParser`) plus a dry-run preview so an operator can check what
 * a definition would extract before it ever reaches a real site. This surface intentionally has no
 * edit or delete: neither exists anywhere in this system yet, matching the Indexers table above it.
 */
export function IndexerDefinitionsSection() {
  const [uploading, setUploading] = useState(false)

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: indexerDefinitionsQueryKey,
    queryFn: discoveryApi.definitions,
  })

  const columns: Column<IndexerDefinitionSummary>[] = [
    { key: 'name', header: 'Name', render: (definition) => <span className="font-medium text-fg">{definition.name}</span> },
    { key: 'schemaVersion', header: 'Schema', align: 'end', render: (definition) => definition.schemaVersion },
    {
      key: 'contentHash',
      header: 'Content hash',
      width: '14rem',
      render: (definition) => (
        <span className="block truncate font-mono text-xs text-faint">{definition.contentHash}</span>
      ),
    },
    {
      key: 'createdAt',
      header: 'Uploaded',
      render: (definition) => new Date(definition.createdAt).toLocaleString(),
    },
  ]

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold tracking-tight text-fg">Indexer definitions</h2>
          <p className="mt-1 text-sm text-muted">
            Declarative definitions for a site that speaks neither Torznab nor Newznab. Select one for a
            &quot;Definition&quot; protocol indexer above.
          </p>
        </div>
        <Button size="sm" icon={<PlusIcon className="size-4" />} onClick={() => setUploading(true)}>
          Upload definition
        </Button>
      </div>

      {isError ? (
        <ErrorState message={errorMessage(error, 'Could not load indexer definitions.')} onRetry={() => void refetch()} />
      ) : (
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(definition) => definition.id}
          caption="Uploaded indexer definitions"
          loading={isPending}
          empty={
            <EmptyState
              title="No definitions uploaded"
              description="Upload a definition to add an indexer that has no Torznab or Newznab API."
            />
          }
        />
      )}

      <UploadDefinitionModal open={uploading} onClose={() => setUploading(false)} />
    </div>
  )
}

function UploadDefinitionModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [rawContent, setRawContent] = useState('')
  const [sampleRequestUrl, setSampleRequestUrl] = useState('')
  const [sampleResponseBody, setSampleResponseBody] = useState('')
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewResult, setPreviewResult] = useState<ValidateIndexerDefinitionResponse | null>(null)

  function reset() {
    setName('')
    setRawContent('')
    setSampleRequestUrl('')
    setSampleResponseBody('')
    setUploadError(null)
    setPreviewError(null)
    setPreviewResult(null)
  }

  const preview = useMutation({
    mutationFn: () =>
      discoveryApi.validateDefinition({
        rawContent,
        sampleResponseBody: sampleResponseBody || undefined,
        sampleRequestUrl: sampleRequestUrl || undefined,
      }),
    onSuccess: (result) => {
      setPreviewError(null)
      setPreviewResult(result)
    },
    onError: (err) => {
      setPreviewResult(null)
      setPreviewError(errorMessage(err, 'Could not validate the definition.'))
    },
  })

  const upload = useMutation({
    mutationFn: () => discoveryApi.uploadDefinition({ name, rawContent }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: indexerDefinitionsQueryKey })
      onClose()
      reset()
    },
    onError: (err) => setUploadError(errorMessage(err, 'Could not upload the definition.')),
  })

  function onPreview() {
    setUploadError(null)
    preview.mutate()
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setUploadError(null)
    upload.mutate()
  }

  return (
    <Modal open={open} onClose={onClose} title="Upload indexer definition">
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <TextField label="Name" required value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        <TextArea
          label="Definition (JSON)"
          required
          rows={10}
          placeholder='{ "schemaVersion": 1, "resultKind": "Torrent", "search": { ... } }'
          value={rawContent}
          onChange={(e) => setRawContent(e.target.value)}
        />

        <div className="rounded-control border border-line p-3">
          <p className="text-sm font-medium text-muted">Test extraction (optional)</p>
          <p className="mt-1 text-xs text-faint">
            Paste a sample response this definition would receive to preview what it extracts. This never
            contacts a real site.
          </p>
          <div className="mt-3 flex flex-col gap-3">
            <TextField
              label="Sample request URL"
              type="url"
              placeholder="https://indexer.example/search?q=Interstellar"
              value={sampleRequestUrl}
              onChange={(e) => setSampleRequestUrl(e.target.value)}
            />
            <TextArea
              label="Sample response body"
              rows={6}
              placeholder="<table>...</table>"
              value={sampleResponseBody}
              onChange={(e) => setSampleResponseBody(e.target.value)}
            />
            <div>
              <Button type="button" variant="subtle" size="sm" loading={preview.isPending} onClick={onPreview}>
                Preview extraction
              </Button>
            </div>

            {previewError && <Alert tone="danger">{previewError}</Alert>}
            {previewResult && (
              <>
                <FieldIssues issues={previewResult.fieldIssues} />
                <PreviewCandidates candidates={previewResult.candidates} />
              </>
            )}
          </div>
        </div>

        {uploadError && <Alert tone="danger">{uploadError}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={upload.isPending}>
            Upload definition
          </Button>
        </div>
      </form>
    </Modal>
  )
}

/**
 * The rules that produced nothing. Without these a broken definition previews exactly like a working
 * one — a size rule that cannot read "473K" yields 0 bytes, which is also what a site that publishes
 * no size yields — so this list is the whole reason the dry run can be used to debug a definition.
 */
function FieldIssues({ issues }: { issues: DefinitionFieldIssue[] }) {
  if (issues.length === 0) {
    return null
  }

  return (
    <Alert tone="warning" title={`${issues.length} field ${issues.length === 1 ? 'value' : 'values'} could not be read`}>
      <ul className="flex flex-col gap-1">
        {issues.map((issue) => (
          <li key={`${issue.rowIndex}-${issue.field}-${issue.code}`}>
            <span className="font-mono text-xs">
              row {issue.rowIndex} · {issue.field}
            </span>{' '}
            {issue.message}
          </li>
        ))}
      </ul>
    </Alert>
  )
}

function PreviewCandidates({ candidates }: { candidates: ReleaseCandidate[] }) {
  if (candidates.length === 0) {
    return <Alert tone="info">The definition parsed, but extracted no candidates from the sample response.</Alert>
  }

  return (
    <ul className="flex flex-col gap-2">
      {candidates.map((candidate) => (
        <li key={candidate.guid} className="rounded-control border border-line bg-elevated p-2 text-sm">
          <p className="font-medium text-fg">{candidate.title}</p>
          <p className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-faint">
            <Badge tone="neutral">{candidate.protocol}</Badge>
            {candidate.sizeBytes > 0 && <span>{formatBytes(candidate.sizeBytes)}</span>}
            {candidate.seeders !== null && <span>{candidate.seeders} seeders</span>}
          </p>
        </li>
      ))}
    </ul>
  )
}
