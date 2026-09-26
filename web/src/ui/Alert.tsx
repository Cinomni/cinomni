import type { ReactNode } from 'react'
import { cn } from '@/lib/cn'

export type AlertTone = 'danger' | 'warning' | 'info' | 'success'

const TONES: Record<AlertTone, string> = {
  danger: 'border-danger bg-danger/8 text-danger',
  warning: 'border-warning bg-warning/8 text-warning',
  info: 'border-info bg-info/8 text-info',
  success: 'border-success bg-success/8 text-success',
}

// danger and warning need urgent, un-requested announcement; info and success are routine
// acknowledgements that should not interrupt a screen reader mid-task.
const ROLES: Record<AlertTone, 'alert' | 'status'> = {
  danger: 'alert',
  warning: 'alert',
  info: 'status',
  success: 'status',
}

interface AlertProps {
  tone: AlertTone
  title?: string
  children: ReactNode
  className?: string
}

/**
 * A form/page-level message tied to a status tone. Always mount this component with its final
 * content — toggling it from empty to filled after mount can lose the announcement in some
 * screen readers, since role="alert"/"status" only reliably fires on regions already in the DOM.
 */
export function Alert({ tone, title, children, className }: AlertProps) {
  return (
    <div
      role={ROLES[tone]}
      // A tone bar on the leading edge, not a box: the message reads as part of the page it annotates.
      className={cn('rounded-r-control border-l-2 px-3 py-2 text-meta', TONES[tone], className)}
    >
      {title && <p className="font-medium">{title}</p>}
      <div className={title ? 'mt-1' : undefined}>{children}</div>
    </div>
  )
}
