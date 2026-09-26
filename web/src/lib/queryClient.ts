import { QueryClient, type Query } from '@tanstack/react-query'
import { ApiError } from './api'

declare module '@tanstack/react-query' {
  interface Register {
    queryMeta: {
      /**
       * Running this query's fetch changes something on the server — it opens a playback session and,
       * for a transcode, starts a conversion. Only an explicit refetch may run it again.
       */
      opensServerState?: true
    }
  }
}

/**
 * Whether a blanket invalidation may re-run a query. One that opens server state may not: re-running
 * it is not a re-read but a second session, and for the player that means the film starts over.
 */
export function isRereadable(query: Query): boolean {
  return query.meta?.opensServerState !== true
}

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      refetchOnWindowFocus: false,
      // Don't hammer the server on auth/not-found failures; retry transient errors once.
      retry: (failureCount, error) => {
        if (error instanceof ApiError && (error.status === 401 || error.status === 404)) return false
        return failureCount < 1
      },
    },
  },
})
