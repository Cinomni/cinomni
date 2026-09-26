import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, SESSION_EXPIRED_EVENT, UNREADABLE_RESPONSE, api, postOnUnload } from './api'
import { tokenStore } from './token'

/** A fetch that answers once with the given status and body. */
function respondWith(status: number, body: string, contentType = 'application/json') {
  return vi.fn().mockResolvedValue(
    new Response(body || null, { status, headers: body ? { 'Content-Type': contentType } : {} }),
  )
}

let expired: number

beforeEach(() => {
  tokenStore.set('a-session-token')
  expired = 0
  window.addEventListener(SESSION_EXPIRED_EVENT, countExpiry)
})

afterEach(() => {
  window.removeEventListener(SESSION_EXPIRED_EVENT, countExpiry)
  tokenStore.clear()
  vi.unstubAllGlobals()
})

function countExpiry() {
  expired += 1
}

describe('api 401 handling', () => {
  it('api_ends_the_session_when_the_token_itself_was_rejected', async () => {
    // The authentication layer challenges with an empty body. That is the session being over, and
    // it is the only 401 that should cost the caller their session.
    vi.stubGlobal('fetch', respondWith(401, ''))

    await expect(api.get('/api/identity/me')).rejects.toBeInstanceOf(ApiError)

    expect(expired).toBe(1)
    expect(tokenStore.get()).toBeNull()
  })

  it('api_keeps_the_session_when_a_typed_credential_was_the_thing_rejected', async () => {
    // Confirming a password to enable two-factor answers 401 with the envelope when the password is
    // wrong. The session is untouched — and signing someone out for a typo, in the flow where they
    // are being most careful, threw away the enrolment they were halfway through.
    vi.stubGlobal(
      'fetch',
      respondWith(401, JSON.stringify({ error: 'identity.invalid_credentials', message: 'Invalid.' })),
    )

    await expect(api.post('/api/identity/two-factor/enroll', { password: 'wrong' })).rejects.toBeInstanceOf(
      ApiError,
    )

    expect(expired).toBe(0)
    expect(tokenStore.get()).toBe('a-session-token')
  })

  it('api_keeps_the_session_when_a_second_factor_code_was_rejected', async () => {
    // Same shape, and the one that would have bitten hardest: every mistyped authenticator code.
    vi.stubGlobal(
      'fetch',
      respondWith(401, JSON.stringify({ error: 'identity.invalid_two_factor_code', message: 'No.' })),
    )

    await expect(api.post('/api/identity/two-factor/confirm', { code: '000000' })).rejects.toBeInstanceOf(
      ApiError,
    )

    expect(expired).toBe(0)
    expect(tokenStore.get()).toBe('a-session-token')
  })

  it('api_still_reports_the_error_code_and_message_it_kept_the_session_for', async () => {
    // Keeping the session must not swallow the refusal: the screen still has to say what happened.
    vi.stubGlobal(
      'fetch',
      respondWith(401, JSON.stringify({ error: 'identity.invalid_two_factor_code', message: 'No.' })),
    )

    await expect(api.post('/api/identity/two-factor/confirm', { code: '000000' })).rejects.toMatchObject({
      status: 401,
      code: 'identity.invalid_two_factor_code',
      message: 'No.',
    })
  })
})

describe('postOnUnload', () => {
  it('postOnUnload_keeps_the_token_out_of_the_url_and_outlives_the_page', () => {
    // A URL ends up in proxy and access logs; the header does not. keepalive is what lets the request
    // finish after the tab has gone, which is the only reason this is not an ordinary api.post.
    const fetchMock = respondWith(204, '')
    vi.stubGlobal('fetch', fetchMock)

    postOnUnload('/api/playback/sessions/s-1/stop')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/playback/sessions/s-1/stop')
    expect(url).not.toContain('access_token')
    expect(init.method).toBe('POST')
    expect(init.keepalive).toBe(true)
    expect(init.headers).toEqual({ Authorization: 'Bearer a-session-token' })
  })
})

describe('api answers that are not JSON', () => {
  it('api_turns_a_proxys_html_error_page_into_an_api_error_with_its_status', async () => {
    // A reverse proxy answers a dead upstream with its own page. JSON.parse threw a SyntaxError there,
    // so the caller got no status, and the session bootstrap read it as a rejected token.
    vi.stubGlobal('fetch', respondWith(502, '<html><body>Bad Gateway</body></html>', 'text/html'))

    await expect(api.get('/api/identity/me')).rejects.toMatchObject({ status: 502, code: '502' })
    expect(tokenStore.get()).toBe('a-session-token')
  })

  it('api_refuses_a_success_whose_body_is_not_json', async () => {
    vi.stubGlobal('fetch', respondWith(200, '<html>captive portal</html>', 'text/html'))

    await expect(api.get('/api/identity/me')).rejects.toMatchObject({ code: UNREADABLE_RESPONSE })
  })
})

describe('api 401 on a token another tab replaced', () => {
  it('api_leaves_a_newer_session_alone_when_the_old_token_is_rejected', async () => {
    // Another tab signed out and back in while this request was in flight: the 401 is about the token
    // this request carried, not about the one now stored, which is the new session's.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(async () => {
        tokenStore.set('a-newer-token')
        return new Response(null, { status: 401 })
      }),
    )

    await expect(api.get('/api/works')).rejects.toBeInstanceOf(ApiError)

    expect(expired).toBe(0)
    expect(tokenStore.get()).toBe('a-newer-token')
  })
})
