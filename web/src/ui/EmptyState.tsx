import type { ReactNode } from 'react'
import { cn } from '@/lib/cn'

/**
 * A zero state that explains itself: what is empty, why, and what to do next. No box around it — an
 * empty page is already quiet, and a dashed frame only makes the absence louder.
 */
export function EmptyState({
  icon,
  title,
  description,
  action,
  className,
}: {
  icon?: ReactNode
  title: string
  description?: string
  action?: ReactNode
  className?: string
}) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-4 px-6 py-16 text-center', className)}>
      {icon && <div className="text-faint">{icon}</div>}
      <div className="space-y-1.5">
        <p className="text-section text-fg">{title}</p>
        {description && <p className="mx-auto max-w-md text-meta text-muted">{description}</p>}
      </div>
      {action}
    </div>
  )
}
