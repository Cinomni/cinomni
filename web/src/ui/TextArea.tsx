import { forwardRef, type TextareaHTMLAttributes } from 'react'
import { cn } from '@/lib/cn'

interface TextAreaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string
  hint?: string
  error?: string
}

/** A labelled multi-line text input with the same focus, hint and error treatment as TextField. */
export const TextArea = forwardRef<HTMLTextAreaElement, TextAreaProps>(function TextArea(
  { label, hint, error, id, className, rows = 6, ...rest },
  ref,
) {
  const inputId = id ?? `field-${label.replace(/\s+/g, '-').toLowerCase()}`
  const errorId = error ? `${inputId}-error` : undefined
  const hintId = !error && hint ? `${inputId}-hint` : undefined

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={inputId} className="text-meta font-medium text-muted">
        {label}
      </label>
      <textarea
        ref={ref}
        id={inputId}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={cn(hintId, errorId) || undefined}
        className={cn(
          'rounded-control border bg-surface px-3 py-2 font-mono text-meta text-fg',
          'placeholder:text-faint placeholder:font-sans focus:outline-none',
          error ? 'border-danger' : 'border-line-soft focus:border-accent',
          className,
        )}
        {...rest}
      />
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
