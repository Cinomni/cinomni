import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { identityApi } from '@/api/endpoints'
import type { CurrentUser } from '@/api/types'
import { ApiError, SESSION_EXPIRED_EVENT } from '@/lib/api'
import { TOKEN_STORAGE_KEY, tokenStore } from '@/lib/token'
import { AuthContext, type AuthStatus, type LoginOutcome } from './context'

/**
 * Bootstraps and owns the session. On mount it resolves the current state: a stored token that still
 * validates → authenticated; otherwise the first-run setup gate decides between setup and the login
 * screen. A 401 from any request drops the session back to anonymous.
 *
 * Every session change clears the query cache: accounts differ in what they may see (their own requests,
 * operator-only data), so nothing one user fetched may survive into the next user's session in this tab.
 * That includes a change made in another tab: the token is shared by every tab of the origin, so a
 * sign-in or sign-out there is followed here instead of leaving this tab showing one account's data
 * while its requests carry another's token.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<AuthStatus>('loading')
  const [user, setUser] = useState<CurrentUser | null>(null)
  // Bumped by every session change, so a `/me` answer that arrives after the session moved on is dropped.
  const generation = useRef(0)

  const resolveAnonymous = useCallback(async () => {
    const current = generation.current
    let next: AuthStatus = 'anon'
    try {
      const { setupRequired } = await identityApi.setupRequired()
      if (setupRequired) next = 'setup'
    } catch {
      // The sign-in screen is the safe answer when the gate cannot be read.
    }
    // A sign-in, here or in another tab, may have landed while this was being asked.
    if (current === generation.current) setStatus(next)
  }, [])

  const loadSession = useCallback(async () => {
    const current = ++generation.current
    const token = tokenStore.get()
    if (!token) {
      await resolveAnonymous()
      return
    }
    try {
      const me = await identityApi.me()
      if (current !== generation.current) return
      setUser(me)
      setStatus('authed')
    } catch (error) {
      if (current !== generation.current) return
      // Only the server refusing the token ends the session. A 5xx, a proxy's error page or a network
      // drop says nothing about it, and signing out on those threw away a good session every time the
      // server was restarting while a tab reloaded.
      if (error instanceof ApiError && error.status === 401) {
        tokenStore.clearIfCurrent(token)
        await resolveAnonymous()
      } else {
        setStatus('unavailable')
      }
    }
  }, [resolveAnonymous])

  const retrySession = useCallback(async () => {
    setStatus('loading')
    await loadSession()
  }, [loadSession])

  useEffect(() => {
    void loadSession()
  }, [loadSession])

  useEffect(() => {
    const onExpired = () => {
      generation.current++
      setUser(null)
      queryClient.clear()
      void resolveAnonymous()
    }
    window.addEventListener(SESSION_EXPIRED_EVENT, onExpired)
    return () => window.removeEventListener(SESSION_EXPIRED_EVENT, onExpired)
  }, [queryClient, resolveAnonymous])

  useEffect(() => {
    const onStorage = (event: StorageEvent) => {
      // A null key is `localStorage.clear()`, which takes the token with it.
      if (event.key !== null && event.key !== TOKEN_STORAGE_KEY) return
      if (event.key !== null && event.newValue === event.oldValue) return
      setStatus('loading')
      setUser(null)
      queryClient.clear()
      void loadSession()
    }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
  }, [queryClient, loadSession])

  /**
   * Turns an issued token into a live session. Shared by both halves of the sign-in so a session
   * established through the second factor is indistinguishable from one established without it.
   */
  const establishSession = useCallback(
    async (token: string) => {
      const current = ++generation.current
      tokenStore.set(token)
      queryClient.clear()
      const me = await identityApi.me()
      // Another tab may have signed out or in while `/me` was answering; its session is the one now stored.
      if (current !== generation.current) return
      setUser(me)
      setStatus('authed')
    },
    [queryClient],
  )

  const login = useCallback(
    async (username: string, password: string): Promise<LoginOutcome> => {
      const response = await identityApi.login(username, password)
      // Discriminate on the flag the API always sends, never on whether a token happens to be there.
      if (response.twoFactorRequired) {
        return {
          twoFactorRequired: true,
          challenge: response.challenge,
          expiresInSeconds: response.expiresInSeconds,
        }
      }
      await establishSession(response.token)
      return { twoFactorRequired: false }
    },
    [establishSession],
  )

  const completeTwoFactor = useCallback(
    async (challenge: string, code: string) => {
      const { token } = await identityApi.loginTwoFactor(challenge, code)
      await establishSession(token)
    },
    [establishSession],
  )

  const setup = useCallback(
    // A newly created account cannot have a second factor yet, but this returns the outcome rather
    // than asserting that: the caller already handles both, and an assertion here would be a claim
    // about the server that this client is in no position to make.
    async (username: string, password: string): Promise<LoginOutcome> => {
      await identityApi.setup(username, password)
      return login(username, password)
    },
    [login],
  )

  /** Enrolling or disabling the second factor changes `/me` without changing the session. */
  const refreshUser = useCallback(async () => {
    const current = generation.current
    const me = await identityApi.me()
    if (current === generation.current) setUser(me)
  }, [])

  const logout = useCallback(async () => {
    const token = tokenStore.get()
    const current = ++generation.current
    try {
      await identityApi.logout()
    } catch {
      // Revoking is best-effort; drop the local session regardless.
    }
    // Only the token this tab revoked: another tab may have signed in while the revoke was answering.
    if (token) tokenStore.clearIfCurrent(token)
    if (current !== generation.current) return
    setUser(null)
    queryClient.clear()
    setStatus('anon')
  }, [queryClient])

  return (
    <AuthContext.Provider value={{ status, user, login, completeTwoFactor, setup, logout, refreshUser, retrySession }}>
      {children}
    </AuthContext.Provider>
  )
}
