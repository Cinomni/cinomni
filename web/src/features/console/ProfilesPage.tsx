import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { decisionApi } from '@/api/endpoints'
import type { AcquisitionProfile } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { Alert } from '@/ui/Alert'
import { Badge } from '@/ui/Badge'
import { Button } from '@/ui/Button'
import { Card } from '@/ui/Card'
import { Checkbox } from '@/ui/Checkbox'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Modal } from '@/ui/Modal'
import { SettingsIcon } from '@/ui/icons'
import { TextField } from '@/ui/TextField'

/**
 * Acquisition profiles: where a title stops being searched for, and whether a release already on
 * disk may still be replaced by a better one. `minFormatScore` and everything else a profile applies
 * — the allowed quality set, custom formats — is fixed at seed time; the API only accepts writes to
 * the cutoff rank and the upgrades switch, so this page only ever presents and edits those two.
 */
export function ProfilesPage() {
  const [editing, setEditing] = useState<AcquisitionProfile | null>(null)
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['decision', 'profiles'],
    queryFn: decisionApi.profiles,
  })

  return (
    <div className="space-y-4">
      <Card as="section" className="space-y-2">
        <h2 className="font-medium text-fg">How the cutoff decides</h2>
        <p className="text-sm text-muted">
          The cutoff rank is the quality a title is good enough at. Once what is on disk reaches it,
          nothing further is searched for that title and no other candidate is accepted — changing the
          cutoff is deciding when the system stops trying. Below the cutoff, a new candidate must beat
          what is already on disk to be accepted; merely matching it is only a temporary rejection,
          since a release that would beat it may simply not be posted yet.
        </p>
        <p className="text-sm text-muted">
          Upgrades start switched off on a library that predates them: replacing a file already working
          on disk is its owner&apos;s call, not one this installation makes for them.
        </p>
      </Card>

      {isPending ? (
        <LoadingBlock label="Loading profiles" />
      ) : isError ? (
        <ErrorState
          title="Profiles could not be loaded"
          message={errorMessage(error, 'The request failed.')}
          onRetry={() => void refetch()}
        />
      ) : data.length === 0 ? (
        <EmptyState
          icon={<SettingsIcon className="size-9" />}
          title="No acquisition profiles"
          description="Profiles are seeded when the installation is set up. None exist on this one yet."
        />
      ) : (
        <ul className="space-y-2">
          {data.map((profile) => (
            <Card key={profile.id} as="li" className="flex flex-wrap items-center justify-between gap-4">
              <div className="min-w-0">
                <p className="font-medium text-fg">{profile.name}</p>
                <dl className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-sm">
                  <div className="flex gap-1.5">
                    <dt className="text-faint">Minimum format score</dt>
                    <dd className="text-fg">{profile.minFormatScore}</dd>
                  </div>
                  <div className="flex gap-1.5">
                    <dt className="text-faint">Cutoff rank</dt>
                    <dd className="text-fg">{profile.cutoffRank}</dd>
                  </div>
                </dl>
              </div>

              <div className="flex shrink-0 items-center gap-3">
                <Badge tone={profile.upgradesAllowed ? 'success' : 'neutral'}>
                  {profile.upgradesAllowed ? 'Upgrades on' : 'Upgrades off'}
                </Badge>
                <Button size="sm" variant="subtle" onClick={() => setEditing(profile)}>
                  Edit
                </Button>
              </div>
            </Card>
          ))}
        </ul>
      )}

      {editing && <EditProfileModal profile={editing} onClose={() => setEditing(null)} />}
      <BlockedReleases />
    </div>
  )
}

