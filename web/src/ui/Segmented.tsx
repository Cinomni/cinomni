import { useRef, type KeyboardEvent } from 'react'
import { cn } from '@/lib/cn'

export interface SegmentedOption {
  readonly value: string
  readonly label: string
  readonly count?: number
}

interface SegmentedProps {
  label: string
  options: readonly SegmentedOption[]
  value: string
  onChange: (value: string) => void
  className?: string
}

/**
 * A labelled group of toggle buttons for single selection, e.g. a status filter.
 * Left/Right arrow keys move focus between options; Enter/Space activates the focused one.
 */
export function Segmented({ label, options, value, onChange, className }: SegmentedProps) {
  const groupRef = useRef<HTMLDivElement>(null)

  function focusOptionAt(index: number) {
    const buttons = groupRef.current?.querySelectorAll<HTMLButtonElement>('button')
    if (!buttons || buttons.length === 0) return
    const wrapped = (index + buttons.length) % buttons.length
    buttons[wrapped]?.focus()
  }

  function handleKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    if (event.key === 'ArrowRight') {
      event.preventDefault()
      focusOptionAt(index + 1)
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault()
      focusOptionAt(index - 1)
    }
  }

  return (
    <div ref={groupRef} role="group" aria-label={label} className={cn('inline-flex flex-wrap gap-2', className)}>
      {options.map((option, index) => {
        const selected = option.value === value
        return (
          <button
            key={option.value}
            type="button"
            aria-pressed={selected}
            onClick={() => onChange(option.value)}
            onKeyDown={(event) => handleKeyDown(event, index)}
            className={cn(
              'inline-flex items-center gap-1.5 rounded-full px-3 py-1.5 text-card transition-colors',
              selected ? 'bg-fg text-bg' : 'text-muted hover:bg-hover hover:text-fg',
            )}
          >
            {option.label}
            {option.count !== undefined && <span className="tabular-nums opacity-60">{option.count}</span>}
          </button>
        )
      })}
    </div>
  )
}
