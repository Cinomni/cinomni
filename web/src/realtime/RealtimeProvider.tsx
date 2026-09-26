import { useEffect, useState, type ReactNode } from 'react'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import type { DownloadTaskSummary } from '@/api/types'
import { isRereadable } from '@/lib/queryClient'
import { openRealtimeStream, type RealtimeMessage, type RealtimeStatus } from '@/lib/realtime'
import { RealtimeContext } from './context'

/**
 * Query keys a topic makes stale. Prefix matches, so `['works']` covers `['works', collectionId]`.
 * The server's signals name no content — the client answers them by re-reading the REST endpoints,
 * which are what decide who may see what.
 */
const INVALIDATED_BY: Record<string, readonly (readonly string[])[]> = {
  downloads: [['downloads']],
  activity: [['intents']],
  // An import changes the library, the assets under a work (a movie's and every series' subtree, which
  // holds its seasons, episodes and files), whether a target is still missing, the calendar's "have it"
  // marks, the operator's import list, and the subtitles an import brought along. Each entry must be the
  // first element of a key some query really uses — a prefix nothing starts with invalidates nothing,
  // silently.
  library: [
    ['works'], ['work'], ['assets'], ['asset'], ['target'], ['series'], ['monitoring'], ['imports'], ['subtitles'],
  ],
  notifications: [['notifications']],
  requests: [['requests']],
}

/**
 * Holds the live stream open for the signed-in session and turns what arrives into cache updates.
 *
 * Two kinds of message arrive. Most are pure invalidations: the server says a topic changed and the
 * client re-reads it, so nothing about another account's content can leak through the stream. The
 * download progress snapshot is the exception — it carries the data, because replacing a poll with a
 * push that triggers a fetch would not have replaced anything.
 */
export function RealtimeProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<RealtimeStatus>('connecting')

  useEffect(
    () =>
      openRealtimeStream({
        onMessage: (message) => apply(queryClient, message),
        onStatus: setStatus,
        // Signals raised while the stream was down are gone; the only safe answer is to re-read —
        // everything that is a read. A query whose fetch opens something on the server is not one.
        onResync: () => void queryClient.invalidateQueries({ predicate: isRereadable }),
      }),
    [queryClient],
  )

  return <RealtimeContext.Provider value={status}>{children}</RealtimeContext.Provider>
}

function apply(queryClient: QueryClient, message: RealtimeMessage): void {
  if (message.topic === 'downloads.progress') {
    const tasks = asDownloadTasks(message.payload)
    if (tasks) {
      // Straight into the cache entry the REST call fills: the page re-renders and asks for nothing.
      queryClient.setQueryData(['downloads'], tasks)
    }
    return
  }

  for (const queryKey of INVALIDATED_BY[message.topic] ?? []) {
    void queryClient.invalidateQueries({ queryKey })
  }
}

/**
 * Narrows the one payload that carries data. Wire input is never trusted into the cache untyped: a
 * malformed frame must leave the last good snapshot in place rather than render as `undefined`.
 */
function asDownloadTasks(payload: unknown): DownloadTaskSummary[] | null {
  if (!Array.isArray(payload)) return null
  return payload.every(isDownloadTask) ? payload : null
}

function isDownloadTask(value: unknown): value is DownloadTaskSummary {
  if (typeof value !== 'object' || value === null) return false
  const task = value as Record<string, unknown>
  return (
    typeof task.id === 'string' &&
    typeof task.name === 'string' &&
    typeof task.state === 'string' &&
    typeof task.progress === 'number' &&
    // Activity walks these to place each transfer under its title; a frame without them is malformed.
    Array.isArray(task.intentIds) &&
    task.intentIds.every((id) => typeof id === 'string')
  )
}
