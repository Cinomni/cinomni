import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { requestsApi } from '@/api/endpoints'
import type { MediaRequest, MediaRequestStatus } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { PageHeader } from '@/components/PageHeader'
import { ApiError, errorMessage } from '@/lib/api'
import { formatRelative } from '@/lib/format'
import { requestTone } from '@/lib/status'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { InboxIcon } from '@/ui/icons'
import { Modal } from '@/ui/Modal'
import { Segmented } from '@/ui/Segmented'
import { Select } from '@/ui/Select'
import { Spinner } from '@/ui/Spinner'
import { TextField } from '@/ui/TextField'

type Filter = 'All' | MediaRequestStatus

const FILTERS: Filter[] = ['All', 'Pending', 'Approved', 'Available', 'Rejected']
const FILTER_VALUES: readonly string[] = FILTERS

/**
 * `requestsApi.list` takes a `limit` but no offset, so there is no way to page past it — asking
 * for more is the only lever this endpoint gives a caller. Each step is a fresh, larger fetch of
 * the same "most recent" window, not a second page appended to the first.
 */
const REQUEST_LIMIT_STEPS = [100, 250, 500, 1000] as const
const ALL_REQUESTERS = 'all'

function isFilter(value: string): value is Filter {
  return FILTER_VALUES.includes(value)
}

/** One entry per distinct requester seen in the current fetch, in the order first encountered. */
function requestersIn(requests: readonly MediaRequest[]): { id: string; username: string }[] {
  const byId = new Map<string, string>()
  for (const request of requests) {
    if (!byId.has(request.requestedByUserId)) {
      byId.set(request.requestedByUserId, request.requestedByUsername)
    }
  }
  return [...byId.entries()]
    .map(([id, username]) => ({ id, username }))
    .sort((a, b) => a.username.localeCompare(b.username))
}

export function RequestsPage() {
  const { user } = useAuth()
  const isAdmin = user?.isAdministrator ?? false
  const [filter, setFilter] = useState<Filter>(isAdmin ? 'Pending' : 'All')
  const [limit, setLimit] = useState<number>(REQUEST_LIMIT_STEPS[0])
  const [requesterId, setRequesterId] = useState<string>(ALL_REQUESTERS)

  // A different status filter is a different question, so the accumulated fetch window and any
  // requester pick from the previous question are not assumed to still apply.
  useEffect(() => {
    setLimit(REQUEST_LIMIT_STEPS[0])
    setRequesterId(ALL_REQUESTERS)
  }, [filter])

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['requests', filter, limit],
    queryFn: () => requestsApi.list(filter === 'All' ? undefined : filter, false, limit),
  })

  // A member's requests are already scoped server-side to their own, so a roster derived here
  // never shows more than that member could already see by reading the list — and a non-admin
  // never gets a control for it, since the accounts endpoint is never called from this page.
  const requesters = useMemo(() => (isAdmin && data ? requestersIn(data) : []), [isAdmin, data])
  const showRequesterFilter = isAdmin && requesters.length > 1

  const visibleRequests = useMemo(() => {
    if (!data) return []
    if (!isAdmin || requesterId === ALL_REQUESTERS) return data
    return data.filter((request) => request.requestedByUserId === requesterId)
  }, [data, isAdmin, requesterId])

  const atCap = data !== undefined && data.length === limit
  const nextLimit = REQUEST_LIMIT_STEPS.find((step) => step > limit)
  const selectedRequesterName = requesters.find((requester) => requester.id === requesterId)?.username

  return (
    <>
      <PageHeader
        title="Requests"
        subtitle={
          isAdmin
            ? 'Titles your household asked for. Approving one adds it to the library and starts the search.'
            : 'Titles you asked for, and where each one stands.'
        }
      />

      <div className="mb-6 flex flex-wrap items-end gap-4">
        <Segmented
          label="Filter requests by status"
          options={FILTERS.map((option) => ({ value: option, label: option }))}
          value={filter}
          onChange={(value) => {
            if (isFilter(value)) setFilter(value)
          }}
        />

        {showRequesterFilter && (
          <Select
            label="Filter by requester"
            className="w-full sm:w-56"
            value={requesterId}
            onChange={(event) => setRequesterId(event.target.value)}
          >
            <option value={ALL_REQUESTERS}>All requesters</option>
            {requesters.map((requester) => (
              <option key={requester.id} value={requester.id}>
                {requester.username}
              </option>
            ))}
          </Select>
        )}
      </div>

      {isPending ? (
        <div className="grid place-items-center py-20">
          <Spinner className="size-7 text-accent" />
        </div>
      ) : isError ? (
        <ErrorState message={errorMessage(error)} onRetry={() => void refetch()} />
      ) : data.length === 0 ? (
        <EmptyState
          icon={<InboxIcon className="size-9" />}
          title={filter === 'All' ? 'No requests yet' : `Nothing ${filter.toLowerCase()}`}
          description="Search for a title from Add movie and request it — an administrator decides from here."
        />
      ) : (
        <>
          {atCap && (
            <Alert tone="info" className="mb-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <span>
                  Showing the most recent {limit} request{limit === 1 ? '' : 's'} — there may be more.
                </span>
                {nextLimit !== undefined && (
                  <Button size="sm" variant="ghost" onClick={() => setLimit(nextLimit)}>
                    Show more (up to {nextLimit})
                  </Button>
                )}
              </div>
            </Alert>
          )}

          {visibleRequests.length === 0 ? (
            <EmptyState
              icon={<InboxIcon className="size-9" />}
              title={`Nothing from ${selectedRequesterName ?? 'that requester'}`}
              description="Within the requests currently loaded, this requester has none in this status. Clear the requester filter to see everyone."
            />
          ) : (
            <ul className="space-y-2">
              {visibleRequests.map((request) => (
                <RequestRow key={request.id} request={request} canDecide={isAdmin} />
              ))}
            </ul>
          )}
        </>
      )}
    </>
  )
}

