import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { discoveryApi } from '@/api/endpoints'
import type {
  IndexerCatalogDraftRequest,
  IndexerCatalogEntry,
  IndexerSettings,
  IndexerSummary,
  IndexerTestResult,
} from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Checkbox } from '@/ui/Checkbox'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'
import { TextField } from '@/ui/TextField'
import { CATALOG_SOURCE_EXPLANATION } from './CatalogSourcesSection'
import { catalogSourcesQueryKey, indexerCatalogQueryKey } from './indexerCatalogQueries'

interface SettingsDraft {
  minimumSeeders: string
  preferMagnet: boolean
  queryLimit: string
  grabLimit: string
  useFlareSolverr: boolean
}

interface SettingsErrors {
  minimumSeeders?: string
  queryLimit?: string
  grabLimit?: string
}

function settingsDraft(settings: IndexerSettings): SettingsDraft {
  return {
    minimumSeeders: settings.minimumSeeders === null ? '' : String(settings.minimumSeeders),
    preferMagnet: settings.preferMagnet,
    queryLimit: settings.queryLimit === null ? '' : String(settings.queryLimit),
    grabLimit: settings.grabLimit === null ? '' : String(settings.grabLimit),
    useFlareSolverr: settings.useFlareSolverr,
  }
}

function parseOptionalInteger(raw: string, label: string, minimum: number): number | null | string {
  if (raw.trim() === '') return null
  if (!/^\d+$/.test(raw) || Number(raw) < minimum) {
    return `${label} must be ${minimum === 0 ? 'a nonnegative' : 'a positive'} whole number, or blank.`
  }
  return Number(raw)
}

function parseSettings(draft: SettingsDraft): { settings?: IndexerSettings; errors: SettingsErrors } {
  const minimumSeeders = parseOptionalInteger(draft.minimumSeeders, 'Minimum seeders', 0)
  const queryLimit = parseOptionalInteger(draft.queryLimit, 'Query limit', 1)
  const grabLimit = parseOptionalInteger(draft.grabLimit, 'Grab limit', 1)
  const errors: SettingsErrors = {}
  if (typeof minimumSeeders === 'string') errors.minimumSeeders = minimumSeeders
  if (typeof queryLimit === 'string') errors.queryLimit = queryLimit
  if (typeof grabLimit === 'string') errors.grabLimit = grabLimit

  if (typeof minimumSeeders === 'string' || typeof queryLimit === 'string' || typeof grabLimit === 'string') {
    return { errors }
  }
  return {
    errors,
    settings: {
      minimumSeeders,
      preferMagnet: draft.preferMagnet,
      queryLimit,
      grabLimit,
      limitsUnit: 'Day',
      useFlareSolverr: draft.useFlareSolverr,
    },
  }
}

