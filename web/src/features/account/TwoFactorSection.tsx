import { useState, type FormEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { identityApi } from '@/api/endpoints'
import type { TwoFactorEnrollment } from '@/api/types'
import { ApiError, errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Modal } from '@/ui/Modal'
import { TextField } from '@/ui/TextField'
import { RecoveryCodes } from './RecoveryCodes'

/**
 * The one failure here that is not about what the person typed: the installation has no master key,
 * so it cannot encrypt a shared secret and no enrolment is possible on this server at all. Telling
 * someone to check their password would send them round a loop they cannot win.
 */
function isUnavailable(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'identity.two_factor_unavailable'
}

/**
 * The server drops a pending enrolment after too many wrong codes, so the key on screen is dead and
 * another code cannot succeed. The way on is the password again, not a retry.
 */
function isAbandoned(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'identity.two_factor_enrollment_abandoned'
}

function describe(error: unknown, refused: string): string {
  if (isUnavailable(error)) {
    return 'This server cannot set up two-factor authentication: it has no master key configured. That is a server setting, not something you can fix here — ask whoever runs this installation.'
  }
  // The API answers 401 here with the wording it uses for a sign-in — "Invalid username or
  // password" — which names a field this screen does not have and a step already behind you. The
  // caller's own sentence says what was actually refused, so it wins over the server's for this
  // status alone; every other failure still reports what the server said.
  if (error instanceof ApiError && error.status === 401) {
    return refused
  }
  return errorMessage(error, refused)
}

/** Where an enrolment has got to. It only becomes real at `codes`; everything before is reversible. */
type EnrollStep =
  | { name: 'idle' }
  | { name: 'password' }
  | { name: 'confirm'; enrollment: TwoFactorEnrollment }
  | { name: 'codes'; codes: string[] }

export function TwoFactorSection({
  enabled,
  onChanged,
}: {
  enabled: boolean
  /** Re-reads the account: enrolling and disabling both change what `/me` reports. */
  onChanged: () => Promise<void>
}) {
  const [step, setStep] = useState<EnrollStep>({ name: 'idle' })
  const [disabling, setDisabling] = useState(false)

  return (
    <Card as="section" className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-medium text-fg">Two-factor authentication</h2>
        <Badge tone={enabled ? 'success' : 'neutral'}>{enabled ? 'On' : 'Off'}</Badge>
      </div>

      <p className="text-sm text-muted">
        {enabled
          ? 'Signing in on a new device asks for a code from your authenticator app as well as your password.'
          : 'Adds a code from an authenticator app to your password when you sign in. Your password alone stops being enough.'}
      </p>

      {enabled ? (
        <>
          <Button type="button" variant="subtle" onClick={() => setDisabling(true)}>
            Turn off
          </Button>
          {disabling && (
            <DisableModal
              onClose={() => setDisabling(false)}
              onDisabled={async () => {
                setDisabling(false)
                await onChanged()
              }}
            />
          )}
        </>
      ) : (
        <Button type="button" variant="subtle" onClick={() => setStep({ name: 'password' })}>
          Set up
        </Button>
      )}

      {/* Outside the branch above on purpose: confirming re-reads the account while the recovery
          codes are showing, the re-read flips `enabled`, and a modal that lived in the "off" branch
          would be unmounted with codes nobody can ever see again. */}
      {step.name !== 'idle' && (
        <EnrollModal
          step={step}
          onStep={setStep}
          onClose={() => setStep({ name: 'idle' })}
          onEnabled={onChanged}
        />
      )}
    </Card>
  )
}

function EnrollModal({
  step,
  onStep,
  onClose,
  onEnabled,
}: {
  step: EnrollStep
  onStep: (step: EnrollStep) => void
  onClose: () => void
  onEnabled: () => Promise<void>
}) {
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')

  const begin = useMutation({
    mutationFn: () => identityApi.enrollTwoFactor(password),
    onSuccess: (enrollment) => {
      setPassword('')
      onStep({ name: 'confirm', enrollment })
    },
  })

  const confirm = useMutation({
    mutationFn: () => identityApi.confirmTwoFactor(code.trim()),
    onSuccess: async ({ recoveryCodes }) => {
      setCode('')
      onStep({ name: 'codes', codes: recoveryCodes })
      // The factor is on from here, so the account state is stale even though this modal is still up.
      await onEnabled()
    },
  })

  function submitPassword(event: FormEvent) {
    event.preventDefault()
    begin.mutate()
  }

  function submitCode(event: FormEvent) {
    event.preventDefault()
    confirm.mutate()
  }

  return (
    // Closing is refused once the codes are up: they cannot be shown again, and the backdrop is the
    // easiest thing in the world to click by accident.
    <Modal
      open
      onClose={step.name === 'codes' ? () => undefined : onClose}
      title={step.name === 'codes' ? 'Save your recovery codes' : 'Set up two-factor authentication'}
    >
      {step.name === 'password' && (
        <form onSubmit={submitPassword} className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            Confirm your password to begin. Turning the second factor on changes what protects this
            account, so a signed-in session is not enough on its own.
          </p>
          <TextField
            label="Password"
            type="password"
            autoComplete="current-password"
            autoFocus
            required
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
          {begin.isError && <Alert tone="danger">{describe(begin.error, 'That password was not accepted.')}</Alert>}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={begin.isPending} onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={begin.isPending}>
              Continue
            </Button>
          </div>
        </form>
      )}

      {step.name === 'confirm' && (
        <form onSubmit={submitCode} className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            Add this secret to your authenticator app, then enter the code it shows. Nothing changes
            until you do — this account still signs in exactly as it does now.
          </p>

          <div className="space-y-1">
            <p className="text-sm font-medium text-muted">Setup key</p>
            <p className="break-all rounded-control border border-line bg-elevated p-3 font-mono text-sm text-fg">
              {step.enrollment.secret}
            </p>
            <p className="text-xs text-faint">
              An app that accepts a setup link can use{' '}
              <span className="break-all font-mono">{step.enrollment.enrollmentUri}</span> instead.
            </p>
          </div>

          <TextField
            label="Code from your app"
            autoComplete="one-time-code"
            autoFocus
            required
            value={code}
            onChange={(event) => setCode(event.target.value)}
          />

          {confirm.isError &&
            (isAbandoned(confirm.error) ? (
              <Alert tone="danger">
                Too many wrong codes, so this setup was cancelled and the key above no longer works.{' '}
                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => {
                    // Or the next enrolment would open on this one's error.
                    confirm.reset()
                    setCode('')
                    onStep({ name: 'password' })
                  }}
                >
                  Start again
                </Button>
              </Alert>
            ) : (
              <Alert tone="danger">{describe(confirm.error, 'That code was not accepted.')}</Alert>
            ))}

          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={confirm.isPending} onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={confirm.isPending}>
              Turn on
            </Button>
          </div>
        </form>
      )}

      {step.name === 'codes' && <RecoveryCodes codes={step.codes} onDone={onClose} />}
    </Modal>
  )
}

