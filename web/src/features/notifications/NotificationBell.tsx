import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { catalogApi, notificationsApi } from '@/api/endpoints'
import type { AppNotification, NotificationSeverity } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatRelative } from '@/lib/format'
import { workPath } from '@/lib/routes'
import { useFallbackRefetchInterval } from '@/realtime/useRealtimeStatus'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { BellIcon } from '@/ui/icons'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'

const DOT: Record<NotificationSeverity, string> = {
  Info: 'bg-info',
  Success: 'bg-success',
  Warning: 'bg-warning',
  Error: 'bg-danger',
}

export function NotificationBell({ className }: { className?: string }) {
  const [open, setOpen] = useState(false)
  // A raised notification pushes this count; the interval only covers a stream that is down.
  const { data: unread } = useQuery({
    queryKey: ['notifications', 'unread-count'],
    queryFn: notificationsApi.unreadCount,
    refetchInterval: useFallbackRefetchInterval(20_000),
  })
  const count = unread?.count ?? 0

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        aria-label={count > 0 ? `Notifications, ${count} unread` : 'Notifications'}
        className={cn('relative rounded-control p-2 text-muted transition-colors hover:bg-elevated hover:text-fg', className)}
      >
        <BellIcon className="size-5" />
        {count > 0 && (
          <span className="absolute right-1 top-1 grid min-w-4 place-items-center rounded-full bg-accent px-1 text-[10px] font-semibold text-on-accent">
            {count > 99 ? '99+' : count}
          </span>
        )}
      </button>

      <NotificationPanel open={open} onClose={() => setOpen(false)} />
    </>
  )
}

function NotificationPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()

  const notifications = useQuery({
    queryKey: ['notifications', 'list'],
    queryFn: () => notificationsApi.list(false, 50),
    enabled: open,
  })

  // A notification carries only a work id, and a series has its own detail page. This shares the
  // library's cache entry, so opening the panel usually costs nothing.
  const { data: works } = useQuery({
    queryKey: ['works', null],
    queryFn: () => catalogApi.list(),
    enabled: open,
  })

  const invalidate = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['notifications', 'list'] }),
      queryClient.invalidateQueries({ queryKey: ['notifications', 'unread-count'] }),
    ])

  const markRead = useMutation({ mutationFn: notificationsApi.markRead, onSuccess: invalidate })
  const markAll = useMutation({ mutationFn: notificationsApi.markAllRead, onSuccess: invalidate })

  const hasUnread = notifications.data?.some((n) => !n.read) ?? false

  function onOpen(notification: AppNotification) {
    if (!notification.read) markRead.mutate(notification.id)
    if (notification.workId) {
      const work = works?.find((w) => w.id === notification.workId)
      onClose()
      navigate(workPath(work ?? { id: notification.workId, kind: 'Movie' }))
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Notifications">
      {hasUnread && (
        <div className="mb-3 flex justify-end">
          <Button size="sm" variant="ghost" loading={markAll.isPending} onClick={() => markAll.mutate()}>
            Mark all read
          </Button>
        </div>
      )}

      {/* Marking read is a write against the same inbox that is on screen. Failing it silently would
          leave the row looking unread with no reason given, and the next click would look ignored. */}
      {markAll.isError && (
        <Alert tone="danger" className="mb-3">
          {errorMessage(markAll.error, 'These notifications could not be marked as read.')}
        </Alert>
      )}
      {markRead.isError && (
        <Alert tone="danger" className="mb-3">
          {errorMessage(markRead.error, 'That notification could not be marked as read.')}
        </Alert>
      )}

      {notifications.isPending ? (
        <LoadingBlock label="Loading notifications" />
      ) : notifications.isError ? (
        // An unreachable inbox is not an empty one. "You're all caught up" over a failed read tells
        // the household everything is fine at precisely the moment it is not.
        <ErrorState
          title="Notifications could not be loaded"
          message={errorMessage(notifications.error)}
          onRetry={() => void notifications.refetch()}
        />
      ) : notifications.data.length === 0 ? (
        <EmptyState
          icon={<BellIcon className="size-8" />}
          title="No notifications"
          description="You’re all caught up. New activity shows up here."
        />
      ) : (
        <ul className="-mx-1 max-h-[55vh] divide-y divide-line overflow-y-auto">
          {notifications.data.map((notification) => (
            <li key={notification.id}>
              <button
                onClick={() => onOpen(notification)}
                className={cn(
                  'flex w-full items-start gap-3 px-2 py-3 text-left transition-colors hover:bg-elevated',
                  notification.workId && 'cursor-pointer',
                )}
              >
                <span className={cn('mt-1.5 size-2 shrink-0 rounded-full', DOT[notification.severity])} />
                <span className="min-w-0 flex-1">
                  <span className="flex items-center justify-between gap-2">
                    <span className={cn('truncate text-sm', notification.read ? 'text-muted' : 'font-medium text-fg')}>
                      {notification.title}
                    </span>
                    <span className="shrink-0 text-xs text-faint">{formatRelative(notification.createdAt)}</span>
                  </span>
                  {notification.body && <span className="mt-0.5 block text-sm text-muted">{notification.body}</span>}
                </span>
                {!notification.read && <span className="mt-1.5 size-2 shrink-0 rounded-full bg-accent" />}
              </button>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  )
}
