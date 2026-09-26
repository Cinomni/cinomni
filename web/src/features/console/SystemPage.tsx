import { useQuery } from '@tanstack/react-query'
import { downloadsApi, healthApi, systemApi } from '@/api/endpoints'
import type { HealthStatus, TunnelEgressStatus } from '@/api/types'
import { systemInfoQueryKey } from '@/components/BuildFooter'
import { errorMessage } from '@/lib/api'
import { formatRelative } from '@/lib/format'
import { tunnelPolicyExplanation, tunnelPolicyLabel, tunnelReasonSentence, tunnelTone } from '@/lib/tunnel'
import { useFallbackRefetchInterval } from '@/realtime/useRealtimeStatus'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Card } from '@/ui/Card'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'

/** Readiness reports Degraded as a 200 on purpose, so the tone carries the distinction the status code does not. */
const HEALTH_TONE: Record<HealthStatus, Tone> = {
  Healthy: 'success',
  Degraded: 'warning',
  Unhealthy: 'danger',
}

/**
 * The check names are fixed words chosen by the Host, never values from configuration, so they can be
 * given readable labels here. An unknown name is shown as-is: it is a word from our own source, not
 * an operator's storage root.
 */
const CHECK_LABELS: Record<string, string> = {
  postgres: 'Database',
  sidecar: 'Torrent sidecar',
  'library-storage': 'Library storage',
  'transcode-storage': 'Transcode storage',
}

function checkLabel(name: string): string {
  return CHECK_LABELS[name] ?? name
}

function TunnelPanel({ status }: { status: TunnelEgressStatus }) {
  const tone = tunnelTone(status)

  if (!status.configured) {
    return (
      <Card as="section" className="space-y-2">
        <div className="flex items-center justify-between gap-3">
          <h2 className="font-medium text-fg">Torrent egress</h2>
          <Badge tone="neutral">No tunnel</Badge>
        </div>
        <p className="text-sm text-muted">
          No tunnel device is configured, so the guard is inert and this installation behaves as it did before
          the kill-switch existed. Torrent traffic leaves over the ordinary network connection.
        </p>
      </Card>
    )
  }

  return (
    <Card as="section" className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-medium text-fg">Torrent egress</h2>
        <Badge tone={tone === 'success' ? 'success' : 'warning'}>
          {status.verified ? 'Verified' : 'Unverified'}
        </Badge>
      </div>

      <p className="text-sm text-muted">{tunnelReasonSentence(status.reason)}</p>

      {!status.verified && (
        <Alert tone="warning">
          Egress is unverified, which is not the same as unprotected and not the same as fine. Until the guard
          verifies it again, this installation applies its loss policy below.
        </Alert>
      )}

      <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div>
          <dt className="text-faint">Interface</dt>
          <dd className="text-fg">{status.tunnelDevice}</dd>
        </div>
        <div>
          <dt className="text-faint">On tunnel loss</dt>
          <dd className="text-fg">{tunnelPolicyLabel[status.policy]}</dd>
        </div>
        <div>
          <dt className="text-faint">Transfers held</dt>
          <dd className="text-fg">{status.heldTaskCount}</dd>
        </div>
        <div>
          <dt className="text-faint">Last observed</dt>
          <dd className="text-fg">{status.observedAt ? formatRelative(status.observedAt) : 'Never'}</dd>
        </div>
      </dl>

      <p className="text-xs text-faint">{tunnelPolicyExplanation[status.policy]}</p>
    </Card>
  )
}

/**
 * Which build this installation runs, stated in full. The layout footer shows the same version
 * quietly and disappears when it cannot be read; here the opposite is right — this page exists to
 * diagnose the installation, so a build identity that could not be read is a finding, not a reason
 * to render nothing.
 *
 * A row is shown only when the build actually carries it. `commit` and `buildDate` are null on any
 * build the pipeline did not stamp, which today is every one of them, and an empty "Commit —" row
 * would dress a fact about the build up as missing data.
 */
