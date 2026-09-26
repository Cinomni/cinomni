import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { catalogApi, identityApi } from '@/api/endpoints'
import type { UserAccount, UserPermissions, UserRole } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { errorMessage } from '@/lib/api'
import { formatDateTime } from '@/lib/format'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Checkbox } from '@/ui/Checkbox'
import { ErrorState } from '@/ui/ErrorState'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'
import { PlusIcon } from '@/ui/icons'
import { Spinner } from '@/ui/Spinner'
import { TextField } from '@/ui/TextField'
import { ContentCeilingField } from './ContentCeilingField'
import { OpenRequestLimitField } from './OpenRequestLimitField'

/** Household accounts: who can sign in, what they are, and what they may do. Administrators only. */
export function UsersPage() {
  const [inviting, setInviting] = useState(false)
  const { data, isPending, isError, error, refetch } = useQuery({ queryKey: ['users'], queryFn: identityApi.users })
  const ratings = useQuery({ queryKey: ['catalog', 'content-ratings'], queryFn: catalogApi.contentRatings })

  return (
    <section>
      <div className="mb-4 flex items-end justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold tracking-tight">Users</h2>
          <p className="mt-1 text-sm text-muted">
            Members request titles instead of adding them. Trust one and their requests skip the queue.
          </p>
        </div>
        <Button size="sm" icon={<PlusIcon className="size-4" />} onClick={() => setInviting(true)}>
          Add user
        </Button>
      </div>

      {isPending ? (
        <div className="grid place-items-center py-10">
          <Spinner className="size-6 text-accent" />
        </div>
      ) : isError ? (
        // Rendering an empty list here would read as "no accounts exist" instead of "the fetch failed" —
        // an administrator could otherwise believe accounts had been lost.
        <ErrorState message={errorMessage(error, 'Could not load users.')} onRetry={() => void refetch()} />
      ) : (
        <ul className="space-y-2">
          {data?.map((user) => (
            <UserRow
              key={user.id}
              user={user}
              certificates={ratings.data?.certificates ?? []}
              ladderReady={ratings.isSuccess}
            />
          ))}
        </ul>
      )}

      <AddUserModal open={inviting} onClose={() => setInviting(false)} />
    </section>
  )
}