function DisableModal({ onClose, onDisabled }: { onClose: () => void; onDisabled: () => Promise<void> }) {
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')

  const disable = useMutation({
    mutationFn: () => identityApi.disableTwoFactor(password, code.trim()),
    onSuccess: onDisabled,
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    disable.mutate()
  }

  return (
    <Modal open onClose={onClose} title="Turn off two-factor authentication">
      <form onSubmit={submit} className="flex flex-col gap-4">
        <p className="text-sm text-muted">
          Afterwards your password alone signs this account in.
        </p>
        <Alert tone="warning">
          This also signs you out everywhere else. Every other session on this account is revoked —
          only the one you are using now survives.
        </Alert>

        <TextField
          label="Password"
          type="password"
          autoComplete="current-password"
          autoFocus
          required
          value={password}
          onChange={(event) => setPassword(event.target.value)}
        />
        <TextField
          label="Authentication code"
          autoComplete="one-time-code"
          required
          value={code}
          onChange={(event) => setCode(event.target.value)}
          hint="From your authenticator app, or one of your recovery codes."
        />

        {disable.isError && (
          <Alert tone="danger">{describe(disable.error, 'That password and code were not accepted.')}</Alert>
        )}

        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" disabled={disable.isPending} onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" variant="danger" loading={disable.isPending}>
            Turn off
          </Button>
        </div>
      </form>
    </Modal>
  )
}
