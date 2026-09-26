import { useState, type FormEvent } from 'react'
import { useAuth } from '@/auth/useAuth'
import { ApiError } from '@/lib/api'
import { SourceOffer } from '@/components/SourceOffer'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { ApertureMark } from '@/ui/Brand'
import { TextField } from '@/ui/TextField'
import { TwoFactorStep } from './TwoFactorStep'

/** A challenge waiting to be redeemed: the password was accepted, the sign-in is not finished. */
interface PendingChallenge {
  challenge: string
  /** When it lapses, on this browser's clock: the arrival time plus the lifetime the server gave. */
  deadline: number
}

/** First-run setup or returning-user login — one card, driven by the auth gate's mode. */
export function AuthPage({ mode }: { mode: 'setup' | 'login' }) {
  const { login, setup, completeTwoFactor } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [pending, setPending] = useState<PendingChallenge | null>(null)

  const isSetup = mode === 'setup'

  function describe(err: unknown, unauthorized: string): string {
    if (!(err instanceof ApiError)) return 'Something went wrong. Please try again.'
    return err.status === 401 ? unauthorized : err.message
  }

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const outcome = isSetup ? await setup(username, password) : await login(username, password)
      // A correct password on an account with a second factor is not a finished sign-in. Nothing is
      // authenticated yet, so the password is dropped here rather than kept for a retry.
      if (outcome.twoFactorRequired) {
        setPassword('')
        setPending({ challenge: outcome.challenge, deadline: Date.now() + outcome.expiresInSeconds * 1000 })
        setBusy(false)
      }
    } catch (err) {
      setError(describe(err, 'That username and password did not match.'))
      setBusy(false)
    }
  }

  async function onCode(code: string) {
    if (!pending) return
    setError(null)
    setBusy(true)
    try {
      await completeTwoFactor(pending.challenge, code)
    } catch (err) {
      // One error covers every cause on purpose, so this says what happened and not why. The step
      // itself carries the standing explanation of the limits.
      setError(describe(err, 'That code was not accepted.'))
      setBusy(false)
    }
  }

  function startOver(message: string | null) {
    setPending(null)
    setBusy(false)
    setError(message)
  }

  return (
    <div className="grid min-h-dvh place-items-center bg-bg px-4">
      <div className="w-full max-w-sm">
        <div className="mb-8 flex flex-col items-center gap-3 text-center">
          <ApertureMark className="size-12 text-accent" />
          <div>
            <h1 className="text-title font-bold tracking-brand">Cinomni</h1>
            <p className="mt-1 text-meta text-muted">
              {pending
                ? 'Enter the code from your authenticator to finish signing in.'
                : isSetup
                  ? 'Create the administrator account to begin.'
                  : 'Sign in to your library.'}
            </p>
          </div>
        </div>

        {pending ? (
          <TwoFactorStep
            deadline={pending.deadline}
            busy={busy}
            error={error}
            onSubmit={(code) => void onCode(code)}
            onStartOver={() => startOver(null)}
            onExpired={() => startOver('That sign-in request expired. Enter your password again.')}
          />
        ) : (
          <form
            onSubmit={onSubmit}
            className="flex flex-col gap-4 rounded-panel bg-surface p-6 ring-1 ring-line-soft"
          >
            <TextField
              label="Username"
              autoComplete="username"
              autoFocus
              required
              value={username}
              onChange={(e) => setUsername(e.target.value)}
            />
            <TextField
              label="Password"
              type="password"
              autoComplete={isSetup ? 'new-password' : 'current-password'}
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              hint={isSetup ? 'Use at least 8 characters.' : undefined}
            />

            {error && <Alert tone="danger">{error}</Alert>}

            <Button type="submit" loading={busy} className="mt-1 w-full">
              {isSetup ? 'Create account' : 'Sign in'}
            </Button>
          </form>
        )}
        <p className="mt-6 text-center text-xs text-faint"><SourceOffer /></p>
      </div>
    </div>
  )
}
