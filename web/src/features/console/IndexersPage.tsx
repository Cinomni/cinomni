import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { discoveryApi } from '@/api/endpoints'
import type {
  IndexerCredentialState,
  IndexerProtocol,
  IndexerSummary,
  SetIndexerCapabilitiesRequest,
} from '@/api/types'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatDateTime } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Checkbox } from '@/ui/Checkbox'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { Menu } from '@/ui/Menu'
import { Modal } from '@/ui/Modal'
import { Skeleton } from '@/ui/Skeleton'
import { PlusIcon, TrashIcon } from '@/ui/icons'
import { Switch } from '@/ui/Switch'
import { Select } from '@/ui/Select'
import { Segmented } from '@/ui/Segmented'
import { TextField } from '@/ui/TextField'
import {
  draftFor,
  IndexerCredentialFields,
  toCredentialRequest,
  type IndexerCredentialDraft,
} from './IndexerCredentialFields'
import { IndexerDefinitionsSection, indexerDefinitionsQueryKey } from './IndexerDefinitionsSection'
import { CatalogSourcesSection } from './CatalogSourcesSection'
import { indexerCatalogQueryKey } from './indexerCatalogQueries'
import { IndexerSettingsModal, NativeIndexerCatalog } from './NativeIndexerCatalog'

/** A successfully parsed comma-separated list, or a message naming what was wrong with it. */
type ParseResult<T> = { ok: true; value: T } | { ok: false; error: string }

/**
 * Splits a comma-separated field into whole numbers. Blank entries (an empty field, a trailing
 * comma) are dropped rather than treated as zero; anything else that is not a whole number is
 * reported as a field error instead of being coerced into `NaN` and sent to the server.
 */
function parseIntegerList(raw: string, fieldLabel: string): ParseResult<number[]> {
  const tokens = raw
    .split(',')
    .map((token) => token.trim())
    .filter((token) => token.length > 0)

  const value: number[] = []
  for (const token of tokens) {
    if (!/^-?\d+$/.test(token)) {
      return { ok: false, error: `${fieldLabel} must be a comma-separated list of whole numbers.` }
    }
    value.push(Number(token))
  }
  return { ok: true, value }
}

/** Splits a comma-separated field into trimmed, non-empty strings. */
function parseStringList(raw: string): string[] {
  return raw
    .split(',')
    .map((token) => token.trim())
    .filter((token) => token.length > 0)
}

/**
 * How each credential state reads in the table. The label carries the distinction on its own, so the
 * tone is reinforcement and never the only signal: a stored secret this installation cannot decrypt
 * is not "configured", because nothing it is stored for actually happens.
 */
const CREDENTIAL_BADGE: Record<IndexerCredentialState, { label: string; tone: Tone }> = {
  None: { label: 'None', tone: 'neutral' },
  Readable: { label: 'Configured', tone: 'success' },
  MasterKeyMissing: { label: 'Unreadable — no master key', tone: 'warning' },
  MasterKeyChanged: { label: 'Unreadable — key changed', tone: 'warning' },
  Corrupt: { label: 'Unreadable — corrupt', tone: 'warning' },
}

/**
 * Why a stored credential cannot be read here. The three failure classes are different deployment
 * facts with different remedies, so they are not flattened into one "error"; `null` marks the two
 * states that are not failures at all. The backend classifies — this only phrases what it reported.
 */
const CREDENTIAL_REASON: Record<IndexerCredentialState, string | null> = {
  None: null,
  Readable: null,
  MasterKeyMissing: 'no master key is loaded on this installation, so nothing stored encrypted can be decrypted here.',
  MasterKeyChanged:
    'it was stored under a different master key — the key was rotated, or this database was restored somewhere that never had it.',
  Corrupt:
    'it fails authentication under the master key this installation holds: the stored row is damaged, or it belongs to another indexer.',
}

