import { useEffect, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { operationsApi } from '@/api/endpoints'
import type { OperationsSetting, SettingSource } from '@/api/types'
import { ApiError, errorMessage } from '@/lib/api'
import { HardwarePanel } from './HardwarePanel'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Checkbox } from '@/ui/Checkbox'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'
import { TextField } from '@/ui/TextField'

const SETTINGS_QUERY_KEY = ['operations', 'settings']

/** What an operator reads for each value of an enumeration key; a value not listed is shown as it is. */
const ENUM_VALUE_LABEL: Readonly<Record<string, string>> = {
  auto: 'Automatic',
  vaapi: 'VAAPI (AMD / Intel on Linux)',
  qsv: 'Quick Sync (Intel)',
  nvenc: 'NVENC (NVIDIA)',
  amf: 'AMF (AMD on Windows)',
  h264: 'H.264 (plays everywhere)',
  'hevc-when-supported': 'HEVC when the device supports it',
  fastest: 'Fastest',
  fast: 'Fast',
  balanced: 'Balanced',
  quality: 'Best quality',
  original: 'Original (no limit)',
  '2160': '2160p (4K)',
  '1440': '1440p',
  '1080': '1080p',
  '720': '720p',
  '480': '480p',
  '6': '5.1',
  '2': 'Stereo',
  hable: 'Hable (filmic)',
  reinhard: 'Reinhard',
  mobius: 'Möbius',
  bt2390: 'BT.2390 (broadcast)',
}

type SettingsSection = 'Catalog' | 'Decision' | 'Import' | 'Metadata' | 'Monitoring' | 'Requests' | 'Backup' | 'Operations' | 'Playback' | 'Security' | 'Subtitles'

interface SettingFieldMeta {
  section: SettingsSection
  label: string
  /** Shown under the field unless the environment pins the value, which has its own explanation. */
  hint?: string
  /**
   * True for the three `*.interval` keys whose value also seeds a `ScheduledJobRegistration`
   * captured once at process start. Saving still persists and audits immediately, and
   * `ILiveOptions.Current` reflects it at once — only the job's actual cadence waits for a restart.
   * `retention.backup.interval` is deliberately excluded: `BackupHostedService` re-reads it every
   * tick of its own loop, so that one key's cadence does change without a restart.
   */
  hotReloadWarning?: boolean
}

/**
 * Which settings key goes in which section, and what an operator calls each one. A key's own naming
 * is not consistent between the backing classes (`backup.keepCount` vs `retention.backup.interval` vs
 * `retention.operations.batchSize`, which nests two prefixes), so this map is the single source of
 * grouping — parsing the key string would guess wrong. A key the backend does not return (a module not
 * composed into this installation, or one not in the map at all) simply never renders — see
 * `rowsForSection`.
 *
 * Insertion order here is also render order within a section.
 */
