import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { catalogApi, identityApi } from '@/api/endpoints'
import type { Collection, CollectionAccessMode, CollectionKind } from '@/api/types'
import { errorMessage } from '@/lib/api'
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

/**
 * Collections and who may browse them. Administrators only — and the API enforces that regardless of
 * what this page renders.
 */
export function CollectionsPage() {
  const [creating, setCreating] = useState(false)
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['collections'],
    queryFn: catalogApi.collections,
  })

  return (
    <section>
      <div className="mb-4 flex items-end justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold tracking-tight">Collections</h2>
          <p className="mt-1 text-sm text-muted">
            Shelves your titles sit on. An open one is visible to everyone; a restricted one only to the
            accounts you grant.
          </p>
        </div>
        <Button size="sm" icon={<PlusIcon className="size-4" />} onClick={() => setCreating(true)}>
          Add collection
        </Button>
      </div>

      {isPending ? (
        <div className="grid place-items-center py-10">
          <Spinner className="size-6 text-accent" />
        </div>
      ) : isError ? (
        // An empty list here previously read as "no collections exist" instead of "the fetch failed".
        <ErrorState message={errorMessage(error, 'Could not load collections.')} onRetry={() => void refetch()} />
      ) : (
        <ul className="space-y-2">
          {data?.map((collection) => (
            <CollectionRow key={collection.id} collection={collection} />
          ))}
        </ul>
      )}

      <AddCollectionModal open={creating} onClose={() => setCreating(false)} />
    </section>
  )
}

function CollectionRow({ collection }: { collection: Collection }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const restricted = collection.accessMode === 'Restricted'

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['collections'] })
  const onError = (err: unknown) => setError(errorMessage(err, 'Could not update the collection.'))

  const setMode = useMutation({
    mutationFn: (mode: CollectionAccessMode) => catalogApi.setCollectionAccessMode(collection.id, mode),
    onMutate: () => setError(null),
    onSuccess: invalidate,
    onError,
  })

  return (
    <Card as="li">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium text-fg">{collection.name}</p>
            <Badge tone="neutral">{collection.kind}</Badge>
            <Badge tone={restricted ? 'warning' : 'success'}>{restricted ? 'Restricted' : 'Open'}</Badge>
            {collection.isDefault && <Badge tone="accent">Default</Badge>}
          </div>
          <p className="mt-0.5 text-xs text-faint">
            {collection.workCount} {collection.workCount === 1 ? 'title' : 'titles'}
          </p>
        </div>

        <Button
          size="sm"
          variant="subtle"
          loading={setMode.isPending}
          onClick={() => setMode.mutate(restricted ? 'Open' : 'Restricted')}
        >
          {restricted ? 'Make open' : 'Restrict'}
        </Button>
      </div>

      {restricted && <GrantList collectionId={collection.id} />}

      {error && (
        <Alert tone="danger" className="mt-2">
          {error}
        </Alert>
      )}
    </Card>
  )
}

function GrantList({ collectionId }: { collectionId: string }) {
  const queryClient = useQueryClient()
  const {
    data: users,
    isError: usersError,
    error: usersErrorValue,
    refetch: refetchUsers,
  } = useQuery({ queryKey: ['users'], queryFn: identityApi.users })
  const grants = useQuery({
    queryKey: ['collections', collectionId, 'grants'],
    queryFn: () => catalogApi.collectionGrants(collectionId),
  })
  const granted = grants.data

  const toggle = useMutation({
    mutationFn: ({ userId, grant }: { userId: string; grant: boolean }) =>
      grant ? catalogApi.grantCollection(collectionId, userId) : catalogApi.revokeCollection(collectionId, userId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['collections', collectionId, 'grants'] }),
  })

  // Administrators see every collection anyway, so granting one to them would be meaningless.
  const members = users?.filter((user) => !user.isAdministrator) ?? []

  return (
    <div className="mt-3 border-t border-line pt-3">
      {usersError ? (
        // The grant list otherwise renders exactly like "no member accounts" when this fetch fails,
        // which would read as an empty household instead of a failed request.
        <ErrorState
          message={errorMessage(usersErrorValue, 'Could not load member accounts.')}
          onRetry={() => void refetchUsers()}
        />
      ) : grants.isError ? (
        // Unchecked boxes over a failed read would say nobody has access, which may be untrue.
        <ErrorState
          message={errorMessage(grants.error, 'Could not load who has access.')}
          onRetry={() => void grants.refetch()}
        />
      ) : members.length === 0 ? (
        <p className="text-sm text-muted">No member accounts yet — add one under Users.</p>
      ) : (
        <div className="flex flex-wrap gap-4">
          {members.map((member) => (
            <Checkbox
              key={member.id}
              label={member.username}
              checked={granted?.includes(member.id) ?? false}
              disabled={toggle.isPending}
              onChange={(e) => toggle.mutate({ userId: member.id, grant: e.target.checked })}
            />
          ))}
        </div>
      )}
      {toggle.isError && (
        <Alert tone="danger" className="mt-2">
          {errorMessage(toggle.error, 'Could not change who has access. Nothing was changed.')}
        </Alert>
      )}
    </div>
  )
}

function AddCollectionModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [kind, setKind] = useState<CollectionKind>('Movies')
  const [accessMode, setAccessMode] = useState<CollectionAccessMode>('Open')
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: () => catalogApi.createCollection({ name, kind, accessMode }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['collections'] })
      onClose()
      setName('')
      setAccessMode('Open')
    },
    onError: (err) => setError(errorMessage(err, 'Could not create the collection.')),
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    create.mutate()
  }

  return (
    <Modal open={open} onClose={onClose} title="Add collection">
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <TextField label="Name" required value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        <div className="grid grid-cols-2 gap-3">
          <Select label="Holds" value={kind} onChange={(e) => setKind(e.target.value as CollectionKind)}>
            <option value="Movies">Movies</option>
            <option value="Series">Series</option>
            <option value="Mixed">Both</option>
          </Select>
          <Select
            label="Access"
            value={accessMode}
            onChange={(e) => setAccessMode(e.target.value as CollectionAccessMode)}
          >
            <option value="Open">Open to everyone</option>
            <option value="Restricted">Only who I grant</option>
          </Select>
        </div>

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={create.isPending}>
            Add collection
          </Button>
        </div>
      </form>
    </Modal>
  )
}