/**
 * How the session column reads for one indexer. "Signed in" is what exists, not a promise: the site
 * can expire the session at any time and the next search signs in again — which is why a failed
 * sign-in is not an error state to fix here, but a fact the next search will act on.
 *
 * This takes the indexer rather than the state alone because `sessionState` cannot phrase `None` by
 * itself: it is what an indexer whose definition declares no login reports, AND what one that
 * declares a login but has no usable credential reports. Those are opposite statements to an
 * operator — "nothing to configure here" against "configure something here" — and `declaresLogin` is
 * the field that separates them. Calling the second one "No login" would send the operator away from
 * the single action that fixes it.
 */
function sessionBadge(indexer: IndexerSummary): { label: string; tone: Tone } {
  switch (indexer.sessionState) {
    case 'Active':
      return { label: 'Signed in', tone: 'success' }
    case 'NotLoggedIn':
      return { label: 'Signed out', tone: 'info' }
    case 'Failed':
      return { label: 'Sign-in failed', tone: 'warning' }
    case 'None':
      return indexer.declaresLogin
        ? { label: 'No usable credential', tone: 'warning' }
        : { label: 'No login', tone: 'neutral' }
  }
}

/** A stored credential exists in every state but `None`, so replacing and removing stay available. */
function hasStoredCredential(indexer: IndexerSummary): boolean {
  return indexer.credentialState !== 'None'
}

/**
 * The console's indexer surface: what discovery searches against, plus enable, priority and delete.
 * A disabled indexer is not searched. Delete removes the credential and session; a definition stays.
 */
export function IndexersPage() {
  const [adding, setAdding] = useState(false)
  const [editingIndexer, setEditingIndexer] = useState<IndexerSummary | null>(null)
  const [credentialFor, setCredentialFor] = useState<IndexerSummary | null>(null)
  const [settingsFor, setSettingsFor] = useState<IndexerSummary | null>(null)

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['indexers'],
    queryFn: discoveryApi.indexers,
  })

  // A credential that is stored but undecryptable is the one case an operator cannot see coming: the
  // row looks configured, and the search it was meant to authenticate has already stopped doing so.
  const unreadableCredentials = (data ?? []).flatMap((indexer) => {
    const reason = CREDENTIAL_REASON[indexer.credentialState]
    return reason === null ? [] : [{ id: indexer.id, name: indexer.name, reason }]
  })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold tracking-tight text-fg">Indexers</h2>
          <p className="mt-1 text-sm text-muted">Where discovery searches for releases.</p>
        </div>
        <Button size="sm" icon={<PlusIcon className="size-4" />} onClick={() => setAdding(true)}>
          Add indexer
        </Button>
      </div>

      {unreadableCredentials.length > 0 && (
        <Alert
          tone="warning"
          title={
            unreadableCredentials.length === 1
              ? 'A stored credential cannot be read on this installation'
              : `${unreadableCredentials.length} stored credentials cannot be read on this installation`
          }
        >
          <p>
            These indexers are already queried unauthenticated: the site sees an anonymous request rather than
            this account, so anything it shows only to that account is missing from every search. The stored
            secret cannot be recovered from here — entering it again is what restores authentication.
          </p>
          <ul className="mt-1.5 list-disc space-y-1 pl-5">
            {unreadableCredentials.map((indexer) => (
              <li key={indexer.id}>
                <span className="font-medium">{indexer.name}</span> — {indexer.reason}
              </li>
            ))}
          </ul>
        </Alert>
      )}

      {isError ? (
        <ErrorState message={errorMessage(error, 'Could not load indexers.')} onRetry={() => void refetch()} />
      ) : isPending ? (
        <div role="status" aria-label="Loading indexers" className="space-y-3">
          {Array.from({ length: 3 }, (_, index) => (
            <Skeleton key={index} className="h-24" />
          ))}
        </div>
      ) : data.length === 0 ? (
        <EmptyState
          title="No indexers configured"
          description="Add a Torznab or Newznab indexer so monitored titles can find releases."
        />
      ) : (
        <ul aria-label="Configured indexers" className="space-y-3">
          {data.map((indexer) => (
            <IndexerRow
              key={indexer.id}
              indexer={indexer}
              onSettings={() => setSettingsFor(indexer)}
              onCredential={() => setCredentialFor(indexer)}
              onCapabilities={() => setEditingIndexer(indexer)}
            />
          ))}
        </ul>
      )}

      <AddIndexerModal open={adding} onClose={() => setAdding(false)} />
      {editingIndexer && (
        <CapabilitiesModal indexer={editingIndexer} onClose={() => setEditingIndexer(null)} />
      )}
      {credentialFor && (
        <CredentialModal indexer={credentialFor} onClose={() => setCredentialFor(null)} />
      )}
      {settingsFor && <IndexerSettingsModal indexer={settingsFor} onClose={() => setSettingsFor(null)} />}

      <CatalogSourcesSection />

      <IndexerDefinitionsSection />
    </div>
  )
}