const SETTING_FIELD_META: Readonly<Record<string, SettingFieldMeta>> = {
  'decision.evaluationRetention': { section: 'Decision', label: 'Evaluation retention' },
  'retention.decision.interval': { section: 'Decision', label: 'Purge interval', hotReloadWarning: true },
  'metadata.snapshotRetention': { section: 'Metadata', label: 'Snapshot retention' },
  'retention.metadata.interval': { section: 'Metadata', label: 'Purge interval', hotReloadWarning: true },
  // Empty is the shipped state: no region means no classification, not a guessed one.
  'catalog.trendingList.enabled': {
    section: 'Catalog',
    label: 'Add trending titles',
    hint: 'When on, the scheduled job adds movies and series from TMDB trending that are not already in the catalog, with monitoring off: nothing downloads until you switch a title on or approve a request for it. Off adds nothing. It never removes a title.',
  },
  'import.movieNaming': {
    section: 'Import',
    label: 'Movie file names',
    hint: 'ReleaseName keeps the downloaded file name. TitleYear names the folder and file from the catalog title and year. Series already use the catalog title. Applies to new imports only.',
  },
  'monitoring.searchDelay': {
    section: 'Monitoring',
    label: 'Search delay after air',
    hint: 'How long the automatic search waits after a known air time. Zero searches as soon as it has aired. An interactive search does not wait. Format: days.hours:minutes:seconds.',
  },
  'metadata.contentRatingRegion': {
    section: 'Metadata',
    label: 'Classification region',
    hint: 'Two-letter country code, or empty. Empty means no age classification is read.',
  },
  // Secrets: never shown, only replaced. An empty key switches its provider off, and it says so in the log.
  'metadata.tmdb.apiKey': {
    section: 'Metadata',
    label: 'TMDB API key',
    hint: 'Movie search needs it. Series use it alongside TheTVDB and TVmaze. Applies at once.',
  },
  'metadata.tvdb.apiKey': {
    section: 'Metadata',
    label: 'TheTVDB API key',
    hint: 'Gives series their season and episode structure. Applies at once.',
  },
  'metadata.tvdb.pin': {
    section: 'Metadata',
    label: 'TheTVDB subscriber PIN',
    hint: 'Only for a user-supported TheTVDB key. Leave it unset for a project key.',
  },
  'backup.keepCount': { section: 'Backup', label: 'Backups to keep' },
  'retention.backup.interval': { section: 'Backup', label: 'Backup interval' },
  'retention.operations.outboxRetention': { section: 'Operations', label: 'Outbox retention' },
  'retention.operations.completedCommandRetention': { section: 'Operations', label: 'Completed command retention' },
  'retention.operations.failedCommandRetention': { section: 'Operations', label: 'Failed command retention' },
  'retention.operations.batchSize': { section: 'Operations', label: 'Purge batch size' },
  'retention.operations.interval': { section: 'Operations', label: 'Purge interval', hotReloadWarning: true },
  'playback.hardwareTranscodingEnabled': {
    section: 'Playback',
    label: 'Hardware transcoding',
    hint: 'Off forces every conversion to the CPU, whatever the hardware test found.',
  },
  'playback.hardwareBackend': {
    section: 'Playback',
    label: 'Preferred hardware',
    hint: 'Automatic takes the first backend that passed the hardware test. A named one is used when it passed, otherwise the next.',
  },
  'playback.hardwareDecoding': {
    section: 'Playback',
    label: 'Hardware decoding',
    hint: 'Also decode the source on the GPU. Turn off if a driver decodes some files badly.',
  },
  'playback.outputCodec': {
    section: 'Playback',
    label: 'Output codec',
    hint: 'HEVC needs about 40% less bandwidth, and is only used when the device plays it and the GPU encodes it.',
  },
  'playback.encoderPreset': {
    section: 'Playback',
    label: 'Encoder speed',
    hint: 'Faster presets use less CPU/GPU; slower ones look better at the same bitrate.',
  },
  'playback.videoQuality': {
    section: 'Playback',
    label: 'Video quality (15–40)',
    hint: 'Constant-quality target: lower looks better and uses more bandwidth. 23 is a good middle.',
  },
  'playback.maxResolution': {
    section: 'Playback',
    label: 'Maximum resolution',
    hint: 'Anything taller is converted down for every viewer — useful on a weak CPU or a slow uplink.',
  },
  'playback.maxBitrateKbps': {
    section: 'Playback',
    label: 'Maximum bitrate (kbps)',
    hint: 'A file above it is converted down. 0 means no limit.',
  },
  'playback.encoderThreads': {
    section: 'Playback',
    label: 'Software encoder threads',
    hint: '0 lets FFmpeg decide. Lower it to leave CPU for other services.',
  },
  'playback.toneMapping': {
    section: 'Playback',
    label: 'HDR tone mapping',
    hint: 'Bring HDR and Dolby Vision to SDR when converting, so colours do not look washed out.',
  },
  'playback.toneMapAlgorithm': { section: 'Playback', label: 'Tone mapping curve' },
  'playback.maxAudioChannels': {
    section: 'Playback',
    label: 'Maximum audio channels',
    hint: 'Surround above it is folded down when converting. Stereo suits phones, laptops and TVs without a receiver.',
  },
  'playback.audioBitrateKbps': { section: 'Playback', label: 'Audio bitrate (kbps)' },
  'playback.burnInImageSubtitles': {
    section: 'Playback',
    label: 'Burn in picture subtitles',
    hint: 'PGS and VobSub subtitles can only be shown drawn into the video, which needs a conversion.',
  },
  // Zero means no limit, and is what every installation ships with. An account may override it in
  // either direction from the Users page.
  'requests.defaultOpenRequestLimit': { section: 'Requests', label: 'Open requests per account' },
  // Login and first-run setup only. 1–1000 attempts, and a window from 1 minute to 1 hour.
  'security.anonymousRateLimitPermits': {
    section: 'Security',
    label: 'Sign-in attempts per client',
    hint: 'How many login or setup attempts one client may make in the window. 1 to 1000.',
  },
  'subtitles.wantedLanguages': {
    section: 'Subtitles',
    label: 'Wanted languages',
    hint: 'Comma-separated two-letter codes, in the order to search. Example: en, es. Empty searches for none. A change is picked up for titles already in the library, not only the next import.',
  },
  'subtitles.hearingImpaired': {
    section: 'Subtitles',
    label: 'Hearing-impaired only',
    hint: 'When on, only a hearing-impaired subtitle satisfies a language. When off, a hearing-impaired track does not.',
  },
  'subtitles.forced': {
    section: 'Subtitles',
    label: 'Forced only',
    hint: 'When on, only a forced subtitle (foreign dialogue) satisfies a language. When off, a forced track does not.',
  },
  'security.anonymousRateLimitWindow': {
    section: 'Security',
    label: 'Sign-in attempt window',
    hint: 'How long that budget lasts, from 1 minute to 1 hour. Format: days.hours:minutes:seconds (for example 00:01:00).',
  },
}

