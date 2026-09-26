import { cn } from '@/lib/cn'
import { Button } from './Button'

/**
 * A consistent retryable error state: a plain-words headline, the message the caller narrowed from the
 * failed request (typically `ApiError.message`), and a retry action. Anything diagnostic — a status, an
 * error code — goes in `details`, which stays behind a "Technical details" disclosure so the first
 * thing read is what happened, not how. This component never reads the error object itself, so callers
 * stay in control of what gets shown.
 */
export function ErrorState({
  title = 'Something went wrong',
  message,
  details,
  onRetry,
  className,
}: {
  title?: string
  message: string
  details?: string
  onRetry: () => void
  className?: string
}) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-4 px-6 py-16 text-center', className)}>
      <svg viewBox="0 0 24 24" className="size-8 text-danger/80" fill="none" stroke="currentColor" strokeWidth={1.6} aria-hidden="true">
        <circle cx="12" cy="12" r="9" />
        <path d="M12 7.5v5.5M12 16.2v.3" strokeLinecap="round" />
      </svg>
      <div className="space-y-1.5">
        <p className="text-section text-fg">{title}</p>
        <p role="alert" className="mx-auto max-w-md text-meta text-muted">
          {message}
        </p>
      </div>
      <Button variant="subtle" size="sm" onClick={onRetry}>
        Retry
      </Button>
      {details && (
        <details className="max-w-md text-left text-meta text-faint">
          <summary className="cursor-pointer select-none text-center hover:text-muted">Technical details</summary>
          <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-all rounded-control bg-surface p-3 font-mono text-label normal-case tracking-normal">
            {details}
          </pre>
        </details>
      )}
    </div>
  )
}
