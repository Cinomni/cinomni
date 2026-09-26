import { cn } from '@/lib/cn'

/**
 * A two-state toggle. Rendered as a real `role="switch"` button rather than a styled checkbox so it
 * carries its own accessible name and state without a wrapping label, which is what a dense list of
 * episode rows needs.
 */
export function Switch({
  checked,
  onChange,
  label,
  disabled = false,
  className,
}: {
  checked: boolean
  onChange: (checked: boolean) => void
  /** The accessible name — a row of unlabelled switches is unusable with a screen reader. */
  label: string
  disabled?: boolean
  className?: string
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        'inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors',
        'disabled:cursor-not-allowed disabled:opacity-50',
        checked ? 'border-accent/40 bg-accent/70' : 'border-line bg-elevated hover:bg-line',
        className,
      )}
    >
      <span
        className={cn(
          'size-3.5 rounded-full bg-fg shadow-sm transition-transform duration-150',
          checked ? 'translate-x-[1.125rem]' : 'translate-x-[0.1875rem]',
        )}
      />
    </button>
  )
}