const SECTION_ORDER: readonly SettingsSection[] = [
  'Catalog',
  'Decision',
  'Import',
  'Metadata',
  'Monitoring',
  'Requests',
  'Backup',
  'Operations',
  'Playback',
  'Security',
  'Subtitles',
]

const SOURCE_LABEL: Record<SettingSource, string> = {
  environment: 'Fixed by environment',
  database: 'Customized',
  file: 'From configuration file',
  default: 'Default',
}

const SOURCE_TONE: Record<SettingSource, Tone> = {
  environment: 'warning',
  database: 'accent',
  file: 'info',
  default: 'neutral',
}

function SourceBadge({ source }: { source: SettingSource }) {
  return <Badge tone={SOURCE_TONE[source]}>{SOURCE_LABEL[source]}</Badge>
}

/** Rows for one section, in the fixed order declared in `SETTING_FIELD_META`, dropping any key the
 *  backend did not return rather than guessing at a group for it. */
function rowsForSection(settings: readonly OperationsSetting[], section: SettingsSection): OperationsSetting[] {
  const byKey = new Map(settings.map((setting) => [setting.key, setting]))
  return Object.entries(SETTING_FIELD_META)
    .filter(([, meta]) => meta.section === section)
    .map(([key]) => byKey.get(key))
    .filter((setting): setting is OperationsSetting => setting !== undefined)
}

function fieldId(key: string): string {
  return `setting-${key.replace(/[^a-z0-9]+/gi, '-')}`
}

function hintFor(setting: OperationsSetting, fieldHint?: string): string | undefined {
  if (setting.source === 'environment') {
    return 'Fixed by the environment or command line; not editable here.'
  }
  if (fieldHint) return fieldHint
  if (setting.kind === 'Duration') {
    return 'Format: days.hours:minutes:seconds (for example 7.00:00:00 for 7 days).'
  }
  return undefined
}

export function SettingsPage() {
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: SETTINGS_QUERY_KEY,
    queryFn: operationsApi.settings,
  })

  return (
    <section className="space-y-4">
      <div>
        <h2 className="text-section text-fg">Settings</h2>
        <p className="mt-1 text-meta text-muted">
          Metadata provider keys, retention, backup, playback, the search delay, the classification region, and the anonymous sign-in throttle.
          Everything else still comes from configuration files and environment variables.
        </p>
      </div>

      {isPending ? (
        <LoadingBlock label="Loading settings" />
      ) : isError ? (
        <ErrorState message={errorMessage(error, 'Could not load settings.')} onRetry={() => void refetch()} />
      ) : data.length === 0 ? (
        <EmptyState
          title="No editable settings"
          description="This installation has no settings registered in the live catalogue yet."
        />
      ) : (
        <>
          <SectionIndex sections={SECTION_ORDER.filter((section) => rowsForSection(data, section).length > 0)} />
          {SECTION_ORDER.map((section) => {
            const rows = rowsForSection(data, section)
            if (rows.length === 0) return null
            return (
              <Card as="section" key={section} id={sectionAnchor(section)} className="scroll-mt-8 space-y-4">
                <h3 className="text-body font-semibold text-fg">{section}</h3>
                {section === 'Playback' && <HardwarePanel />}
                <div className="space-y-4">
                  {rows.map((setting, index) => (
                    <div key={setting.key} className={index > 0 ? 'border-t border-line-soft pt-4' : undefined}>
                      <SettingRow setting={setting} />
                    </div>
                  ))}
                </div>
              </Card>
            )
          })}
        </>
      )}
    </section>
  )
}

