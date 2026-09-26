import type { ReactNode } from 'react'
import { cn } from '@/lib/cn'
import { ChevronRightIcon } from './icons'

/**
 * Progressive disclosure for the advanced layer: technical detail that exists for whoever wants it and
 * stays folded for everyone else. A native `<details>`, so it works without script, is announced as
 * expandable, and opens with Enter or Space.
 */
export function Disclosure({
  title,
  hint,
  children,
  defaultOpen = false,
  className,
}: {
  title: string
  /** What is inside, so the folded row says whether it is worth opening. */
  hint?: string
  children: ReactNode
  defaultOpen?: boolean
  className?: string
}) {
  return (
    <details open={defaultOpen} className={cn('group border-t border-line-soft', className)}>
      <summary className="flex cursor-pointer select-none list-none items-center gap-3 py-4 text-muted transition-colors hover:text-fg [&::-webkit-details-marker]:hidden">
        <ChevronRightIcon className="size-4 transition-transform duration-150 group-open:rotate-90" />
        <span className="text-section text-fg">{title}</span>
        {hint && <span className="hidden truncate text-meta text-faint sm:inline">{hint}</span>}
      </summary>
      <div className="pb-6 pl-7">{children}</div>
    </details>
  )
}
