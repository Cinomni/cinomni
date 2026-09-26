import { createContext } from 'react'
import type { CurrentUser } from '@/api/types'

/** `unavailable`: a token is stored but the server could not be asked about it. The session is kept. */
export type AuthStatus = 'loading' | 'setup' | 'anon' | 'authed' | 'unavailable'

/**
 * What a correct password produced. An account without a second factor is signed in by the time this
 * resolves; one with a second factor is halfway, and the caller has to redeem the challenge.
 *
 * `expiresInSeconds` is the challenge's own lifetime, not a session's — a duration, so it is counted
 * from when the reply arrived and never compared with the browser's clock.
 */
export type LoginOutcome =
  | { twoFactorRequired: false }
  | { twoFactorRequired: true; challenge: string; expiresInSeconds: number }

export interface AuthContextValue {
  status: AuthStatus
  user: CurrentUser | null
  /** Resolves to what the password bought: a session, or a challenge still to redeem. */
  login: (username: string, password: string) => Promise<LoginOutcome>
  /** Redeems a challenge with an authenticator code or a recovery code, completing the sign-in. */
  completeTwoFactor: (challenge: string, code: string) => Promise<void>
  setup: (username: string, password: string) => Promise<LoginOutcome>
  logout: () => Promise<void>
  /** Re-reads the current account. Enrolling or disabling the second factor changes what `/me` says. */
  refreshUser: () => Promise<void>
  /** Asks the server about the stored token again, after it could not be reached. */
  retrySession: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | null>(null)
