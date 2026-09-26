import { useEffect, useState, type FormEvent } from 'react'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { TextField } from '@/ui/TextField'

/** Seconds left until `deadline` (epoch milliseconds on this browser's clock), never negative. */
function secondsUntil(deadline: number): number {
  return Math.max(0, Math.ceil((deadline - Date.now()) / 1000))
}

function formatCountdown(seconds: number): string {
  const minutes = Math.floor(seconds / 60)
  return `${minutes}:${String(seconds % 60).padStart(2, '0')}`
}

/**
 * How long a challenge has left, ticking once a second.
 *
 * This is a courtesy, not a gate. The deadline is the lifetime the server gave, counted from when its
 * reply arrived, so a browser clock that disagrees with the server's cannot end the challenge early; it
 * only ever errs late by the reply's own travel time. At zero it sends the user back to a step that
 * definitely works. The server remains the only authority on whether a challenge is still good.
 */
function useCountdown(deadline: number, onLapsed: () => void): number {
  const [remaining, setRemaining] = useState(() => secondsUntil(deadline))

  useEffect(() => {
    setRemaining(secondsUntil(deadline))
    const timer = window.setInterval(() => {
      const left = secondsUntil(deadline)
      setRemaining(left)
      if (left === 0) onLapsed()
    }, 1000)
    return () => window.clearInterval(timer)
  }, [deadline, onLapsed])

  return remaining
}

/**
 * The second half of a sign-in: the password was right, and this redeems the challenge it bought.
 *
 * Every way this can fail comes back as one error — expired, already spent, wrong code, too many
 * wrong codes — because telling them apart would tell an attacker which half of the credential they
 * already have. That is the right call on the server and it leaves this screen unable to diagnose
 * anything, so the limits are stated up front, permanently, instead of being explained after the
 * fact. Someone who does not know a challenge dies after a few wrong codes will otherwise retype the
 * same code until they are locked into a loop with no way out but a page reload.
 */
export function TwoFactorStep({
  deadline,
  busy,
  error,
  onSubmit,
  onStartOver,
  onExpired,
}: {
  deadline: number
  busy: boolean
  error: string | null
  onSubmit: (code: string) => void
  onStartOver: () => void
  onExpired: () => void
}) {
  const [code, setCode] = useState('')
  const remaining = useCountdown(deadline, onExpired)

  function submit(event: FormEvent) {
    event.preventDefault()
    onSubmit(code.trim())
  }

  return (
    <form
      onSubmit={submit}
      className="flex flex-col gap-4 rounded-panel border border-line bg-surface p-6 shadow-[var(--shadow-lift)]"
    >
      <TextField
        label="Authentication code"
        // Not narrowed to six digits, and deliberately: the same field takes a recovery code, so a
        // numeric input mode or a length limit would quietly shut off the way back in.
        autoComplete="one-time-code"
        autoFocus
        required
        value={code}
        onChange={(event) => setCode(event.target.value)}
        hint="From your authenticator app, or one of your recovery codes."
      />

      <p className="text-xs text-faint">
        This sign-in request expires in {formatCountdown(remaining)}, and is discarded after several
        incorrect codes. If that happens, enter your password again to start a new one.
      </p>

      {error && <Alert tone="danger">{error}</Alert>}

      <Button type="submit" loading={busy} className="mt-1 w-full">
        Verify
      </Button>
      <Button type="button" variant="ghost" disabled={busy} onClick={onStartOver} className="w-full">
        Back to sign in
      </Button>
    </form>
  )
}
