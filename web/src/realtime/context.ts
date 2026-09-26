import { createContext } from 'react'
import type { RealtimeStatus } from '@/lib/realtime'

/**
 * Whether the live stream is carrying updates right now. Pages read it to decide whether they still
 * need a refresh timer: while the stream is `live` nothing polls, and the moment it is not, the few
 * views that must stay current fall back to asking.
 */
export const RealtimeContext = createContext<RealtimeStatus>('connecting')
