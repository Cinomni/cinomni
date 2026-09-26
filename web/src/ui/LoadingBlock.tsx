import { cn } from '@/lib/cn'
import { Skeleton } from './Skeleton'

type LoadingBlockSize = 'sm' | 'md' | 'lg'

/** How many placeholder lines stand in for the content, by the size of the space it will take. */
const LINE_COUNT: Record<LoadingBlockSize, number> = {
  sm: 2,
  md: 3,
  lg: 5,
}

/** Varied widths, so the block reads as text still arriving rather than as a set of bars. */
const LINE_WIDTHS = ['w-full', 'w-11/12', 'w-4/5', 'w-2/3', 'w-3/4']

/**
 * A placeholder for a section awaiting remote data whose exact shape is not known in advance (a
 * settings panel, a diagnostic list). Where the shape is known, the media components use their own
 * skeletons; this is the generic one — lines, not a spinner, so the space the content will need is
 * held from the start and nothing jumps when it lands.
 *
 * The block owns the live-region announcement (`role="status"`, `aria-live="polite"`) and names itself
 * with `label`, so the lines themselves stay out of the accessibility tree.
 */
export function LoadingBlock({
  size = 'md',
  label = 'Loading',
  className,
}: {
  size?: LoadingBlockSize
  label?: string
  className?: string
}) {
  return (
    <div role="status" aria-live="polite" aria-label={label} className={cn('space-y-3 py-4', className)}>
      {Array.from({ length: LINE_COUNT[size] }, (_, index) => (
        <Skeleton key={index} className={cn('h-4', LINE_WIDTHS[index % LINE_WIDTHS.length])} />
      ))}
      <span className="sr-only">{label}</span>
    </div>
  )
}
