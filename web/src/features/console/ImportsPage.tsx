import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { importsApi } from '@/api/endpoints'
import type { ImportJobSummary } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { useFallbackRefetchInterval } from '@/realtime/useRealtimeStatus'
import { Badge } from '@/ui/Badge'
import { Card } from '@/ui/Card'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { ServerIcon } from '@/ui/icons'
import { IMPORT_JOB_STATE_TONE, ImportJobDetail } from './ImportJobDetail'
import { PathRepairPanel } from './PathRepairPanel'

// The source path comes first deliberately: DataTable turns the first cell into the row's button, so
// whatever sits here becomes the row's accessible name. With the state badge first, every row
// announced itself as "Pending" or "Registered" — the one thing every row shares — and a screen-reader
// user could not tell them apart. The path is what identifies a job.
const JOB_COLUMNS: Column<ImportJobSummary>[] = [
  {
    key: 'sourcePath',
    header: 'Source path',
    width: '24rem',
    render: (job) => (
      <span className="block truncate font-mono text-xs" title={job.sourcePath}>
        {job.sourcePath}
      </span>
    ),
  },
  {
    key: 'state',
    header: 'State',
    render: (job) => <Badge tone={IMPORT_JOB_STATE_TONE[job.state]}>{job.state}</Badge>,
  },
  { key: 'fileCount', header: 'Files', align: 'end', width: '5rem', render: (job) => job.fileCount },
  {
    key: 'reason',
    header: 'Reason',
    width: '16rem',
    render: (job) => (job.reason ? <span className="text-fg">{job.reason}</span> : <span className="text-faint">—</span>),
  },
]

/**
 * Where a completed download lands in the library: one job per download, though a season pack still
 * lands as a single job with many files. The job list is presentation only — every state and reason
 * shown here is what the Import module already decided; clicking a row opens the full per-file trail.
 */
export function ImportsPage() {
  const [selectedJob, setSelectedJob] = useState<ImportJobSummary | null>(null)

  const jobs = useQuery({
    queryKey: ['imports'],
    queryFn: importsApi.list,
    refetchInterval: useFallbackRefetchInterval(5_000),
  })

  return (
    <div className="space-y-4">
      <Card as="section" className="space-y-3">
        <div>
          <h2 className="font-medium text-fg">Import jobs</h2>
          <p className="mt-1 text-sm text-muted">
            A season pack still lands as one job; open a row to see every file it matched.
          </p>
        </div>

        {jobs.isError ? (
          <ErrorState
            title="Could not load import jobs"
            message={errorMessage(jobs.error)}
            onRetry={() => void jobs.refetch()}
          />
        ) : (
          <DataTable
            columns={JOB_COLUMNS}
            rows={jobs.data ?? []}
            rowKey={(job) => job.id}
            caption="Import jobs, most recently created first, with the state, source path, file count and reason the Import module recorded for each."
            loading={jobs.isPending}
            onRowClick={(job) => setSelectedJob(job)}
            empty={
              <EmptyState
                icon={<ServerIcon className="size-9" />}
                title="No import jobs yet"
                description="A job appears here once a completed download starts landing in the library."
              />
            }
          />
        )}
      </Card>

      <PathRepairPanel />

      {selectedJob && (
        <ImportJobDetail jobId={selectedJob.id} open onClose={() => setSelectedJob(null)} />
      )}
    </div>
  )
}
