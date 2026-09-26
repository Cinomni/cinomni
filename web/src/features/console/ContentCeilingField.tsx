import { Select } from '@/ui/Select'

/**
 * The highest classification one account may see. The options are the server's ladder for the
 * current region — this control does not know an order of its own. An empty ladder means a ceiling
 * cannot be set, and the page says so rather than offering a choice the API would refuse.
 */
export function ContentCeilingField({
  value,
  region,
  applies,
  certificates,
  disabled,
  onApply,
}: {
  value: string | null
  region: string | null
  applies: boolean
  certificates: readonly string[]
  disabled: boolean
  onApply: (ceiling: string | null) => void
}) {
  if (certificates.length === 0) {
    return (
      <p className="max-w-sm text-sm text-muted">
        Set a classification region this installation can order before restricting what an account may see.
      </p>
    )
  }

  const stale = value !== null && !certificates.includes(value)

  return (
    <Select
      label="Content ceiling"
      className="w-56 text-sm"
      value={value ?? ''}
      disabled={disabled}
      hint={
        stale || (value !== null && !applies)
          ? `Set for ${region ?? 'another region'}, and not applied while this installation classifies differently.`
          : 'Titles above this stay hidden. Unrated titles stay visible.'
      }
      onChange={(event) => onApply(event.target.value === '' ? null : event.target.value)}
    >
      <option value="">No restriction</option>
      {stale && value !== null && <option value={value}>{value} (not in effect)</option>}
      {certificates.map((certificate) => (
        <option key={certificate} value={certificate}>
          {certificate}
        </option>
      ))}
    </Select>
  )
}
