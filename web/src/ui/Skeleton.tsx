import { cn } from '@/lib/cn'

/**
 * Placeholders shaped like what is loading, so the layout is already in place when the data lands and
 * nothing jumps. The shimmer is a slow opacity pulse (no moving gradient) and stops under reduced motion.
 */
export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden="true" className={cn('animate-shimmer rounded-control bg-elevated', className)} />
}

/** A 2:3 poster with its title and year lines. */
export function SkeletonPoster({ className }: { className?: string }) {
  return (
    <div aria-hidden="true" className={cn('flex flex-col gap-2', className)}>
      <Skeleton className="aspect-2/3 w-full rounded-media" />
      <Skeleton className="h-3.5 w-3/4" />
      <Skeleton className="h-3 w-1/3" />
    </div>
  )
}

/** A rail heading and a row of posters, bleeding off the right edge like the real rail. */
export function SkeletonRail({ count = 8, label }: { count?: number; label: string }) {
  return (
    <div role="status" aria-label={label} className="space-y-4">
      <div className="gutter">
        <Skeleton className="h-5 w-40" />
      </div>
      <div className="gutter flex gap-4 overflow-hidden">
        {Array.from({ length: count }, (_, index) => (
          <SkeletonPoster key={index} className="w-36 shrink-0 sm:w-40 lg:w-44" />
        ))}
      </div>
    </div>
  )
}

/** The full-bleed hero: a backdrop block with the title, metadata and action lines inside it. */
export function SkeletonHero({ label = 'Loading' }: { label?: string }) {
  return (
    <div role="status" aria-label={label} className="relative h-[min(72vh,40rem)] min-h-[26rem] bg-surface">
      <div className="gutter absolute inset-x-0 bottom-10 space-y-4">
        <Skeleton className="h-3 w-48" />
        <Skeleton className="h-10 w-2/3 max-w-xl" />
        <Skeleton className="h-4 w-full max-w-lg" />
        <Skeleton className="h-4 w-4/5 max-w-md" />
        <div className="flex gap-3 pt-2">
          <Skeleton className="h-12 w-36" />
          <Skeleton className="size-12" />
        </div>
      </div>
    </div>
  )
}