/**
 * Write-only, like the settings store's secret rows: the current secret is never fetched and never
 * shown, so the field starts empty and the only offered action is replacing it. A `Definition`
 * indexer signs in with a username and password; Torznab/Newznab take an API key or, behind an
 * authenticating proxy, a username and password. A stored username means the latter.
 */
function CredentialModal({ indexer, onClose }: { indexer: IndexerSummary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<IndexerCredentialDraft>(() =>
    draftFor(indexer.protocol, false, {
      kind: indexer.credentialUsername ? 'login' : 'apiKey',
      username: indexer.credentialUsername ?? '',
      secret: '',
    }),
  )
  const [error, setError] = useState<string | null>(null)
  const unreadableReason = CREDENTIAL_REASON[indexer.credentialState]

  const save = useMutation({
    mutationFn: () => {
      const body = toCredentialRequest(draft)
      // Unreachable: this modal never offers 'none' — removing is its own button.
      if (!body) return Promise.resolve()
      return discoveryApi.setCredential(indexer.id, body)
    },
    onMutate: () => setError(null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
      onClose()
    },
    onError: (err) => setError(errorMessage(err, 'Could not store the credential.')),
  })

  const clear = useMutation({
    mutationFn: () => discoveryApi.clearCredential(indexer.id),
    onMutate: () => setError(null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
      onClose()
    },
    onError: (err) => setError(errorMessage(err, 'Could not remove the credential.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    save.mutate()
  }

  return (
    <Modal open onClose={onClose} title={`Credential for “${indexer.name}”`}>
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        {unreadableReason && (
          <Alert tone="warning">
            <p>This indexer&rsquo;s stored secret cannot be read on this installation — {unreadableReason}</p>
            <p className="mt-1">
              Every search against it already runs unauthenticated. Entering the secret again restores that;
              nothing here can recover the stored one.
            </p>
          </Alert>
        )}

        <p className="text-sm text-muted">
          The stored secret is never shown here. Enter a new one to replace it, or cancel to leave it
          as it is.
        </p>
        <p className="text-sm text-muted">
          Saving a credential resets the session; the next search signs in with it. Removing the
          credential drops any kept session with it.
        </p>

        <IndexerCredentialFields
          protocol={indexer.protocol}
          draft={draft}
          onChange={setDraft}
          allowNone={false}
          autoFocus
        />

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-between gap-2">
          {hasStoredCredential(indexer) ? (
            <Button
              type="button"
              variant="ghost"
              loading={clear.isPending}
              onClick={() => clear.mutate()}
            >
              Remove
            </Button>
          ) : (
            <span />
          )}
          <div className="flex gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending}>
              Save
            </Button>
          </div>
        </div>
      </form>
    </Modal>
  )
}

function IndexerEnabled({ indexer }: { indexer: IndexerSummary }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const toggle = useMutation({
    mutationFn: (enabled: boolean) => discoveryApi.setIndexerEnabled(indexer.id, enabled),
    onMutate: () => setError(null),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['indexers'] }),
    onError: (err) => setError(errorMessage(err, 'Could not change whether this indexer is searched.')),
  })

  return (
    <div className="flex flex-col gap-1">
      <Switch
        checked={indexer.enabled}
        disabled={toggle.isPending}
        label={`Enable ${indexer.name}`}
        onChange={(enabled) => toggle.mutate(enabled)}
      />
      {error && (
        <p role="alert" className="max-w-40 text-xs text-danger">
          {error}
        </p>
      )}
    </div>
  )
}

function IndexerPriority({ indexer }: { indexer: IndexerSummary }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (priority: number) => discoveryApi.setIndexerPriority(indexer.id, priority),
    onMutate: () => setError(null),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['indexers'] }),
    onError: (err) => setError(errorMessage(err, 'Could not change the priority.')),
  })

  function commit(raw: string) {
    if (!/^\d+$/.test(raw)) {
      setError('Priority must be a whole number from 1 to 50.')
      return
    }

    const priority = Number(raw)
    if (priority < 1 || priority > 50) {
      setError('Priority must be a whole number from 1 to 50.')
      return
    }

    setError(null)
    if (priority !== indexer.priority) {
      save.mutate(priority)
    }
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <div className="flex items-center gap-2">
        <span aria-hidden="true" className="hidden text-meta text-faint sm:inline">
          Priority
        </span>
        <input
          key={`${indexer.id}-${indexer.priority}`}
          type="number"
          inputMode="numeric"
          min={1}
          max={50}
          defaultValue={indexer.priority}
          aria-label={`Priority for ${indexer.name}`}
          aria-invalid={error ? true : undefined}
          disabled={save.isPending}
          onBlur={(event) => commit(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault()
              commit(event.currentTarget.value)
            }
          }}
          className="h-8 w-16 rounded-md border border-line bg-elevated px-2 text-end text-sm text-fg focus:border-accent focus:outline-none disabled:opacity-50"
        />
      </div>
      {error && (
        <p role="alert" className="max-w-40 text-end text-xs text-danger">
          {error}
        </p>
      )}
    </div>
  )
}

