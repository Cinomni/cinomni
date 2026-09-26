import type { ReactNode } from 'react'
import { cn } from '@/lib/cn'

/**
 * The ticket line: uppercase label type with tabular numerals, parts separated by thin vertical rules
 * (`2016 │ 1H 56M │ RELEASED`). Empty parts are dropped, so a caller can pass optional values as-is.
 */
export function MediaMetadata({
  items,
  className,
}: {
  items: ReadonlyArray<ReactNode | null | undefined | false>
  className?: string
}) {
  const parts = items.filter((item): item is Exclude<ReactNode, null | undefined | false> => item != null && item !== false && item !== '')
  if (parts.length === 0) return null
  return (
    <p className={cn('flex flex-wrap items-center gap-x-2.5 gap-y-1 text-label uppercase tabular-nums text-muted', className)}>
      {parts.map((part, index) => (
        <span key={index} className="inline-flex items-center gap-2.5">
          {index > 0 && <span aria-hidden="true" className="h-2.5 w-px bg-current opacity-40" />}
          {part}
        </span>
      ))}
    </p>
  )
}