function sectionAnchor(section: SettingsSection): string {
  return `settings-${section.toLowerCase().replace(/\s+/g, '-')}`
}

/**
 * An index of the sections present, so a long form is one jump away from any of its parts instead of
 * a scroll through every other domain. Plain in-page links: they work without script and keep the
 * browser's own history and focus behaviour.
 */
function SectionIndex({ sections }: { sections: readonly SettingsSection[] }) {
  if (sections.length < 2) return null
  return (
    <nav aria-label="Settings sections" className="flex flex-wrap gap-1">
      {sections.map((section) => (
        <a
          key={section}
          href={`#${sectionAnchor(section)}`}
          className="rounded-full px-3 py-1 text-meta text-muted transition-colors hover:bg-hover hover:text-fg"
        >
          {section}
        </a>
      ))}
    </nav>
  )
}

function SettingRow({ setting }: { setting: OperationsSetting }) {
  const meta = SETTING_FIELD_META[setting.key]
  const label = meta?.label ?? setting.key

  if (setting.isSecret) {
    return (
      <SecretSettingRow setting={setting} label={label} hint={meta?.hint} hotReloadWarning={meta?.hotReloadWarning} />
    )
  }

  return (
    <ValueSettingRow
      setting={setting}
      label={label}
      hint={meta?.hint}
      hotReloadWarning={meta?.hotReloadWarning}
    />
  )
}

const CONFLICT_MESSAGE =
  'This setting changed since the page loaded. The current value is shown now; make your change again to save it.'

/** A write refused because another save landed first — the one failure a reload actually fixes. */
function isVersionConflict(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'settings.conflict'
}

function ValueSettingRow({
  setting,
  label,
  hint,
  hotReloadWarning,
}: {
  setting: OperationsSetting
  label: string
  hint?: string
  hotReloadWarning?: boolean
}) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState(setting.value ?? '')
  const [error, setError] = useState<string | null>(null)

  // Follows the confirmed server value once a save or reset lands and the query re-fetches. It never
  // drives itself off a failed write — see `onError` below, which restores this explicitly instead.
  useEffect(() => {
    setDraft(setting.value ?? '')
  }, [setting.value])

  const update = useMutation({
    mutationFn: (value: string | null) =>
      operationsApi.updateSetting(setting.key, { value, expectedVersion: setting.version }),
    onMutate: () => setError(null),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: SETTINGS_QUERY_KEY }),
    onError: (err) => {
      if (isVersionConflict(err)) {
        // Someone else saved this key since the page loaded: the version this row holds is stale, so
        // every retry would fail the same way. Fetch the current value and version before trying again.
        void queryClient.invalidateQueries({ queryKey: SETTINGS_QUERY_KEY })
        setError(CONFLICT_MESSAGE)
        setDraft(setting.value ?? '')
        return
      }
      setError(errorMessage(err, 'Could not update this setting.'))
      // The write failed — the running configuration did not change, so the field must go on
      // showing it rather than the value the operator just tried and failed to save.
      setDraft(setting.value ?? '')
    },
  })

  const isEnvironment = setting.source === 'environment'
  const isDirty = draft !== (setting.value ?? '')
  const inputId = fieldId(setting.key)

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (!isDirty) return
    update.mutate(draft)
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-end justify-between gap-3">
        {isEnvironment ? (
          <div className="min-w-[16rem] flex-1">
            {setting.kind === 'Boolean' ? (
              <Checkbox
                label={label}
                id={inputId}
                checked={draft === 'true'}
                disabled
                hint={hintFor(setting, hint)}
              />
            ) : (
              <TextField
                label={label}
                id={inputId}
                value={setting.kind === 'Enum' ? (ENUM_VALUE_LABEL[draft] ?? draft) : draft}
                disabled
                hint={hintFor(setting, hint)}
              />
            )}
          </div>
        ) : (
          <form onSubmit={onSubmit} className="flex flex-1 flex-wrap items-end gap-2">
            <div className="min-w-[16rem] flex-1">
              {setting.kind === 'Boolean' ? (
                <Checkbox
                  label={label}
                  id={inputId}
                  checked={draft === 'true'}
                  onChange={(event) => setDraft(event.target.checked ? 'true' : 'false')}
                  hint={hintFor(setting, hint)}
                />
              ) : (
                setting.kind === 'Enum' && setting.allowedValues?.length ? (
                  <Select
                    label={label}
                    id={inputId}
                    value={draft}
                    onChange={(event) => setDraft(event.target.value)}
                    hint={hintFor(setting, hint)}
                  >
                    {setting.allowedValues.map((value) => (
                      <option key={value} value={value}>
                        {ENUM_VALUE_LABEL[value] ?? value}
                      </option>
                    ))}
                  </Select>
                ) : (
                  <TextField
                    label={label}
                    id={inputId}
                    type={setting.kind === 'Number' ? 'number' : 'text'}
                    min={setting.minValue ?? undefined}
                    max={setting.maxValue ?? undefined}
                    value={draft}
                    onChange={(event) => setDraft(event.target.value)}
                    hint={hintFor(setting, hint)}
                  />
                )
              )}
            </div>
            <Button type="submit" size="sm" disabled={!isDirty} loading={update.isPending}>
              Save
            </Button>
            {setting.isSet && (
              <Button
                type="button"
                size="sm"
                variant="ghost"
                loading={update.isPending}
                onClick={() => update.mutate(null)}
              >
                Reset
              </Button>
            )}
          </form>
        )}
        <SourceBadge source={setting.source} />
      </div>

      {hotReloadWarning && (
        <p className="text-xs text-faint">
          The change saves immediately, but the scheduled job&apos;s cadence only applies after this
          installation next restarts.
        </p>
      )}

      {error && <Alert tone="danger">{error}</Alert>}
    </div>
  )
}

