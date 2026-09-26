import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { discoveryApi } from '@/api/endpoints'
import type { CatalogSource, UpdateCatalogSourceRequest } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatDateTime } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { Menu } from '@/ui/Menu'
import { Modal } from '@/ui/Modal'
import { Skeleton } from '@/ui/Skeleton'
import { Switch } from '@/ui/Switch'
import { TextField } from '@/ui/TextField'
import { PlusIcon, TrashIcon } from '@/ui/icons'
import { catalogSourcesQueryKey, invalidateCatalogSources } from './indexerCatalogQueries'

/** What a catalog source is, said the same way wherever the concept is introduced. */
export const CATALOG_SOURCE_EXPLANATION =
  'A catalog source is a URL that publishes indexer definitions you choose to trust.'

/**
 * A shape check only, so an obvious typo is caught before a round trip. Whether the address is
 * acceptable, reachable and publishes a valid manifest is the server's call, and its message is shown.
 */
function isWellFormedUrl(raw: string): boolean {
  try {
    const url = new URL(raw.trim())
    return url.protocol === 'https:' || url.protocol === 'http:'
  } catch {
    return false
  }
}

function pluralIndexers(count: number): string {
  return `${count} ${count === 1 ? 'indexer' : 'indexers'}`
}

/** How the last refresh reads. The label carries the meaning on its own; the tone only reinforces it. */
function refreshBadge(source: CatalogSource): { label: string; tone: Tone } {
  if (source.lastRefreshSucceeded === null) return { label: 'Not refreshed yet', tone: 'neutral' }
  return source.lastRefreshSucceeded
    ? { label: 'Refreshed', tone: 'success' }
    : { label: 'Refresh failed', tone: 'danger' }
}

/**
 * The administrator's catalog sources: URLs that publish indexer entries which then appear under
 * "Catalog" in Add indexer. Cinomni ships with none, and nothing here suggests one.
 */
