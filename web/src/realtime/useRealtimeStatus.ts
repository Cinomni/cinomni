import { useContext } from 'react'
import type { RealtimeStatus } from '@/lib/realtime'
import { RealtimeContext } from './context'

/** Whether the live stream is currently delivering updates. */
export function useRealtimeStatus(): RealtimeStatus {
  return useContext(RealtimeContext)
}

/**
 * The refresh interval a view still needs. While the stream is live the answer is "none" — that is the
 * whole point of it. When it is not, the view falls back to asking, because a downloads or activity
 * page frozen on a stale frame is worse than a request every few seconds.
 */
export function useFallbackRefetchInterval(intervalMs = 5_000): number | false {
  return useRealtimeStatus() === 'live' ? false : intervalMs
}