function SecretSettingRow({
  setting,
  label,
  hint,
  hotReloadWarning,
}: {
  setting: OperationsSetting
  label: string
  hint?: string
  hotReloadWarning?: boolean
}) {
  const [replacing, setReplacing] = useState(false)
  const isEnvironment = setting.source === 'environment'
  // Configured wherever the value comes from: the server only reports a file or environment source when
  // that source actually holds a value, and a stored row is the only other way to have one.
  const isConfigured = setting.isSet || isEnvironment || setting.source === 'file'

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <p className="text-sm font-medium text-fg">{label}</p>
          <p className="mt-0.5 font-mono text-xs text-faint">{setting.configurationPath}</p>
        </div>
        <div className="flex items-center gap-2">
          <SourceBadge source={setting.source} />
          <Badge tone={isConfigured ? 'success' : 'neutral'}>{isConfigured ? 'Configured' : 'Not set'}</Badge>
          {!isEnvironment && (
            <Button size="sm" variant="subtle" onClick={() => setReplacing(true)}>
              Replace
            </Button>
          )}
        </div>
      </div>

      {hint && !isEnvironment && <p className="text-xs text-faint">{hint}</p>}

      {hotReloadWarning && (
        <p className="text-xs text-faint">
          The change saves immediately, but the scheduled job&apos;s cadence only applies after this
          installation next restarts.
        </p>
      )}

      <ReplaceSecretModal
        open={replacing}
        onClose={() => setReplacing(false)}
        settingKey={setting.key}
        expectedVersion={setting.version}
        label={label}
      />
    </div>
  )
}

function ReplaceSecretModal({
  open,
  onClose,
  settingKey,
  expectedVersion,
  label,
}: {
  open: boolean
  onClose: () => void
  settingKey: string
  expectedVersion: number
  label: string
}) {
  const queryClient = useQueryClient()
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)

  const replace = useMutation({
    mutationFn: () => operationsApi.updateSetting(settingKey, { value, expectedVersion }),
    onMutate: () => setError(null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: SETTINGS_QUERY_KEY })
      handleClose()
    },
    onError: (err) => {
      if (isVersionConflict(err)) {
        void queryClient.invalidateQueries({ queryKey: SETTINGS_QUERY_KEY })
        // A secret is never shown, so unlike a value row there is no "current value" to point at.
        setError('This value was replaced by someone else since the page loaded. Enter it again to replace it.')
        return
      }
      setError(errorMessage(err, 'Could not replace this value.'))
    },
  })

  function handleClose() {
    setValue('')
    setError(null)
    onClose()
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    replace.mutate()
  }

  return (
    <Modal open={open} onClose={handleClose} title={`Replace ${label}`}>
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <p className="text-sm text-muted">
          The current value is never shown here. Enter a new one to replace it, or cancel to leave it
          as it is.
        </p>
        <TextField
          label="New value"
          type="password"
          autoComplete="off"
          required
          value={value}
          onChange={(event) => setValue(event.target.value)}
          autoFocus
        />

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={handleClose}>
            Cancel
          </Button>
          <Button type="submit" loading={replace.isPending}>
            Replace
          </Button>
        </div>
      </form>
    </Modal>
  )
}