export function CatalogSourcesSection() {
  const [adding, setAdding] = useState(false)
  const [added, setAdded] = useState<CatalogSource | null>(null)

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: catalogSourcesQueryKey,
    queryFn: discoveryApi.catalogSources,
  })

  function openAdd() {
    setAdded(null)
    setAdding(true)
  }

  return (
    <section aria-labelledby="catalog-sources-heading" className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 id="catalog-sources-heading" className="text-lg font-semibold tracking-tight text-fg">
            Catalog sources
          </h2>
          <p className="mt-1 text-sm text-muted">{CATALOG_SOURCE_EXPLANATION}</p>
        </div>
        <Button size="sm" variant="subtle" icon={<PlusIcon className="size-4" />} onClick={openAdd}>
          Add catalog source
        </Button>
      </div>

      {added && <AddedNotice source={added} onDismiss={() => setAdded(null)} />}

      {isError ? (
        <ErrorState message={errorMessage(error, 'Could not load catalog sources.')} onRetry={() => void refetch()} />
      ) : isPending ? (
        <div role="status" aria-label="Loading catalog sources" className="space-y-3">
          {Array.from({ length: 2 }, (_, index) => (
            <Skeleton key={index} className="h-24" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <EmptyState
          title="No catalog sources"
          description={`${CATALOG_SOURCE_EXPLANATION} Add one to install indexers from it, or use Manual setup in Add indexer to configure an indexer yourself.`}
          action={
            <Button size="sm" icon={<PlusIcon className="size-4" />} onClick={openAdd}>
              Add catalog source
            </Button>
          }
        />
      ) : (
        <ul aria-label="Catalog sources" className="space-y-3">
          {data.map((source) => (
            <CatalogSourceRow key={source.id} source={source} />
          ))}
        </ul>
      )}

      <AddCatalogSourceModal
        open={adding}
        onClose={() => setAdding(false)}
        onAdded={(source) => {
          setAdding(false)
          setAdded(source)
        }}
      />
    </section>
  )
}

/**
 * The result of adding a source. Adding succeeds even when the first refresh does not — the source
 * is kept so it can be fixed and refreshed — so a failed refresh is reported, not treated as an error.
 */
function AddedNotice({ source, onDismiss }: { source: CatalogSource; onDismiss: () => void }) {
  const failed = source.lastRefreshSucceeded === false
  return (
    <Alert
      tone={failed ? 'warning' : 'success'}
      title={failed ? `${source.name} was added, but its first refresh failed` : `${source.name} was added`}
    >
      <p>
        {failed
          ? source.lastRefreshMessage ?? source.lastRefreshCode ?? 'No details were reported.'
          : `It publishes ${pluralIndexers(source.entryCount)}. Install them from Add indexer.`}
      </p>
      <Button type="button" variant="ghost" size="sm" className="mt-1 -ml-3" onClick={onDismiss}>
        Dismiss
      </Button>
    </Alert>
  )
}

function CatalogSourceRow({ source }: { source: CatalogSource }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const [renaming, setRenaming] = useState(false)
  const [removing, setRemoving] = useState(false)
  const status = refreshBadge(source)

  const update = useMutation({
    mutationFn: (body: UpdateCatalogSourceRequest) => discoveryApi.updateCatalogSource(source.id, body),
    onMutate: () => setError(null),
    onSuccess: () => invalidateCatalogSources(queryClient),
    onError: (err) => setError(errorMessage(err, 'Could not change whether this source is used.')),
  })

  const refresh = useMutation({
    mutationFn: () => discoveryApi.refreshCatalogSource(source.id),
    onMutate: () => setError(null),
    onSuccess: () => invalidateCatalogSources(queryClient),
    onError: (err) => setError(errorMessage(err, 'Could not refresh this source.')),
  })

  const busy = update.isPending || refresh.isPending

  return (
    <Card as="li" className={cn('flex flex-col gap-3', !source.enabled && 'opacity-70')}>
      <div className="flex items-start gap-3">
        <div className="pt-0.5">
          <Switch
            checked={source.enabled}
            disabled={busy}
            label={`Use ${source.name}`}
            onChange={(enabled) => update.mutate({ name: source.name, enabled })}
          />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="truncate text-card font-semibold text-fg">{source.name}</span>
            <Badge tone="neutral">{pluralIndexers(source.entryCount)}</Badge>
            {!source.enabled && <span className="text-meta text-faint">Not used</span>}
          </div>
          <p className="mt-0.5 truncate font-mono text-xs text-faint" title={source.url}>
            {source.url}
          </p>
        </div>
        <div className="flex shrink-0 items-start gap-2">
          <Button
            type="button"
            variant="subtle"
            size="sm"
            aria-label={`Refresh ${source.name}`}
            loading={refresh.isPending}
            disabled={busy}
            onClick={() => refresh.mutate()}
          >
            Refresh
          </Button>
          <Menu
            label={`Source actions for ${source.name}`}
            size="icon-sm"
            items={[
              { label: 'Edit name', onSelect: () => setRenaming(true) },
              {
                label: 'Remove',
                danger: true,
                icon: <TrashIcon className="size-4" />,
                onSelect: () => setRemoving(true),
              },
            ]}
          />
        </div>
      </div>

      <div className="flex flex-col gap-1 border-t border-line-soft pt-3 text-meta">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-faint">Last refresh</span>
          <Badge tone={status.tone}>{status.label}</Badge>
          {source.lastRefreshedAt && <span className="text-muted">{formatDateTime(source.lastRefreshedAt)}</span>}
        </div>
        {source.lastRefreshSucceeded === false && (
          <p className="text-muted">
            {source.lastRefreshMessage ?? 'No details were reported.'}
            {source.lastRefreshCode && <span className="ml-2 font-mono text-xs text-faint">{source.lastRefreshCode}</span>}
          </p>
        )}
        {error && (
          <p role="alert" className="text-xs text-danger">
            {error}
          </p>
        )}
      </div>

      {renaming && <RenameCatalogSourceModal source={source} onClose={() => setRenaming(false)} />}
      <RemoveCatalogSourceModal source={source} open={removing} onClose={() => setRemoving(false)} />
    </Card>
  )
}

function AddCatalogSourceModal({
  open,
  onClose,
  onAdded,
}: {
  open: boolean
  onClose: () => void
  onAdded: (source: CatalogSource) => void
}) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [urlError, setUrlError] = useState<string | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const add = useMutation({
    mutationFn: () => discoveryApi.addCatalogSource({ name: name.trim(), url: url.trim() }),
    onMutate: () => setSubmitError(null),
    onSuccess: async (source) => {
      await invalidateCatalogSources(queryClient)
      setName('')
      setUrl('')
      onAdded(source)
    },
    onError: (err) => setSubmitError(errorMessage(err, 'Could not add the catalog source.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (add.isPending) return
    if (!isWellFormedUrl(url)) {
      setUrlError('Enter a full address, starting with https://.')
      return
    }
    setUrlError(null)
    add.mutate()
  }

  return (
    <Modal open={open} onClose={onClose} title="Add catalog source">
      <form onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
        <p className="text-sm text-muted">
          {CATALOG_SOURCE_EXPLANATION} Cinomni fetches it now and on each refresh; its indexers then appear
          under Catalog in Add indexer.
        </p>
        <TextField
          id="catalog-source-name"
          label="Name"
          required
          maxLength={200}
          value={name}
          onChange={(event) => setName(event.target.value)}
          autoFocus
        />
        <div>
          <TextField
            id="catalog-source-url"
            label="URL"
            type="url"
            inputMode="url"
            required
            placeholder="https://catalog.example/indexers.json"
            value={url}
            onChange={(event) => {
              setUrl(event.target.value)
              setUrlError(null)
            }}
            aria-invalid={urlError ? true : undefined}
            aria-describedby={urlError ? 'catalog-source-url-error' : undefined}
          />
          {urlError && (
            <p id="catalog-source-url-error" role="alert" className="mt-1 text-xs text-danger">
              {urlError}
            </p>
          )}
        </div>

        {submitError && <Alert tone="danger">{submitError}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" disabled={add.isPending} onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={add.isPending} disabled={add.isPending || name.trim() === ''}>
            Add source
          </Button>
        </div>
      </form>
    </Modal>
  )
}

function RenameCatalogSourceModal({ source, onClose }: { source: CatalogSource; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(source.name)
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () => discoveryApi.updateCatalogSource(source.id, { name: name.trim(), enabled: source.enabled }),
    onMutate: () => setError(null),
    onSuccess: async () => {
      await invalidateCatalogSources(queryClient)
      onClose()
    },
    onError: (err) => setError(errorMessage(err, 'Could not rename the catalog source.')),
  })

  const trimmed = name.trim()
  return (
    <Modal open onClose={onClose} title={`Rename ${source.name}`}>
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault()
          if (!save.isPending && trimmed !== '') save.mutate()
        }}
      >
        <TextField
          id="catalog-source-rename"
          label="Name"
          required
          maxLength={200}
          value={name}
          onChange={(event) => setName(event.target.value)}
          autoFocus
        />
        {error && <Alert tone="danger">{error}</Alert>}
        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" disabled={save.isPending} onClick={onClose}>
            Cancel
          </Button>
          <Button
            type="submit"
            loading={save.isPending}
            disabled={save.isPending || trimmed === '' || trimmed === source.name}
          >
            Save name
          </Button>
        </div>
      </form>
    </Modal>
  )
}

function RemoveCatalogSourceModal({
  source,
  open,
  onClose,
}: {
  source: CatalogSource
  open: boolean
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)

  const remove = useMutation({
    mutationFn: () => discoveryApi.removeCatalogSource(source.id),
    onMutate: () => setError(null),
    onSuccess: async () => {
      await invalidateCatalogSources(queryClient)
      onClose()
    },
    onError: (err) => setError(errorMessage(err, 'Could not remove the catalog source.')),
  })

  return (
    <Modal open={open} onClose={onClose} title={`Remove ${source.name}?`}>
      <div className="flex flex-col gap-4">
        <p className="text-sm text-muted">
          Its entries leave the catalog. Indexers already installed from{' '}
          <span className="font-medium text-fg">{source.name}</span> are kept and keep working.
        </p>
        {error && <Alert tone="danger">{error}</Alert>}
        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose} disabled={remove.isPending}>
            Cancel
          </Button>
          <Button
            type="button"
            variant="danger"
            disabled={remove.isPending}
            loading={remove.isPending}
            onClick={() => remove.mutate()}
          >
            Remove source
          </Button>
        </div>
      </div>
    </Modal>
  )
}
