import { useQuery } from '@tanstack/react-query'
import { subtitlesApi } from '@/api/endpoints'
import type { SubtitleSearchSummary } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { subtitleTone } from '@/lib/status'
import { Badge } from '@/ui/Badge'
import { Card } from '@/ui/Card'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'

const COLUMNS: Column<SubtitleSearchSummary>[] = [
  { key: 'language', header: 'Language', render: (row) => row.language },
  { key: 'forced', header: 'Forced', render: (row) => (row.forced ? 'Yes' : 'No') },
  {
    key: 'hearingImpaired',
    header: 'Hearing-impaired',
    render: (row) => (row.hearingImpaired ? 'Yes' : 'No'),
  },
  {
    key: 'state',
    header: 'State',
    render: (row) => <Badge tone={subtitleTone[row.state]}>{row.state}</Badge>,
  },
  { key: 'attempts', header: 'Attempts', render: (row) => row.attempts, align: 'end' },
]

/**
 * Subtitle search status for one media file. `subtitlesApi.forAsset` is already scoped server-side
 * to what the calling viewer may see, so this panel is safe to render for any signed-in account: it
 * shows exactly what the API returns and nothing derived from a file path.
 *
 * An empty result and a search that failed after several attempts are deliberately not the same
 * screen. An empty list means Cinomni has never recorded a subtitle search for this file; a row
 * with state `NotFound` is a completed search that came up without an acceptable match. Collapsing
 * both into "no subtitles" would claim something the data does not say.
 */
export function SubtitlePanel({ assetId }: { assetId: string }) {
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['subtitles', 'asset', assetId],
    queryFn: () => subtitlesApi.forAsset(assetId),
  })

  return (
    <Card as="section" className="space-y-3">
      <h2 className="font-medium text-fg">Subtitles</h2>

      {isPending ? (
        <LoadingBlock size="sm" label="Loading subtitle searches" />
      ) : isError ? (
        <ErrorState
          title="Subtitle status could not be loaded"
          message={errorMessage(error)}
          onRetry={() => void refetch()}
        />
      ) : data.length === 0 ? (
        <EmptyState
          title="No subtitle search recorded"
          description="Cinomni has not searched for subtitles for this file yet — this is not the same as confirming none exist."
        />
      ) : (
        <>
          <DataTable
            columns={COLUMNS}
            rows={data}
            rowKey={(row) => row.id}
            caption="Subtitle searches for this file"
          />
          <p className="text-xs text-faint">
            One row per subtitle search Cinomni has run for this file. A "NotFound" state means the search
            completed without an acceptable match — it does not mean no subtitles exist anywhere.
          </p>
        </>
      )}
    </Card>
  )
}