function BuildPanel() {
  const build = useQuery({
    queryKey: systemInfoQueryKey,
    queryFn: ({ signal }) => systemApi.info(signal),
    staleTime: Infinity,
    retry: false,
  })

  return (
    <Card as="section" className="space-y-3">
      <h2 className="font-medium text-fg">Build</h2>

      {build.isPending ? (
        <LoadingBlock size="sm" label="Reading build identity" />
      ) : build.isError ? (
        <ErrorState
          title="Build identity could not be read"
          message={errorMessage(build.error)}
          onRetry={() => void build.refetch()}
        />
      ) : (
        <>
          <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div>
              <dt className="text-faint">Version</dt>
              <dd className="break-all font-mono text-fg">{build.data.informationalVersion}</dd>
            </div>
            {/* The two differ only on a stamped build; repeating the same string twice on a
                development build would look like a rendering mistake. */}
            {build.data.version !== build.data.informationalVersion && (
              <div>
                <dt className="text-faint">Release number</dt>
                <dd className="break-all font-mono text-fg">{build.data.version}</dd>
              </div>
            )}
            {build.data.commit && (
              <div>
                <dt className="text-faint">Commit</dt>
                <dd className="break-all font-mono text-fg">{build.data.commit}</dd>
              </div>
            )}
            {build.data.buildDate && (
              <div>
                <dt className="text-faint">Built</dt>
                <dd className="text-fg">{new Date(build.data.buildDate).toLocaleString()}</dd>
              </div>
            )}
          </dl>

          {build.data.commit === null && (
            <p className="text-xs text-faint">
              This build carries no commit or build date. Nothing stamped them, which is what every
              development build and any image built without those arguments reports.
            </p>
          )}
        </>
      )}
    </Card>
  )
}

/**
 * What the platform reports about itself: which build it is, the readiness probe's per-dependency
 * verdict, and where torrent traffic is actually leaving.
 *
 * All three reads are presentation only. Readiness is computed by the Host and the egress verdict by
 * the guard; this page never concludes either one, because a client that decided health would be
 * deciding it from outside the process that knows.
 */
export function SystemPage() {
  const interval = useFallbackRefetchInterval(15_000)

  const health = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: ({ signal }) => healthApi.ready(signal),
    refetchInterval: 15_000,
  })

  const tunnel = useQuery({
    queryKey: ['downloads', 'tunnel'],
    queryFn: downloadsApi.tunnel,
    refetchInterval: interval,
  })

  return (
    <div className="space-y-4">
      <Card as="section" className="space-y-3">
        <div className="flex items-center justify-between gap-3">
          <h2 className="font-medium text-fg">Readiness</h2>
          {health.data && <Badge tone={HEALTH_TONE[health.data.status]}>{health.data.status}</Badge>}
        </div>

        {health.isPending ? (
          <LoadingBlock size="sm" label="Checking dependencies" />
        ) : health.isError ? (
          <ErrorState
            title="Readiness could not be read"
            message={errorMessage(health.error)}
            onRetry={() => void health.refetch()}
          />
        ) : (
          <>
            <ul className="divide-y divide-line">
              {health.data.checks.map((check) => (
                <li key={check.name} className="flex items-center justify-between gap-3 py-2 text-sm">
                  <span className="text-fg">{checkLabel(check.name)}</span>
                  <Badge tone={HEALTH_TONE[check.status]}>{check.status}</Badge>
                </li>
              ))}
            </ul>
            <p className="text-xs text-faint">
              A degraded dependency still serves the household: browsing, playback and this interface keep
              working. Only an unhealthy one takes the node out of rotation. The probe reports a name and a
              status and nothing else, deliberately — it is answered without credentials.
            </p>
          </>
        )}
      </Card>

      <BuildPanel />

      {tunnel.isPending ? (
        <LoadingBlock size="sm" label="Reading egress status" />
      ) : tunnel.isError ? (
        <ErrorState
          title="Egress status could not be read"
          message={errorMessage(tunnel.error)}
          onRetry={() => void tunnel.refetch()}
        />
      ) : (
        <TunnelPanel status={tunnel.data} />
      )}
    </div>
  )
}
