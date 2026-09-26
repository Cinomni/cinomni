import { tokenStore } from './token'

/** A failed API call, carrying the HTTP status and the server's error envelope when present. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/** Fired when a request comes back 401 with a token present — the session expired or was revoked. */
export const SESSION_EXPIRED_EVENT = 'cinomni:session-expired'

/**
 * Narrows a caught error into a message a user can read. TanStack Query and every `try`/`catch`
 * around `api` hand callers `unknown`, so every call site used to repeat this narrowing by hand —
 * one copy per page, each free to drift.
 *
 * `fallback` is what to say when the throw carries no message of its own, which is where a caller
 * knows more than this function can: "Could not add the indexer" beats "The request failed" when the
 * network died mid-submit. The narrowing itself belongs here either way.
 */
export function errorMessage(error: unknown, fallback = 'The request failed.'): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return fallback
}

interface ErrorEnvelope {
  error?: string
  message?: string
}

/**
 * Whether a body is the API's `{ error, message }` envelope — which is what tells an answer the
 * endpoint composed apart from a bare authentication challenge, whose body is empty.
 */
function isErrorEnvelope(data: unknown): boolean {
  return typeof data === 'object' && data !== null && typeof (data as ErrorEnvelope).error === 'string'
}

async function request<T>(
  method: string,
  path: string,
  body?: unknown,
  signal?: AbortSignal,
  tolerated?: readonly number[],
): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  const token = tokenStore.get()
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  let payload: string | undefined
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }

  const response = await fetch(path, { method, headers, body: payload, signal })

  if (response.status === 204) {
    return undefined as T
  }

  const text = await response.text()
  const data = parseBody(text)

  /*
   * A 401 means two different things on this API, and only one of them is about the session.
   *
   * The authentication layer challenges with an EMPTY body when the bearer token is missing, expired
   * or revoked — that is the session being over. An endpoint that checked a credential the caller
   * just typed answers 401 WITH the error envelope (`identity.invalid_credentials` when confirming a
   * password to change the second factor, `identity.invalid_two_factor_code` for a code), and that
   * says nothing about whether the session is still good.
   *
   * Treating both as expiry signed people out for mistyping — in the two-factor flow above all,
   * where every wrong code ended the session and threw away the enrolment they were halfway through.
   */
  // Only when the rejected token is still the stored one: another tab may have signed in since this
  // request left, and its session is not the one that ended.
  if (response.status === 401 && token && !isErrorEnvelope(data) && tokenStore.clearIfCurrent(token)) {
    window.dispatchEvent(new Event(SESSION_EXPIRED_EVENT))
  }

  if (!response.ok && !tolerated?.includes(response.status)) {
    const envelope = (isErrorEnvelope(data) ? data : {}) as ErrorEnvelope
    throw new ApiError(response.status, envelope.error ?? String(response.status), envelope.message ?? response.statusText)
  }

  // A success whose body is not JSON — a proxy's or captive portal's HTML page answering 200 — is not
  // the answer the caller typed it as, and handing it back as one fails far from here.
  if (data === UNREADABLE) {
    throw new ApiError(response.status, UNREADABLE_RESPONSE, 'The server sent an answer this app cannot read.')
  }

  return data as T
}

/** The code of an `ApiError` raised for a body that is not JSON. */
export const UNREADABLE_RESPONSE = 'client.unreadable_response'

const UNREADABLE: unique symbol = Symbol('unreadable')

/**
 * The body as JSON, or a marker when it is not. A reverse proxy answers a dead upstream with its own
 * HTML error page, and `JSON.parse` throwing a `SyntaxError` there meant the caller got no `ApiError`
 * and no status — the session bootstrap took it for a rejected token.
 */
function parseBody(text: string): unknown {
  if (!text) return undefined
  try {
    return JSON.parse(text) as unknown
  } catch {
    return UNREADABLE
  }
}

/**
 * A POST that outlives the page: `keepalive` lets it finish after the tab closes, which is what
 * `sendBeacon` was used for — but a beacon cannot carry a header, so its token rode in the URL, and a
 * URL ends up in proxy and access logs. Fire-and-forget: nobody is left to read the answer.
 */
export function postOnUnload(path: string): void {
  const headers: Record<string, string> = {}
  const token = tokenStore.get()
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }
  void fetch(path, { method: 'POST', headers, keepalive: true }).catch(() => undefined)
}

/** Build a media URL that authenticates via the access_token query param (for <video>/HLS requests). */
export function mediaUrl(path: string): string {
  const token = tokenStore.get()
  if (!token) {
    return path
  }
  const separator = path.includes('?') ? '&' : '?'
  return `${path}${separator}access_token=${encodeURIComponent(token)}`
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => request<T>('GET', path, undefined, signal),
  /**
   * A GET whose failure status carries the answer rather than replacing it. Readiness is the only
   * caller: it reports 503 precisely when a dependency is down, which is the moment an operator most
   * needs the body naming which one. Do not reach for this to paper over an error envelope.
   */
  getTolerating: <T>(path: string, tolerated: readonly number[], signal?: AbortSignal) =>
    request<T>('GET', path, undefined, signal, tolerated),
  post: <T>(path: string, body?: unknown, signal?: AbortSignal) => request<T>('POST', path, body, signal),
  put: <T>(path: string, body?: unknown, signal?: AbortSignal) => request<T>('PUT', path, body, signal),
  del: <T>(path: string, signal?: AbortSignal) => request<T>('DELETE', path, undefined, signal),
  /** A GET whose answer is text rather than JSON — a subtitle track. Authenticated by header, like the rest. */
  text: (path: string, signal?: AbortSignal) => requestText(path, signal),
}

async function requestText(path: string, signal?: AbortSignal): Promise<string> {
  const headers: Record<string, string> = { Accept: 'text/vtt, text/plain' }
  const token = tokenStore.get()
  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  const response = await fetch(path, { method: 'GET', headers, signal })
  // No endpoint answering text checks a credential of its own: a 401 here is the session ending.
  if (response.status === 401 && token && tokenStore.clearIfCurrent(token)) {
    window.dispatchEvent(new Event(SESSION_EXPIRED_EVENT))
  }
  if (!response.ok) {
    throw new ApiError(response.status, String(response.status), response.statusText || 'The request failed.')
  }
  return response.text()
}