function UserRow({
  user,
  certificates,
  ladderReady,
}: {
  user: UserAccount
  certificates: readonly string[]
  ladderReady: boolean
}) {
  const queryClient = useQueryClient()
  const { user: current, refreshUser } = useAuth()
  const [error, setError] = useState<string | null>(null)
  const [confirmingPromotion, setConfirmingPromotion] = useState(false)
  const [confirming, setConfirming] = useState<'disable' | 'demote-self' | null>(null)

  const isSelf = current?.id === user.id
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['users'] })
  const onError = (err: unknown) => setError(errorMessage(err, 'Could not update the account.'))

  const setRole = useMutation({
    mutationFn: (role: UserRole) => identityApi.setUserRole(user.id, role),
    onMutate: () => setError(null),
    onSuccess: async () => {
      setConfirmingPromotion(false)
      setConfirming(null)
      // Demoting yourself changes what this session may do: reload who is signed in, so the console
      // this page lives in closes instead of answering 403 to every click from here on. The demotion
      // has already landed, so a failed reload must not be reported as a failed demotion.
      if (isSelf) {
        try {
          await refreshUser()
        } catch {
          // The next request this tab makes finds out anyway; the server enforces the new role.
        }
      }
      await invalidate()
    },
    onError,
  })
  const setPermissions = useMutation({
    mutationFn: (permissions: UserPermissions) => identityApi.setUserPermissions(user.id, permissions),
    onMutate: () => setError(null),
    onSuccess: invalidate,
    onError,
  })
  const setDisabled = useMutation({
    mutationFn: (disabled: boolean) => identityApi.setUserDisabled(user.id, disabled),
    onMutate: () => setError(null),
    onSuccess: async () => {
      setConfirming(null)
      await invalidate()
    },
    onError,
  })

  const busy = setRole.isPending || setPermissions.isPending || setDisabled.isPending

  return (
    <Card as="li">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium text-fg">{user.username}</p>
            <Badge tone={user.isAdministrator ? 'accent' : 'neutral'}>{user.role}</Badge>
            {user.isDisabled && <Badge tone="warning">Disabled</Badge>}
            {isSelf && <Badge tone="info">You</Badge>}
          </div>
          <p className="mt-0.5 text-xs text-faint">
            Joined {formatDateTime(user.createdAt)}
            {user.lastLoginAt && ` · last seen ${formatDateTime(user.lastLoginAt)}`}
          </p>
        </div>

        <div className="flex shrink-0 items-center gap-2">
          <Button
            size="sm"
            variant="subtle"
            disabled={busy}
            onClick={() => {
              // Demoting another administrator is reversible by you and protected server-side (the
              // last administrator cannot be demoted), so it goes straight through. Demoting yourself
              // is not reversible by you — you lose this console the moment it lands — and promotion
              // hands over every operator surface, so both are confirmed first.
              if (!user.isAdministrator) {
                setConfirmingPromotion(true)
              } else if (isSelf) {
                setConfirming('demote-self')
              } else {
                setRole.mutate('Member')
              }
            }}
          >
            {user.isAdministrator ? 'Make member' : 'Make administrator'}
          </Button>
          <Button
            size="sm"
            variant={user.isDisabled ? 'subtle' : 'danger'}
            disabled={busy}
            // Enabling gives an account back; disabling signs its owner out everywhere, so it asks.
            onClick={() => (user.isDisabled ? setDisabled.mutate(false) : setConfirming('disable'))}
          >
            {user.isDisabled ? 'Enable' : 'Disable'}
          </Button>
        </div>
      </div>

      {/* An administrator has every permission implicitly, so there is nothing to toggle for them. */}
      {!user.isAdministrator && (
        <div className="mt-3 flex flex-wrap gap-4 border-t border-line pt-3">
          <PermissionToggle
            label="Can request titles"
            checked={user.permissions.canRequest}
            disabled={busy}
            onChange={(canRequest) =>
              setPermissions.mutate({ ...editable(user.permissions), canRequest })
            }
          />
          <PermissionToggle
            label="Requests skip approval"
            checked={user.permissions.requestsAutoApproved}
            disabled={busy}
            onChange={(requestsAutoApproved) =>
              setPermissions.mutate({ ...editable(user.permissions), requestsAutoApproved })
            }
          />
          {/* Only meaningful while the account may request at all; a cap on an account that cannot
              ask for anything is a number with nothing to count. */}
          {user.permissions.canRequest && (
            <OpenRequestLimitField
              value={user.permissions.openRequestLimit}
              disabled={busy}
              onApply={(openRequestLimit) =>
                setPermissions.mutate({ ...editable(user.permissions), openRequestLimit })
              }
            />
          )}
          {ladderReady && (
            <ContentCeilingField
              value={user.permissions.contentCeiling ?? null}
              region={user.permissions.contentCeilingRegion ?? null}
              applies={user.permissions.contentCeilingApplies ?? false}
              certificates={certificates}
              disabled={busy}
              onApply={(contentCeiling) =>
                setPermissions.mutate({ ...editable(user.permissions), contentCeiling })
              }
            />
          )}
        </div>
      )}

      {error && (
        <Alert tone="danger" className="mt-2">
          {error}
        </Alert>
      )}

      <Modal
        open={confirmingPromotion}
        onClose={() => setConfirmingPromotion(false)}
        title={`Promote ${user.username} to administrator?`}
      >
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            Promoting <span className="font-medium text-fg">{user.username}</span> hands over every
            operator surface of this installation: indexers, downloads, imports, the console, and
            the ability to promote and demote other accounts.
          </p>

          {error && <Alert tone="danger">{error}</Alert>}

          <div className="mt-1 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => setConfirmingPromotion(false)}>
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={setRole.isPending}
              loading={setRole.isPending}
              onClick={() => setRole.mutate('Administrator')}
            >
              Promote to administrator
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        open={confirming !== null}
        onClose={() => setConfirming(null)}
        title={
          confirming === 'disable'
            ? isSelf
              ? 'Disable your own account?'
              : `Disable ${user.username}?`
            : 'Give up administrator access?'
        }
      >
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            {confirming === 'disable' && isSelf ? (
              <>
                You will be signed out now, on this device and every other, and cannot sign in again until
                another administrator enables the account.
              </>
            ) : confirming === 'disable' ? (
              <>
                <span className="font-medium text-fg">{user.username}</span> will be signed out on every
                device and cannot sign in until an administrator enables the account again.
              </>
            ) : (
              <>
                You become a member. This console closes for you, and only another administrator can make
                you one again.
              </>
            )}
          </p>

          {error && <Alert tone="danger">{error}</Alert>}

          <div className="mt-1 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => setConfirming(null)}>
              Cancel
            </Button>
            <Button
              variant="danger"
              loading={busy}
              disabled={busy}
              onClick={() => (confirming === 'disable' ? setDisabled.mutate(true) : setRole.mutate('Member'))}
            >
              {confirming === 'disable' ? 'Disable account' : 'Make me a member'}
            </Button>
          </div>
        </div>
      </Modal>
    </Card>
  )
}

/**
 * The settings the permissions endpoint takes, and nothing it reports back about them: the ceiling's
 * region and whether it applies are the server's reading, not something this form decides. The stored
 * ceiling does go back with every other change, and the server keeps it as it was when it is unchanged.
 */
function editable(permissions: UserPermissions): UserPermissions {
  return {
    canRequest: permissions.canRequest,
    requestsAutoApproved: permissions.requestsAutoApproved,
    openRequestLimit: permissions.openRequestLimit,
    contentCeiling: permissions.contentCeiling ?? null,
  }
}

function PermissionToggle({
  label,
  checked,
  disabled,
  onChange,
}: {
  label: string
  checked: boolean
  disabled: boolean
  onChange: (value: boolean) => void
}) {
  return <Checkbox label={label} checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
}

function AddUserModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState<UserRole>('Member')
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: () => identityApi.createUser({ username, password, role }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] })
      onClose()
      setUsername('')
      setPassword('')
      setRole('Member')
    },
    onError: (err) => setError(errorMessage(err, 'Could not create the account.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    create.mutate()
  }

  return (
    <Modal open={open} onClose={onClose} title="Add user">
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <TextField
          label="Username"
          required
          autoComplete="off"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          autoFocus
        />
        <TextField
          label="Password"
          type="password"
          required
          minLength={8}
          hint="At least 8 characters. Share it with them; they can sign in right away."
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <Select label="Role" value={role} onChange={(e) => setRole(e.target.value as UserRole)}>
          <option value="Member">Member — browses, watches and requests</option>
          <option value="Administrator">Administrator — operates the installation</option>
        </Select>

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={create.isPending}>
            Add user
          </Button>
        </div>
      </form>
    </Modal>
  )
}
