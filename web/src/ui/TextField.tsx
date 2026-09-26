import { forwardRef, type InputHTMLAttributes } from 'react'
import { cn } from '@/lib/cn'

interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string
  hint?: string
}

/** A labelled text input with a consistent focus ring and hint slot. */
export const TextField = forwardRef<HTMLInputElement, TextFieldProps>(function TextField(
  { label, hint, id, className, ...rest },
  ref,
) {
  const inputId = id ?? `field-${label.replace(/\s+/g, '-').toLowerCase()}`
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={inputId} className="text-meta font-medium text-muted">
        {label}
      </label>
      <input
        ref={ref}
        id={inputId}
        className={cn(
          'h-10 rounded-control border border-line-soft bg-surface px-3 text-body text-fg',
          'placeholder:text-faint focus:border-accent focus:outline-none',
          className,
        )}
        {...rest}
      />
      {hint && <p className="text-meta text-faint">{hint}</p>}
    </div>
  )
})
