import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { catalogApi } from '@/api/endpoints'
import { errorMessage } from '@/lib/api'
import { formatDateTime } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'

const QUERY_KEY = ['catalog', 'import-list']

/**
 * What the trending list has already considered. Turning it on lives in Settings; this page only
 * shows the recorded decisions and can ask for another pass.
 */
export function ImportListPage() {
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: QUERY_KEY, queryFn: catalogApi.importList })
  const refresh = useMutation({
    mutationFn: catalogApi.refreshImportList,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUERY_KEY }),
  })

  return (
    <div className="space-y-4">
      <Card as="section" className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="font-medium text-fg">Trending list</h2>
            <p className="mt-1 text-sm text-muted">
              TMDB trending movies and series. Off until you enable it in Settings. A title it adds
              arrives with monitoring off; switch on the ones you want from their page. A title already
              in the catalog is recorded and not added again.
            </p>
          </div>
          <Button size="sm" loading={refresh.isPending} onClick={() => refresh.mutate()}>
            Run now
          </Button>
        </div>
        {list.isPending ? (
          <LoadingBlock size="md" label="Loading the trending list" />
        ) : list.isError ? (
          <ErrorState
            title="Trending list could not be loaded"
            message={errorMessage(list.error)}
            onRetry={() => void list.refetch()}
          />
        ) : (
          <>
            <Alert tone={list.data.enabled ? 'info' : 'warning'}>
              {list.data.enabled
                ? 'The scheduled job will add new trending titles.'
                : 'The list is off. Enable “Add trending titles” in Settings before a run will add anything.'}
            </Alert>
            {refresh.isError && <Alert tone="danger">{errorMessage(refresh.error)}</Alert>}
            {list.data.entries.length === 0 ? (
              <EmptyState title="Nothing recorded yet" description="A run records each title it considers." />
            ) : (
              <ul className="divide-y divide-line">
                {list.data.entries.map((entry) => (
                  <li key={entry.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                    <span>
                      <span className="font-medium text-fg">{entry.title}</span>
                      <span className="mt-0.5 block text-xs text-faint">
                        {entry.kind}
                        {entry.year ? ` · ${entry.year}` : ''} · {formatDateTime(entry.lastSeenAt)}
                      </span>
                    </span>
                    <Badge tone={entry.outcome === 'Added' ? 'success' : 'neutral'}>
                      {entry.outcome === 'Added' ? 'Added' : 'Already in catalog'}
                    </Badge>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </Card>
    </div>
  )
}
