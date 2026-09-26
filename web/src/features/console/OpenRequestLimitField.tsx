import { useState, type FormEvent } from 'react'
import { Button } from '@/ui/Button'
import { Select } from '@/ui/Select'
import { TextField } from '@/ui/TextField'

/**
 * The three things an account's open-request limit can mean. They are modelled as a choice rather
 * than as a number with sentinel values because two of them are not numbers an operator would ever
 * type: "use whatever the installation says" and "no limit" are decisions, and `null` and `0` are
 * merely how they travel.
 */
type LimitMode = 'default' | 'unlimited' | 'capped'

function modeOf(limit: number | null): LimitMode {
  if (limit === null) return 'default'
  return limit === 0 ? 'unlimited' : 'capped'
}

/** The server's bound on a per-account cap; above it the API refuses the value. */
const MAX_OPEN_REQUESTS = 1_000

const MODE_LABEL: Record<LimitMode, string> = {
  default: 'Installation default',
  unlimited: 'No limit',
  capped: 'At most',
}

function isLimitMode(value: string): value is LimitMode {
  return value === 'default' || value === 'unlimited' || value === 'capped'
}

/**
 * How many requests one account may have open at once.
 *
 * Applying is a second, explicit action rather than a change-on-blur, because two of the three modes
 * carry no number and a control that saved as you typed would write `At most 1` on the way to
 * `At most 12`. The count only accompanies `capped`; the other two send `null` and `0`.
 */
export function OpenRequestLimitField({
  value,
  disabled,
  onApply,
}: {
  value: number | null
  disabled: boolean
  onApply: (limit: number | null) => void
}) {
  const [mode, setMode] = useState<LimitMode>(() => modeOf(value))
  const [count, setCount] = useState(() => (value !== null && value > 0 ? String(value) : ''))
  const [error, setError] = useState<string | null>(null)

  const parsed = Number(count)
  const serverValue = mode === 'default' ? null : mode === 'unlimited' ? 0 : parsed
  const unchanged = mode === modeOf(value) && (mode !== 'capped' || parsed === value)

  function submit(event: FormEvent) {
    event.preventDefault()
    if (mode === 'capped' && (!/^\d+$/.test(count.trim()) || parsed < 1 || parsed > MAX_OPEN_REQUESTS)) {
      setError(`Enter a whole number from 1 to ${MAX_OPEN_REQUESTS}, or choose No limit.`)
      return
    }
    setError(null)
    onApply(serverValue)
  }

  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
      <Select
        label="Open requests"
        className="w-44 text-sm"
        value={mode}
        disabled={disabled}
        onChange={(event) => {
          if (isLimitMode(event.target.value)) {
            setMode(event.target.value)
            setError(null)
          }
        }}
      >
        {Object.entries(MODE_LABEL).map(([key, label]) => (
          <option key={key} value={key}>
            {label}
          </option>
        ))}
      </Select>

      {mode === 'capped' && (
        <TextField
          label="Requests"
          inputMode="numeric"
          className="w-24"
          value={count}
          disabled={disabled}
          onChange={(event) => setCount(event.target.value)}
          aria-invalid={error ? true : undefined}
        />
      )}

      <Button type="submit" size="sm" variant="subtle" disabled={disabled || unchanged}>
        Apply
      </Button>

      {error && (
        <p role="alert" className="w-full text-xs text-danger">
          {error}
        </p>
      )}
    </form>
  )
}