function FieldError({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return <p id={id} role="alert" className="mt-1 text-xs text-danger">{message}</p>
}

/**
 * Why the FlareSolverr choice is not the operator's to make, when it is not: a catalog indexer whose
 * site cannot be reached without the solver browser. An indexer that signs in may use it too — the
 * browser only solves the site's challenge, and the session travels on requests carrying that
 * clearance — so a login is no longer a reason to lock it.
 */
type FlareSolverrLock = 'RequiredByIndexer' | null

const FLARESOLVERR_LOCK_HINT: Record<NonNullable<FlareSolverrLock>, string> = {
  RequiredByIndexer: 'Required by this catalog indexer.',
}

function SettingsFields({
  draft,
  errors,
  flareSolverrLock,
  onChange,
}: {
  draft: SettingsDraft
  errors: SettingsErrors
  flareSolverrLock: FlareSolverrLock
  onChange: (draft: SettingsDraft) => void
}) {
  return (
    <div className="flex flex-col gap-3">
      <div>
        <TextField
          id="indexer-minimum-seeders"
          label="Minimum seeders"
          inputMode="numeric"
          value={draft.minimumSeeders}
          onChange={(event) => onChange({ ...draft, minimumSeeders: event.target.value })}
          aria-invalid={errors.minimumSeeders ? true : undefined}
          aria-describedby={errors.minimumSeeders ? 'indexer-minimum-seeders-error' : undefined}
          hint="Blank allows releases with any reported seeder count."
        />
        <FieldError id="indexer-minimum-seeders-error" message={errors.minimumSeeders} />
      </div>
      <Checkbox
        label="Prefer magnet links"
        checked={draft.preferMagnet}
        onChange={(event) => onChange({ ...draft, preferMagnet: event.target.checked })}
      />
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div>
          <TextField
            id="indexer-query-limit"
            label="Daily query limit"
            inputMode="numeric"
            value={draft.queryLimit}
            onChange={(event) => onChange({ ...draft, queryLimit: event.target.value })}
            aria-invalid={errors.queryLimit ? true : undefined}
            aria-describedby={errors.queryLimit ? 'indexer-query-limit-error' : undefined}
            hint="Blank means unlimited."
          />
          <FieldError id="indexer-query-limit-error" message={errors.queryLimit} />
        </div>
        <div>
          <TextField
            id="indexer-grab-limit"
            label="Daily detail/grab limit"
            inputMode="numeric"
            value={draft.grabLimit}
            onChange={(event) => onChange({ ...draft, grabLimit: event.target.value })}
            aria-invalid={errors.grabLimit ? true : undefined}
            aria-describedby={errors.grabLimit ? 'indexer-grab-limit-error' : undefined}
            hint="Counts detail pages fetched to resolve download links. Blank means unlimited."
          />
          <FieldError id="indexer-grab-limit-error" message={errors.grabLimit} />
        </div>
      </div>
      <Checkbox
        label="Use FlareSolverr"
        // A locked box shows the value that will actually be sent, not the draft underneath it.
        checked={flareSolverrLock === null ? draft.useFlareSolverr : flareSolverrLock === 'RequiredByIndexer'}
        disabled={flareSolverrLock !== null}
        onChange={(event) => onChange({ ...draft, useFlareSolverr: event.target.checked })}
        hint={flareSolverrLock === null ? undefined : FLARESOLVERR_LOCK_HINT[flareSolverrLock]}
      />
    </div>
  )
}

function TestResultAlert({ result }: { result: IndexerTestResult }) {
  return (
    <Alert tone={result.succeeded ? 'success' : 'danger'} title={result.succeeded ? 'Test succeeded' : 'Test failed'}>
      <p>{result.message}</p>
      <p className="mt-1 text-xs">
        Code: {result.code} · {result.candidateCount} candidates · {result.durationMs} ms · Tested{' '}
        {new Date(result.testedAt).toLocaleString()}
        {/* Only said when it means something: an indexer whose definition declares no login is neither
            authenticated nor unauthenticated, and pretending otherwise would just be noise. */}
        {result.authenticated !== null && (result.authenticated ? ' · Signed in' : ' · Not signed in')}
      </p>
    </Alert>
  )
}

/** Stable identity of a catalog entry: keys are unique only within the source that publishes them. */
function entryId(entry: IndexerCatalogEntry): string {
  return `${entry.sourceId}/${entry.key}`
}

/**
 * The indexers published by the administrator's enabled catalog sources, each ready to install with
 * the defaults its source declares. Cinomni ships with no source, so an empty catalog is the normal
 * starting point and points at Manual setup.
 */
export function NativeIndexerCatalog({
  enabled,
  onInstalled,
  onManual,
}: {
  enabled: boolean
  onInstalled: () => void
  /** Switches the surrounding dialog to manual setup, the alternative to a catalog source. */
  onManual: () => void
}) {
  const queryClient = useQueryClient()
  const [selected, setSelected] = useState<IndexerCatalogEntry | null>(null)
  const [name, setName] = useState('')
  const [baseUrl, setBaseUrl] = useState('')
  const [priority, setPriority] = useState('')
  const [settings, setSettings] = useState<SettingsDraft | null>(null)
  const [settingsErrors, setSettingsErrors] = useState<SettingsErrors>({})
  const [priorityError, setPriorityError] = useState<string | null>(null)
  const [testResult, setTestResult] = useState<IndexerTestResult | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const catalog = useQuery({
    queryKey: indexerCatalogQueryKey,
    queryFn: discoveryApi.indexerCatalog,
    enabled,
  })
  // Only consulted to explain an empty catalog; shares its cache with the Catalog sources section.
  const sources = useQuery({
    queryKey: catalogSourcesQueryKey,
    queryFn: discoveryApi.catalogSources,
    enabled: enabled && catalog.data?.length === 0,
  })

  function clearFeedback() {
    setTestResult(null)
    setSubmitError(null)
  }

  function choose(entry: IndexerCatalogEntry) {
    if (entry.installedIndexerId) return
    setSelected(entry)
    setName(entry.name)
    setBaseUrl(entry.baseUrls[0] ?? '')
    setPriority(String(entry.defaultPriority))
    setSettings(settingsDraft({
      ...entry.defaultSettings,
      useFlareSolverr: entry.requiresFlareSolverr || entry.defaultSettings.useFlareSolverr,
    }))
    setSettingsErrors({})
    setPriorityError(null)
    clearFeedback()
  }

  function draftRequest(): IndexerCatalogDraftRequest | null {
    if (!settings) return null
    const parsedPriority = /^\d+$/.test(priority) ? Number(priority) : 0
    const parsedSettings = parseSettings(settings)
    setPriorityError(parsedPriority >= 1 && parsedPriority <= 50 ? null : 'Priority must be a whole number from 1 to 50.')
    setSettingsErrors(parsedSettings.errors)
    if (parsedPriority < 1 || parsedPriority > 50 || !parsedSettings.settings) return null
    return { name: name.trim(), baseUrl, priority: parsedPriority, settings: parsedSettings.settings }
  }

  const test = useMutation({
    mutationFn: (request: IndexerCatalogDraftRequest) => {
      if (!selected) return Promise.reject(new Error('No catalog entry is selected.'))
      return discoveryApi.testCatalogIndexer(selected.sourceId, selected.key, request)
    },
    onSuccess: (result) => {
      setSubmitError(null)
      setTestResult(result)
    },
    onError: (error) => {
      setTestResult(null)
      setSubmitError(errorMessage(error, 'Could not test the indexer.'))
    },
  })

  const install = useMutation({
    mutationFn: (request: IndexerCatalogDraftRequest) => {
      if (!selected) return Promise.reject(new Error('No catalog entry is selected.'))
      return discoveryApi.installCatalogIndexer(selected.sourceId, selected.key, request)
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['indexers'] }),
        queryClient.invalidateQueries({ queryKey: indexerCatalogQueryKey }),
      ])
      setSelected(null)
      setSettings(null)
      onInstalled()
    },
    onError: (error) => setSubmitError(errorMessage(error, 'Could not install the indexer.')),
  })

  if (selected && settings) {
    const pending = test.isPending || install.isPending
    return (
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault()
          setSubmitError(null)
          const request = draftRequest()
          if (request) install.mutate(request)
        }}
      >
        <div>
          <p className="font-medium text-fg">Set up {selected.name}</p>
          <p className="mt-0.5 text-xs text-faint">From {selected.sourceName}</p>
          <p className="mt-1 text-sm text-muted">{selected.description}</p>
        </div>
        <TextField
          label="Name"
          required
          value={name}
          onChange={(event) => { setName(event.target.value); clearFeedback() }}
          autoFocus
        />
        <Select label="Base URL" required value={baseUrl} onChange={(event) => { setBaseUrl(event.target.value); clearFeedback() }}>
          {selected.baseUrls.map((url) => <option key={url} value={url}>{url}</option>)}
        </Select>
        <div>
          <TextField
            id="catalog-priority"
            label="Priority"
            inputMode="numeric"
            required
            value={priority}
            onChange={(event) => { setPriority(event.target.value); clearFeedback() }}
            aria-invalid={priorityError ? true : undefined}
            aria-describedby={priorityError ? 'catalog-priority-error' : undefined}
          />
          <FieldError id="catalog-priority-error" message={priorityError ?? undefined} />
        </div>
        {/* A catalog entry carries no login block — the API refuses to ship one that does — so the
            only lock reachable from here is the entry demanding the solver browser. */}
        <SettingsFields
          draft={settings}
          errors={settingsErrors}
          flareSolverrLock={selected.requiresFlareSolverr ? 'RequiredByIndexer' : null}
          onChange={(next) => { setSettings(next); clearFeedback() }}
        />
        {testResult && <TestResultAlert result={testResult} />}
        {submitError && <Alert tone="danger">{submitError}</Alert>}
        <div className="mt-1 flex flex-wrap justify-between gap-2">
          <Button type="button" variant="ghost" disabled={pending} onClick={() => setSelected(null)}>Back</Button>
          <div className="flex gap-2">
            <Button
              type="button"
              variant="subtle"
              loading={test.isPending}
              disabled={install.isPending}
              onClick={() => { setSubmitError(null); const request = draftRequest(); if (request) test.mutate(request) }}
            >
              Test
            </Button>
            <Button type="submit" loading={install.isPending} disabled={test.isPending}>Install</Button>
          </div>
        </div>
      </form>
    )
  }

  if (catalog.isPending) return <p role="status" className="py-8 text-center text-sm text-muted">Loading the indexer catalog…</p>
  if (catalog.isError) {
    return <ErrorState message={errorMessage(catalog.error, 'Could not load the indexer catalog.')} onRetry={() => void catalog.refetch()} />
  }
  if (catalog.data.length === 0) {
    const manualAction = (
      <Button type="button" variant="subtle" size="sm" onClick={onManual}>
        Use Manual setup
      </Button>
    )
    if (sources.isPending) {
      return <p role="status" className="py-8 text-center text-sm text-muted">Loading the indexer catalog…</p>
    }
    if (sources.data?.length === 0) {
      return (
        <EmptyState
          title="No catalog sources"
          description={`${CATALOG_SOURCE_EXPLANATION} Add one under Catalog sources on the Indexers page, or use Manual setup to configure a Torznab, Newznab or definition indexer.`}
          action={manualAction}
        />
      )
    }
    return (
      <EmptyState
        title="The catalog is empty"
        description="Your enabled catalog sources publish no indexers right now. Refresh or enable a source under Catalog sources, or use Manual setup."
        action={manualAction}
      />
    )
  }

  return (
    <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2">
      {catalog.data.map((entry) => {
        const installed = entry.installedIndexerId !== null
        return (
          <Card key={entryId(entry)} as="li" interactive={!installed} className="flex flex-col gap-3">
            <div className="flex items-start justify-between gap-3">
              <div>
                <p className="font-medium text-fg">{entry.name}</p>
                <p className="mt-1 text-sm text-muted">{entry.description}</p>
              </div>
              {installed && <Badge tone="success">Installed</Badge>}
            </div>
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted">
              <Badge tone="neutral">{entry.protocol}</Badge>
              <span>{entry.releaseProtocol}</span>
              <span>From {entry.sourceName}</span>
              {entry.requiresFlareSolverr && <span>FlareSolverr required</span>}
            </div>
            <Button
              type="button"
              variant="subtle"
              disabled={installed}
              aria-label={installed ? undefined : `Set up ${entry.name} from ${entry.sourceName}`}
              onClick={() => choose(entry)}
            >
              {installed ? 'Already installed' : `Set up ${entry.name}`}
            </Button>
          </Card>
        )
      })}
    </ul>
  )
}

