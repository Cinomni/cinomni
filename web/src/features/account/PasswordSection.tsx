import { useState, type FormEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { identityApi } from '@/api/endpoints'
import { ApiError, errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { TextField } from '@/ui/TextField'

/** The server's floor. Repeated here only so the browser can say so before a round trip. */
const MINIMUM_LENGTH = 8

/**
 * A 401 from this route is the current password being wrong, not the session ending: the server
 * answers it inside the error envelope, which is what keeps the client from signing you out. Its
 * wording is the sign-in's, so this screen says which field it means instead.
 */
function describe(error: unknown): string {
  if (error instanceof ApiError && error.status === 401) {
    return 'Your current password is not correct.'
  }
  return errorMessage(error, 'Your password could not be changed.')
}

/**
 * Changing this account's own password. The current one is asked for again, so a session left open
 * on someone else's screen is not enough to lock the owner out; and every other session ends with the
 * change, which is said up front rather than discovered on the other device.
 */
export function PasswordSection() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [mismatch, setMismatch] = useState(false)

  const change = useMutation({
    mutationFn: () => identityApi.changePassword(current, next),
    onSuccess: () => {
      setCurrent('')
      setNext('')
      setConfirmation('')
    },
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    // Only a typing check: the server never sees the confirmation and does not need to.
    if (next !== confirmation) {
      setMismatch(true)
      return
    }
    setMismatch(false)
    change.mutate()
  }

  return (
    <Card as="section" className="space-y-3">
      <h2 className="font-medium text-fg">Password</h2>
      <p className="text-sm text-muted">
        Changing it signs you out everywhere else. This device stays signed in.
      </p>

      <form onSubmit={submit} className="flex max-w-sm flex-col gap-4">
        <TextField
          label="Current password"
          type="password"
          autoComplete="current-password"
          required
          value={current}
          onChange={(event) => setCurrent(event.target.value)}
        />
        <TextField
          label="New password"
          type="password"
          autoComplete="new-password"
          required
          minLength={MINIMUM_LENGTH}
          hint={`At least ${MINIMUM_LENGTH} characters.`}
          value={next}
          onChange={(event) => setNext(event.target.value)}
        />
        <TextField
          label="Confirm new password"
          type="password"
          autoComplete="new-password"
          required
          value={confirmation}
          onChange={(event) => setConfirmation(event.target.value)}
        />

        {mismatch && <Alert tone="danger">The new password and its confirmation do not match.</Alert>}
        {change.isError && <Alert tone="danger">{describe(change.error)}</Alert>}
        {change.isSuccess && (
          <Alert tone="success">Your password was changed. Any other devices were signed out.</Alert>
        )}

        <div>
          <Button type="submit" loading={change.isPending}>
            Change password
          </Button>
        </div>
      </form>
    </Card>
  )
}