function BlockedReleases() {
  const queryClient = useQueryClient()
  const [pendingId, setPendingId] = useState<string | null>(null)
  const blocks = useQuery({
    queryKey: ['decision', 'blocks'],
    queryFn: decisionApi.blocks,
  })
  const unblock = useMutation({
    mutationFn: (id: string) => decisionApi.unblock(id),
    onMutate: (id) => setPendingId(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['decision', 'blocks'] })
    },
    onSettled: () => setPendingId(null),
  })

  return (
    <section className="space-y-2">
      <div>
        <h2 className="font-medium text-fg">Blocked releases</h2>
        <p className="mt-1 text-sm text-muted">
          A blocked release is not taken by the automatic search. Grabbing it stays refused until the block is
          lifted. The reason is kept even after the evaluation that prompted it is aged out.
        </p>
      </div>
      {blocks.isPending ? (
        <LoadingBlock label="Loading blocked releases" />
      ) : blocks.isError ? (
        <ErrorState
          title="Blocked releases could not be loaded"
          message={errorMessage(blocks.error, 'The request failed.')}
          onRetry={() => void blocks.refetch()}
        />
      ) : blocks.data.blocks.length === 0 ? (
        <EmptyState
          title="Nothing blocked"
          description="Block a release from an interactive search when the sweep should leave it alone."
        />
      ) : (
        <ul className="space-y-2">
          {blocks.data.blocks.map((block) => (
            <Card key={block.id} as="li" className="flex flex-wrap items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="break-words font-mono text-xs text-fg">{block.releaseTitle}</p>
                <p className="mt-1 text-sm text-muted">{block.reason}</p>
              </div>
              <Button
                size="sm"
                variant="subtle"
                loading={pendingId === block.id}
                onClick={() => unblock.mutate(block.id)}
              >
                Unblock
              </Button>
            </Card>
          ))}
        </ul>
      )}
      {blocks.data?.truncated && (
        <p className="text-sm text-muted">There may be more blocked releases than this list shows.</p>
      )}
      {unblock.isError && (
        <Alert tone="danger">
          {errorMessage(unblock.error, 'That release could not be unblocked. It is still blocked.')}
        </Alert>
      )}
    </section>
  )
}

function EditProfileModal({ profile, onClose }: { profile: AcquisitionProfile; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [cutoffRank, setCutoffRank] = useState(profile.cutoffRank)
  const [upgradesAllowed, setUpgradesAllowed] = useState(profile.upgradesAllowed)
  // The write itself only ever needs a second look when it would newly permit replacing files already
  // on disk — turning the switch off, or leaving it as it was, never routes through this step.
  const [confirmingUpgrade, setConfirmingUpgrade] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    // The endpoint accepts the whole shape, not a partial patch — an omitted field would not be kept
    // as-is, so both current values travel on every write, whichever one the operator actually changed.
    mutationFn: () => decisionApi.setUpgradePolicy(profile.id, { cutoffRank, upgradesAllowed }),
    onMutate: () => setError(null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['decision', 'profiles'] })
      onClose()
    },
    onError: (err) => setError(errorMessage(err, 'Could not update the profile.')),
  })

  const turningUpgradesOn = upgradesAllowed && !profile.upgradesAllowed

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (turningUpgradesOn && !confirmingUpgrade) {
      setConfirmingUpgrade(true)
      return
    }
    save.mutate()
  }

  if (confirmingUpgrade) {
    return (
      <Modal open onClose={onClose} title={`Turn on upgrades for “${profile.name}”?`}>
        <div className="flex flex-col gap-4">
          <p className="text-sm text-fg">
            Files this profile already accepted, and everything below the cutoff after this, can now be
            replaced the moment a release that beats them turns up. Nothing is replaced immediately —
            this only lets future searches for this profile start acting on the files already on disk.
          </p>

          {error && <Alert tone="danger">{error}</Alert>}

          <div className="mt-1 flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => setConfirmingUpgrade(false)}>
              Back
            </Button>
            <Button type="button" loading={save.isPending} onClick={() => save.mutate()}>
              Turn on upgrades
            </Button>
          </div>
        </div>
      </Modal>
    )
  }

  return (
    <Modal open onClose={onClose} title={`Edit “${profile.name}”`}>
      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <TextField
          label="Cutoff rank"
          type="number"
          inputMode="numeric"
          min={0}
          step={1}
          required
          value={cutoffRank}
          onChange={(event) => setCutoffRank(Number(event.target.value))}
          hint="The quality rank this profile stops searching at once a title reaches it."
          autoFocus
        />
        <Checkbox
          label="Search for upgrades"
          checked={upgradesAllowed}
          onChange={(event) => setUpgradesAllowed(event.target.checked)}
          hint="Off keeps every file below the cutoff exactly as it is. On lets a better release replace it."
        />

        {error && <Alert tone="danger">{error}</Alert>}

        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Save
          </Button>
        </div>
      </form>
    </Modal>
  )
}
