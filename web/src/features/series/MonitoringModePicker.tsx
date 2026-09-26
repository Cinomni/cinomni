import { useState } from 'react'
import type { MonitoringMode } from '@/api/types'
import { Button } from '@/ui/Button'
import { Modal } from '@/ui/Modal'
import { Select } from '@/ui/Select'

/**
 * The seven monitoring policies, in the order an operator reasons about them: how much of the show do
 * you want. The description is what makes them distinguishable — "Existing" and "Future" are opposite
 * halves of "All" and the labels alone do not say so.
 */
const MODES: { mode: MonitoringMode; label: string; description: string }[] = [
  { mode: 'All', label: 'All episodes', description: 'Every episode of every season is watched.' },
  { mode: 'Future', label: 'Future episodes', description: 'Only episodes that have not aired yet.' },
  { mode: 'Existing', label: 'Existing episodes', description: 'Only episodes that have already aired.' },
  { mode: 'FirstSeason', label: 'First season', description: 'Only the first real season — specials are not it.' },
  { mode: 'LastSeason', label: 'Latest season', description: 'Only the highest-numbered season, the one airing now.' },
  { mode: 'Pilot', label: 'Pilot only', description: 'Only S01E01, to sample the show before committing.' },
  { mode: 'None', label: 'Nothing', description: 'Stop watching: no searches are scheduled for this show.' },
]

function pluralEpisodes(count: number): string {
  return `${count} episode${count === 1 ? '' : 's'}`
}

/**
 * The consequence line of the confirmation modal. "All" and "None" are the two modes this client can
 * describe precisely — they set every episode's monitored flag the same way, so the tree's own episode
 * total is an exact count of what changes. Every other mode depends on air dates and season structure
 * the server resolves, not this component, so it is described qualitatively rather than guessing a
 * number Monitoring alone can compute.
 */
function consequenceCopy(mode: MonitoringMode, totalEpisodeCount: number | null): string {
  if (mode === 'None') {
    return totalEpisodeCount != null
      ? `This stops monitoring all ${pluralEpisodes(totalEpisodeCount)} of this show. No searches will be scheduled for any of them.`
      : 'This stops monitoring every episode of this show. No searches will be scheduled for any of them.'
  }
  if (mode === 'All') {
    return totalEpisodeCount != null
      ? `This monitors all ${pluralEpisodes(totalEpisodeCount)} of this show.`
      : 'This monitors every episode of this show.'
  }
  return "This re-evaluates which of this show's episodes are monitored against the new policy — some may start being watched, others may stop."
}

/**
 * Chooses the monitoring policy of a series and applies it. Administrators only — the API refuses the
 * policy route to anyone else, so the caller does not render this for a regular account.
 *
 * Applying is deliberately a second, explicit action: a cascading policy opens or closes a target per
 * episode, which is not something to trigger by brushing a dropdown. The second action is a modal that
 * names the show and states the consequence, because "narrower" here can mean hundreds of episodes
 * dropping out of the wanted list in one call.
 *
 * `onApply` returns the mutation's own promise (the caller passes `mutateAsync`, not `mutate`) so this
 * component can close the modal exactly when the apply settles, rather than inferring settlement from
 * `pending` flipping back to `false` — a fast-resolving mutation can do that within a single render
 * batch, before an effect watching `pending` ever observes the intermediate `true`.
 */
export function MonitoringModePicker({
  seriesTitle,
  currentMode,
  totalEpisodeCount,
  pending,
  onApply,
}: {
  seriesTitle: string
  currentMode: MonitoringMode | null
  /** The target tree's own episode rollup (`root.totalCount`), or null while it has not loaded. */
  totalEpisodeCount: number | null
  pending: boolean
  onApply: (mode: MonitoringMode) => Promise<unknown>
}) {
  // Null until the user picks something, so the control follows the server's mode while it is still
  // loading instead of freezing whatever the first render happened to see.
  const [selection, setSelection] = useState<MonitoringMode | null>(null)
  const [confirming, setConfirming] = useState(false)
  const mode = selection ?? currentMode ?? 'All'
  const selected = MODES.find((entry) => entry.mode === mode) ?? MODES[0]

  const confirmApply = () => {
    void onApply(mode)
      .then(() => setConfirming(false))
      // A refusal is not this component's to explain — the page surfaces it. Leaving the modal open
      // keeps the operator on the policy they picked instead of silently discarding their choice.
      .catch(() => {})
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-end gap-2">
        <Select
          label="Monitoring"
          value={mode}
          onChange={(event) => setSelection(event.target.value as MonitoringMode)}
          className="text-sm"
        >
          {MODES.map((entry) => (
            <option key={entry.mode} value={entry.mode}>
              {entry.label}
            </option>
          ))}
        </Select>
        <Button
          variant="subtle"
          disabled={mode === currentMode}
          onClick={() => setConfirming(true)}
        >
          {mode === currentMode ? 'Applied' : 'Apply'}
        </Button>
      </div>
      <p className="text-xs text-faint">{selected.description}</p>

      <Modal
        open={confirming}
        onClose={() => setConfirming(false)}
        title={`Change monitoring for ${seriesTitle}?`}
      >
        <div className="flex flex-col gap-4">
          <p className="text-sm text-muted">
            Switching <span className="font-medium text-fg">{seriesTitle}</span> to{' '}
            <span className="font-medium text-fg">{selected.label}</span>. {consequenceCopy(mode, totalEpisodeCount)}
          </p>

          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={pending} onClick={() => setConfirming(false)}>
              Cancel
            </Button>
            <Button type="button" variant="primary" loading={pending} disabled={pending} onClick={confirmApply}>
              Apply monitoring change
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  )
}
