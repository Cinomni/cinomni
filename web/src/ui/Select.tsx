import { forwardRef, useId, type ReactNode, type SelectHTMLAttributes } from 'react'
import { cn } from '@/lib/cn'

interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label: string
  hint?: string
  error?: string
  /** `<option>` / `<optgroup>` elements, so a caller can group them. */
  children: ReactNode
}

/** A labelled native select with the same focus, hint and error treatment as TextField. */
export const Select = forwardRef<HTMLSelectElement, SelectProps>(function Select(
  { label, hint, error, id, className, children, ...rest },
  ref,
) {
  const generatedId = useId()
  const selectId = id ?? generatedId
  const errorId = error ? `${selectId}-error` : undefined
  const hintId = !error && hint ? `${selectId}-hint` : undefined

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={selectId} className="text-meta font-medium text-muted">
        {label}
      </label>
      <select
        ref={ref}
        id={selectId}
        aria-invalid={error ? true : undefined}
        aria-describedby={cn(hintId, errorId) || undefined}
        className={cn(
          'h-10 rounded-control border bg-surface px-3 text-body text-fg',
          'focus:outline-none disabled:cursor-not-allowed disabled:opacity-50',
          error ? 'border-danger' : 'border-line-soft focus:border-accent',
          className,
        )}
        {...rest}
      >
        {children}
      </select>
      {error ? (
        <p id={errorId} role="alert" className="text-meta text-danger">
          {error}
        </p>
      ) : (
        hint && (
          <p id={hintId} className="text-meta text-faint">
            {hint}
          </p>
        )
      )}
    </div>
  )
})