interface IndexerRowProps {
  indexer: IndexerSummary
  onSettings: () => void
  onCredential: () => void
  onCapabilities: () => void
}

/**
 * One indexer as a panel row: identity and address on top, what authenticates it underneath, and the
 * two controls an operator touches often — enabled and priority — kept inline. Everything else sits
 * behind the actions menu so a row never becomes a strip of equal-weight buttons.
 */
function IndexerRow({ indexer, onSettings, onCredential, onCapabilities }: IndexerRowProps) {
  const credential = CREDENTIAL_BADGE[indexer.credentialState]
  const session = sessionBadge(indexer)

  return (
    <Card as="li" className={cn('flex flex-col gap-3', !indexer.enabled && 'opacity-70')}>
      <div className="flex items-start gap-3">
        <div className="pt-0.5">
          <IndexerEnabled indexer={indexer} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="truncate text-card font-semibold text-fg">{indexer.name}</span>
            <Badge tone="neutral">{indexer.protocol}</Badge>
            {!indexer.enabled && <span className="text-meta text-faint">Not searched</span>}
          </div>
          <p className="mt-0.5 truncate font-mono text-xs text-faint" title={indexer.baseUrl}>
            {indexer.baseUrl}
          </p>
        </div>
        <div className="flex shrink-0 items-start gap-2">
          <IndexerPriority indexer={indexer} />
          <IndexerActions
            indexer={indexer}
            onSettings={onSettings}
            onCredential={onCredential}
            onCapabilities={onCapabilities}
          />
        </div>
      </div>

      <dl className="flex flex-wrap gap-x-6 gap-y-2 border-t border-line-soft pt-3 text-meta">
        <div className="flex min-w-0 items-center gap-2">
          <dt className="text-faint">Credential</dt>
          <dd className="flex min-w-0 items-center gap-2">
            <Badge tone={credential.tone}>{credential.label}</Badge>
            {indexer.credentialUsername && <span className="truncate text-muted">{indexer.credentialUsername}</span>}
          </dd>
        </div>
        <div className="flex min-w-0 items-center gap-2">
          <dt className="text-faint">Session</dt>
          <dd className="flex min-w-0 items-center gap-2">
            <Badge tone={session.tone}>{session.label}</Badge>
            {indexer.lastLoginAt && (
              <span className="truncate text-muted">{formatDateTime(indexer.lastLoginAt)}</span>
            )}
          </dd>
        </div>
      </dl>
    </Card>
  )
}