export function IndexerSettingsModal({ indexer, onClose }: { indexer: IndexerSummary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [settings, setSettings] = useState(() => settingsDraft(indexer.settings))
  const [errors, setErrors] = useState<SettingsErrors>({})
  const [result, setResult] = useState<IndexerTestResult | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: (body: IndexerSettings) => discoveryApi.setIndexerSettings(indexer.id, body),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
      onClose()
    },
    onError: (error) => setSubmitError(errorMessage(error, 'Could not save indexer settings.')),
  })
  const test = useMutation({
    mutationFn: () => discoveryApi.testIndexer(indexer.id),
    onSuccess: async (nextResult) => {
      setSubmitError(null)
      setResult(nextResult)
      await queryClient.invalidateQueries({ queryKey: ['indexers'] })
    },
    onError: (error) => { setResult(null); setSubmitError(errorMessage(error, 'Could not test the indexer.')) },
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitError(null)
    const parsed = parseSettings(settings)
    setErrors(parsed.errors)
    if (parsed.settings) save.mutate(parsed.settings)
  }

  const pending = save.isPending || test.isPending
  return (
    <Modal open onClose={onClose} title={`Settings — ${indexer.name}`}>
      <form onSubmit={submit} className="flex flex-col gap-4">
        <SettingsFields draft={settings} errors={errors} flareSolverrLock={null} onChange={setSettings} />
        {indexer.lastTestedAt && indexer.lastTestSucceeded !== null && (
          <Alert tone={indexer.lastTestSucceeded ? 'success' : 'danger'} title={indexer.lastTestSucceeded ? 'Last test succeeded' : 'Last test failed'}>
            <p>{indexer.lastTestMessage ?? indexer.lastTestCode ?? 'No details were reported.'}</p>
            <p className="mt-1 text-xs">Tested {new Date(indexer.lastTestedAt).toLocaleString()}{indexer.lastTestCode ? ` · ${indexer.lastTestCode}` : ''}</p>
          </Alert>
        )}
        {result && <TestResultAlert result={result} />}
        {submitError && <Alert tone="danger">{submitError}</Alert>}
        <div className="mt-1 flex flex-wrap justify-end gap-2">
          <Button type="button" variant="ghost" disabled={pending} onClick={onClose}>Cancel</Button>
          <Button type="button" variant="subtle" loading={test.isPending} disabled={save.isPending} onClick={() => test.mutate()}>Test</Button>
          <Button type="submit" loading={save.isPending} disabled={test.isPending}>Save settings</Button>
        </div>
      </form>
    </Modal>
  )
}