function RequestRow({ request, canDecide }: { request: MediaRequest; canDecide: boolean }) {
  const queryClient = useQueryClient()
  const [rejecting, setRejecting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // The pending badge lives under the same ['requests'] prefix, so one invalidation refreshes both.
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['requests'] })

  const approve = useMutation({
    mutationFn: () => requestsApi.approve(request.id),
    onMutate: () => setError(null),
    onSuccess: invalidate,
    onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not approve the request.'),
  })

  const decidable = canDecide && request.status === 'Pending'

  return (
    <Card as="li" className="flex items-start gap-4">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <p className="font-medium text-fg">{request.title}</p>
          <span className="text-sm text-faint">{request.year ?? '—'}</span>
          <Badge tone={requestTone[request.status]}>{request.status}</Badge>
        </div>
        <p className="mt-1 text-sm text-muted">
          Requested by {request.requestedByUsername} · {formatRelative(request.requestedAt)}
        </p>
        {request.decisionNote && <p className="mt-1 text-sm text-muted">“{request.decisionNote}”</p>}
        {error && (
          <Alert tone="danger" className="mt-2">
            {error}
          </Alert>
        )}
      </div>

      <div className="flex shrink-0 flex-col items-end gap-2">
        {decidable && (
          <div className="flex gap-2">
            <Button size="sm" loading={approve.isPending} onClick={() => approve.mutate()}>
              Approve
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setRejecting(true)}>
              Reject
            </Button>
          </div>
        )}
        {request.workId && (
          <Link to={`/works/${request.workId}`} className="text-sm font-medium text-accent hover:underline">
            View title
          </Link>
        )}
      </div>

      <RejectModal request={request} open={rejecting} onClose={() => setRejecting(false)} onDecided={invalidate} />
    </Card>
  )
}

function RejectModal({
  request,
  open,
  onClose,
  onDecided,
}: {
  request: MediaRequest
  open: boolean
  onClose: () => void
  onDecided: () => Promise<unknown>
}) {
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)

  // A dialog reopened after a failed attempt starts clean.
  useEffect(() => {
    if (open) {
      setReason('')
      setError(null)
    }
  }, [open])

  const reject = useMutation({
    mutationFn: () => requestsApi.reject(request.id, reason.trim() || null),
    onMutate: () => setError(null),
    onSuccess: async () => {
      await onDecided()
      onClose()
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not reject the request.'),
  })

  return (
    <Modal open={open} onClose={onClose} title={`Reject “${request.title}”`}>
      <form
        onSubmit={(event) => {
          event.preventDefault()
          setError(null)
          reject.mutate()
        }}
        className="flex flex-col gap-4"
      >
        <TextField
          label="Reason (optional)"
          placeholder="Already own it on disc"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          autoFocus
        />
        <p className="text-sm text-muted">
          {request.requestedByUsername} keeps seeing the request, with the reason attached.
        </p>

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" variant="danger" loading={reject.isPending}>
            Reject
          </Button>
        </div>
      </form>
    </Modal>
  )
}