function IndexerActions({ indexer, onSettings, onCredential, onCapabilities }: IndexerRowProps) {
  const queryClient = useQueryClient()
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const remove = useMutation({
    mutationFn: () => discoveryApi.deleteIndexer(indexer.id),
    onMutate: () => setDeleteError(null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
      await queryClient.invalidateQueries({ queryKey: indexerCatalogQueryKey })
      setConfirmingDelete(false)
    },
    onError: (err) => setDeleteError(errorMessage(err, 'Could not delete the indexer.')),
  })

  return (
    <>
      <Menu
        label={`Actions for ${indexer.name}`}
        size="icon-sm"
        items={[
          { label: 'Settings', onSelect: onSettings },
          { label: hasStoredCredential(indexer) ? 'Replace credential' : 'Add credential', onSelect: onCredential },
          { label: 'Edit capabilities', onSelect: onCapabilities },
          {
            label: 'Delete',
            danger: true,
            icon: <TrashIcon className="size-4" />,
            onSelect: () => setConfirmingDelete(true),
          },
        ]}
      />
      <Modal
        open={confirmingDelete}
        onClose={() => setConfirmingDelete(false)}
        title={`Delete ${indexer.name}?`}
      >
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            Searches will stop asking <span className="font-medium text-fg">{indexer.name}</span>. Its stored
            credential and sign-in session are removed. A definition document, if this indexer has one, is kept.
          </p>
          {deleteError && <Alert tone="danger">{deleteError}</Alert>}
          <div className="mt-1 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => setConfirmingDelete(false)} disabled={remove.isPending}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="danger"
              disabled={remove.isPending}
              loading={remove.isPending}
              onClick={() => remove.mutate()}
            >
              Delete indexer
            </Button>
          </div>
        </div>
      </Modal>
    </>
  )
}

function AddIndexerModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [mode, setMode] = useState('catalog')
  const [name, setName] = useState('')
  const [baseUrl, setBaseUrl] = useState('')
  const [protocol, setProtocol] = useState<IndexerProtocol>('Torznab')
  const [definitionId, setDefinitionId] = useState('')
  const [priority, setPriority] = useState('25')
  const [credential, setCredential] = useState<IndexerCredentialDraft>(() => draftFor('Torznab', true))
  const [definitionError, setDefinitionError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Only fetched for its own Select below; IndexerDefinitionsSection shares the same query key so
  // this never issues a second request once that section has already loaded the list.
  const { data: definitions } = useQuery({
    queryKey: indexerDefinitionsQueryKey,
    queryFn: discoveryApi.definitions,
    enabled: protocol === 'Definition',
  })

  const add = useMutation({
    mutationFn: () =>
      discoveryApi.addIndexer({
        name,
        baseUrl,
        protocol,
        priority: Number(priority) || 0,
        definitionId: protocol === 'Definition' ? definitionId : undefined,
        // One request, so an indexer that needs a credential is never created without it.
        credential: toCredentialRequest(credential),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
      onClose()
      setName('')
      setBaseUrl('')
      setDefinitionId('')
      setCredential(draftFor(protocol, true))
    },
    onError: (err) => setError(errorMessage(err, 'Could not add the indexer.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)

    if (protocol === 'Definition' && !definitionId) {
      setDefinitionError('Select the definition this indexer runs on.')
      return
    }
    setDefinitionError(null)
    add.mutate()
  }

  return (
    <Modal open={open} onClose={onClose} title="Add indexer" className="max-w-2xl">
      <div className="mb-4">
        <Segmented
          label="Setup method"
          options={[{ value: 'catalog', label: 'Catalog' }, { value: 'manual', label: 'Manual' }]}
          value={mode}
          onChange={setMode}
        />
      </div>
      {open && mode === 'catalog' ? (
        <NativeIndexerCatalog enabled onInstalled={onClose} onManual={() => setMode('manual')} />
      ) : (
        <form onSubmit={onSubmit} className="flex flex-col gap-4">
          <TextField
            label="Name"
            required
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoFocus
          />
          <TextField
            label="Base URL"
            type="url"
            required
            placeholder="https://indexer.example/api"
            value={baseUrl}
            onChange={(e) => setBaseUrl(e.target.value)}
          />
          <div className="grid grid-cols-2 gap-3">
            <Select
              label="Protocol"
              value={protocol}
              onChange={(e) => {
                const next = e.target.value as IndexerProtocol
                setProtocol(next)
                setCredential((current) => draftFor(next, true, current))
              }}
            >
              <option value="Torznab">Torznab</option>
              <option value="Newznab">Newznab</option>
              <option value="Definition">Definition</option>
            </Select>
            <TextField
              label="Priority"
              inputMode="numeric"
              value={priority}
              onChange={(e) => setPriority(e.target.value.replace(/\D/g, '').slice(0, 3))}
            />
          </div>

          {protocol === 'Definition' && (
            <Select
              label="Definition"
              value={definitionId}
              onChange={(e) => setDefinitionId(e.target.value)}
              error={definitionError ?? undefined}
              hint={definitions?.length === 0 ? 'Upload a definition below before adding this indexer.' : undefined}
            >
              <option value="">Select a definition…</option>
              {definitions?.map((definition) => (
                <option key={definition.id} value={definition.id}>
                  {definition.name}
                </option>
              ))}
            </Select>
          )}

          <IndexerCredentialFields protocol={protocol} draft={credential} onChange={setCredential} allowNone />

          {error && <Alert tone="danger">{error}</Alert>}

          <div className="mt-1 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={add.isPending}>
              Add indexer
            </Button>
          </div>
        </form>
      )}
    </Modal>
  )
}

interface CapabilitiesFieldErrors {
  movieCategories?: string
  tvCategories?: string
}

/**
 * The capability editor: what an indexer says it can answer.
 *
 * `SetIndexerCapabilitiesRequest` fields are all optional on the wire, and that optionality is a
 * trap: the endpoint builds a whole capabilities record from the request, so an omitted field is
 * NOT left alone — it is overwritten with the record's own default (both search flags true, every
 * list empty). A partial patch would therefore wipe the categories an operator configured earlier.
 * Every submit sends the complete shape this form holds.
 */
function CapabilitiesModal({ indexer, onClose }: { indexer: IndexerSummary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [supportsMovieSearch, setSupportsMovieSearch] = useState(indexer.capabilities.supportsMovieSearch)
  const [supportsTvSearch, setSupportsTvSearch] = useState(indexer.capabilities.supportsTvSearch)
  const [movieCategories, setMovieCategories] = useState(indexer.capabilities.movieCategories.join(', '))
  const [tvCategories, setTvCategories] = useState(indexer.capabilities.tvCategories.join(', '))
  const [movieSearchParams, setMovieSearchParams] = useState(indexer.capabilities.movieSearchParams.join(', '))
  const [tvSearchParams, setTvSearchParams] = useState(indexer.capabilities.tvSearchParams.join(', '))
  const [fieldErrors, setFieldErrors] = useState<CapabilitiesFieldErrors>({})
  const [submitError, setSubmitError] = useState<string | null>(null)

  const setCapabilities = useMutation({
    mutationFn: (body: SetIndexerCapabilitiesRequest) => discoveryApi.setCapabilities(indexer.id, body),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
      onClose()
    },
    onError: (err) => setSubmitError(errorMessage(err, 'Could not save capabilities.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setSubmitError(null)

    const movieCategoriesResult = parseIntegerList(movieCategories, 'Movie categories')
    const tvCategoriesResult = parseIntegerList(tvCategories, 'TV categories')
    const errors: CapabilitiesFieldErrors = {}
    if (!movieCategoriesResult.ok) errors.movieCategories = movieCategoriesResult.error
    if (!tvCategoriesResult.ok) errors.tvCategories = tvCategoriesResult.error
    setFieldErrors(errors)
    if (!movieCategoriesResult.ok || !tvCategoriesResult.ok) return

    // The whole shape, always: an omitted field is overwritten with the backend record's default,
    // not preserved, so a partial patch would quietly clear capabilities nobody asked to clear.
    setCapabilities.mutate({
      supportsMovieSearch,
      supportsTvSearch,
      movieCategories: movieCategoriesResult.value,
      tvCategories: tvCategoriesResult.value,
      movieSearchParams: parseStringList(movieSearchParams),
      tvSearchParams: parseStringList(tvSearchParams),
    })
  }

  return (
    <Modal open onClose={onClose} title={`Capabilities — ${indexer.name}`}>
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Checkbox
            label="Supports movie search"
            checked={supportsMovieSearch}
            onChange={(e) => setSupportsMovieSearch(e.target.checked)}
          />
          <Checkbox
            label="Supports TV search"
            checked={supportsTvSearch}
            onChange={(e) => setSupportsTvSearch(e.target.checked)}
          />
        </div>

        <div>
          <TextField
            id="capabilities-movie-categories"
            label="Movie categories"
            placeholder="2000, 2010, 2020"
            value={movieCategories}
            onChange={(e) => setMovieCategories(e.target.value)}
            aria-invalid={fieldErrors.movieCategories ? true : undefined}
            aria-describedby={fieldErrors.movieCategories ? 'capabilities-movie-categories-error' : undefined}
          />
          {fieldErrors.movieCategories ? (
            <p id="capabilities-movie-categories-error" role="alert" className="mt-1 text-xs text-danger">
              {fieldErrors.movieCategories}
            </p>
          ) : (
            <p className="mt-1 text-xs text-faint">Comma-separated Torznab/Newznab category numbers.</p>
          )}
        </div>

        <div>
          <TextField
            id="capabilities-tv-categories"
            label="TV categories"
            placeholder="5000, 5030, 5040"
            value={tvCategories}
            onChange={(e) => setTvCategories(e.target.value)}
            aria-invalid={fieldErrors.tvCategories ? true : undefined}
            aria-describedby={fieldErrors.tvCategories ? 'capabilities-tv-categories-error' : undefined}
          />
          {fieldErrors.tvCategories ? (
            <p id="capabilities-tv-categories-error" role="alert" className="mt-1 text-xs text-danger">
              {fieldErrors.tvCategories}
            </p>
          ) : (
            <p className="mt-1 text-xs text-faint">Comma-separated Torznab/Newznab category numbers.</p>
          )}
        </div>

        <TextField
          id="capabilities-movie-search-params"
          label="Movie search params"
          placeholder="imdbid, tmdbid"
          hint="Comma-separated parameter names this indexer accepts for a movie search."
          value={movieSearchParams}
          onChange={(e) => setMovieSearchParams(e.target.value)}
        />

        <TextField
          id="capabilities-tv-search-params"
          label="TV search params"
          placeholder="tvdbid, season, ep"
          hint="Comma-separated parameter names this indexer accepts for a TV search."
          value={tvSearchParams}
          onChange={(e) => setTvSearchParams(e.target.value)}
        />

        {submitError && <Alert tone="danger">{submitError}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={setCapabilities.isPending}>
            Save capabilities
          </Button>
        </div>
      </form>
    </Modal>
  )
}
