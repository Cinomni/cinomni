import { forwardRef, useId, type InputHTMLAttributes } from 'react'
import { cn } from '@/lib/cn'

interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  label: string
  hint?: string
}

/** A real checkbox input with an associated, clickable label and a visible focus ring. */
export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox(
  { label, hint, id, className, disabled, ...rest },
  ref,
) {
  const generatedId = useId()
  const checkboxId = id ?? generatedId
  const hintId = hint ? `${checkboxId}-hint` : undefined

  return (
    <div className="flex flex-col gap-1">
      <label
        htmlFor={checkboxId}
        className={cn(
          'flex items-center gap-2 text-sm text-muted',
          disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer',
        )}
      >
        <input
          ref={ref}
          type="checkbox"
          id={checkboxId}
          disabled={disabled}
          aria-describedby={hintId}
          className={cn('size-4 rounded border-line accent-[var(--color-accent)]', className)}
          {...rest}
        />
        {label}
      </label>
      {hint && (
        <p id={hintId} className="pl-6 text-xs text-faint">
          {hint}
        </p>
      )}
    </div>
  )
})
