import { useId, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { playbackApi } from '@/api/endpoints'
import type { HardwareReport } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { formatDateTime } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'

const HARDWARE_QUERY_KEY = ['playback', 'hardware']

const BACKEND_LABEL: Readonly<Record<string, string>> = {
  Vaapi: 'VAAPI',
  Qsv: 'Quick Sync',
  Nvenc: 'NVENC',
  Amf: 'AMF',
}

const CODEC_LABEL: Readonly<Record<string, string>> = { H264: 'H.264', Hevc: 'HEVC', h264: 'H.264', hevc: 'HEVC' }

const label = (map: Readonly<Record<string, string>>, value: string) => map[value] ?? value

/**
 * What this server can transcode with, as the hardware test last found it — every backend is tried with
 * a real encode and decode, so what is listed here is what works, not what the FFmpeg build mentions.
 * The operator runs the test again after changing a driver, a device or the container.
 */
export function HardwarePanel() {
  const queryClient = useQueryClient()
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: HARDWARE_QUERY_KEY,
    queryFn: ({ signal }) => playbackApi.hardware(signal),
  })
  const probe = useMutation({
    mutationFn: playbackApi.probeHardware,
    onSuccess: (report) => queryClient.setQueryData(HARDWARE_QUERY_KEY, report),
  })

  return (
    <div className="space-y-3 rounded-panel border border-line-soft bg-elevated/40 p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-body font-medium text-fg">Transcoding hardware</p>
          <p className="text-meta text-muted">
            Each backend is tried with a real encode and decode. Only what passed is used.
          </p>
        </div>
        <Button size="sm" variant="subtle" loading={probe.isPending} onClick={() => probe.mutate()}>
          Run hardware test
        </Button>
      </div>

      {probe.isError && <Alert tone="danger">{errorMessage(probe.error, 'The hardware test could not run.')}</Alert>}

      {isPending ? (
        <LoadingBlock label="Loading hardware" />
      ) : isError ? (
        <ErrorState message={errorMessage(error, 'Could not load the hardware report.')} onRetry={() => void refetch()} />
      ) : (
        <HardwareSummary report={data} />
      )}
    </div>
  )
}

function HardwareSummary({ report }: { report: HardwareReport }) {
  const testsId = useId()
  const [showTests, setShowTests] = useState(false)

  return (
    <div className="space-y-3 text-meta">
      {report.backends.length === 0 ? (
        <p className="text-muted">
          No hardware backend passed its test: every conversion runs on the CPU. On Docker, the GPU has to be
          passed to the container; on Windows, run the server natively to use AMF, NVENC or Quick Sync.
        </p>
      ) : (
        <ul className="space-y-2">
          {report.backends.map((backend) => (
            <li key={backend.backend} className="flex flex-wrap items-center gap-2">
              <Badge tone="success">{label(BACKEND_LABEL, backend.backend)}</Badge>
              <span className="text-muted">
                encodes {backend.encodes.map((c) => label(CODEC_LABEL, c)).join(', ') || 'H.264'}
                {backend.decodes.length > 0
                  ? ` · decodes ${backend.decodes.map((c) => label(CODEC_LABEL, c)).join(', ')}`
                  : ' · decodes in software'}
              </span>
            </li>
          ))}
        </ul>
      )}

      <div className="flex flex-wrap gap-2">
        <Capability ok={report.toneMapping} label="HDR tone mapping" />
        <Capability ok={report.subtitleOverlay} label="Subtitle burn-in" />
        <Capability ok={report.softwareHevc} label="Software HEVC" />
      </div>

      {(report.platform || report.ffmpegVersion) && (
        <p className="text-faint">
          {[report.platform, report.ffmpegVersion, report.probedAt ? `tested ${formatDateTime(report.probedAt)}` : null]
            .filter(Boolean)
            .join(' · ')}
        </p>
      )}

      {report.tests.length > 0 && (
        <div>
          <button
            type="button"
            className="text-muted underline-offset-4 hover:text-fg hover:underline"
            aria-expanded={showTests}
            aria-controls={testsId}
            onClick={() => setShowTests((shown) => !shown)}
          >
            {showTests ? 'Hide test details' : `Show test details (${report.tests.length})`}
          </button>
          {showTests && (
            <ul id={testsId} className="mt-2 space-y-1">
              {report.tests.map((test) => (
                <li key={`${test.backend}-${test.kind}-${test.codec}`} className="flex flex-wrap items-baseline gap-2">
                  <Badge tone={test.passed ? 'success' : 'danger'}>{test.passed ? 'Passed' : 'Failed'}</Badge>
                  <span className="text-fg">
                    {label(BACKEND_LABEL, test.backend)} · {test.kind === 'Encode' ? 'encode' : 'decode'}{' '}
                    {label(CODEC_LABEL, test.codec)}
                  </span>
                  {test.failure && <span className="break-all font-mono text-xs text-faint">{test.failure}</span>}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}

function Capability({ ok, label: name }: { ok: boolean; label: string }) {
  return <Badge tone={ok ? 'info' : 'neutral'}>{`${name}: ${ok ? 'available' : 'unavailable'}`}</Badge>
}
