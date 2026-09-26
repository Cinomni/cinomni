import { SESSION_EXPIRED_EVENT } from './api'
import { tokenStore } from './token'

/** The address of the one live stream the API exposes. */
const STREAM_PATH = '/api/realtime/stream'

const RETRY_BASE_MS = 1_000
const RETRY_MAX_MS = 30_000

/**
 * Ceiling on a partial frame. The server writes small JSON frames; anything past this means the
 * response is not the stream we asked for, and buffering it further would only grow the leak.
 */
const MAX_FRAME_BYTES = 256 * 1024

/** One signal from the server. `payload` is deliberately `unknown`: it is wire data, narrow it. */
export interface RealtimeMessage {
  topic: string
  payload?: unknown
}

export type RealtimeStatus = 'connecting' | 'live' | 'offline'

export interface RealtimeStreamOptions {
  onMessage: (message: RealtimeMessage) => void
  onStatus?: (status: RealtimeStatus) => void
  /** Fired after a drop is repaired: whatever was missed while offline has to be re-read. */
  onResync?: () => void
}

/**
 * Opens the Server-Sent Events stream and keeps it open, reconnecting with backoff.
 *
 * It reads the response body itself rather than using `EventSource`, for one reason: `EventSource`
 * cannot set headers, so it would force the session token into the query string of a long-lived
 * request — the one place a bearer token is most likely to end up in a proxy log. `mediaUrl()` exists
 * for elements that genuinely cannot send a header; this is not one of them.
 *
 * @returns a function that closes the stream and stops reconnecting.
 */
export function openRealtimeStream({ onMessage, onStatus, onResync }: RealtimeStreamOptions): () => void {
  const controller = new AbortController()
  let stopped = false
  let attempt = 0
  let hadFailure = false
  let retryTimer: ReturnType<typeof setTimeout> | undefined

  const report = (status: RealtimeStatus) => {
    if (!stopped) onStatus?.(status)
  }

  async function connect(): Promise<void> {
    while (!stopped) {
      report('connecting')

      try {
        const token = tokenStore.get()
        if (!token) {
          // No session: there is nothing to authenticate with and no point retrying in a loop.
          report('offline')
          return
        }

        const response = await fetch(STREAM_PATH, {
          headers: { Accept: 'text/event-stream', Authorization: `Bearer ${token}` },
          signal: controller.signal,
        })

        if (response.status === 401) {
          // Another tab replaced the token since this one was sent: that session is not over, so
          // reconnect with it at once rather than signing it out.
          if (!tokenStore.clearIfCurrent(token)) continue
          window.dispatchEvent(new Event(SESSION_EXPIRED_EVENT))
          report('offline')
          return
        }

        if (!response.ok || !response.body) {
          throw new Error(`Realtime stream refused with ${response.status}`)
        }

        attempt = 0
        report('live')
        if (hadFailure) {
          hadFailure = false
          onResync?.()
        }

        await readFrames(response.body, onMessage)
      } catch {
        // Every failure mode lands here — refused, dropped mid-stream, offline — and they are all
        // answered the same way: back off and try again.
        if (stopped) return
      }

      hadFailure = true
      report('offline')
      await delay(backoffMs(attempt++))
    }
  }

  function delay(ms: number): Promise<void> {
    return new Promise((resolve) => {
      retryTimer = setTimeout(resolve, ms)
    })
  }

  void connect()

  return () => {
    stopped = true
    if (retryTimer) clearTimeout(retryTimer)
    controller.abort()
  }
}

/** Exponential backoff with a ceiling, so a server that is down is asked once every 30s, not constantly. */
function backoffMs(attempt: number): number {
  return Math.min(RETRY_BASE_MS * 2 ** attempt, RETRY_MAX_MS)
}

/** Reads the body until it ends, emitting one message per complete SSE frame. */
async function readFrames(
  body: ReadableStream<Uint8Array>,
  onMessage: (message: RealtimeMessage) => void,
): Promise<void> {
  const reader = body.getReader()
  // Streaming decode: a multi-byte character split across two chunks must not become a replacement
  // character in the middle of a JSON frame.
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) return
      if (value === undefined) continue

      buffer += decoder.decode(value, { stream: true }).replace(/\r/g, '')

      let boundary = buffer.indexOf('\n\n')
      while (boundary >= 0) {
        const frame = buffer.slice(0, boundary)
        buffer = buffer.slice(boundary + 2)
        const message = parseFrame(frame)
        if (message) onMessage(message)
        boundary = buffer.indexOf('\n\n')
      }

      if (buffer.length > MAX_FRAME_BYTES) {
        throw new Error('Realtime frame exceeded its size limit')
      }
    }
  } finally {
    reader.releaseLock()
  }
}

/**
 * One SSE frame to a message. Comment lines (`: ping`) are the heartbeat and carry nothing; anything
 * that is not a topic-bearing JSON object is dropped rather than guessed at.
 *
 * Exported for its own tests: it is the module's only pure function and the one place a malformed
 * frame could turn into a bad cache write.
 */
export function parseFrame(frame: string): RealtimeMessage | null {
  const data = frame
    .split('\n')
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice('data:'.length).trim())
    .join('\n')

  if (!data) return null

  try {
    const parsed: unknown = JSON.parse(data)
    if (typeof parsed !== 'object' || parsed === null) return null

    const { topic, payload } = parsed as { topic?: unknown; payload?: unknown }
    return typeof topic === 'string' ? { topic, payload } : null
  } catch {
    return null
  }
}
