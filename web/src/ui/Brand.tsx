import { cn } from '@/lib/cn'

/**
 * The Cinomni mark: an open "C" around a six-blade shutter. Drawn inline in `currentColor` (from the
 * official brand asset) so it takes the accent here and any tone elsewhere without a second file.
 */
export function ApertureMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 256 256" className={cn('size-6', className)} fill="currentColor" aria-hidden="true">
      <path d="M 235.553 84.546 A 116 116 0 1 0 235.553 171.454 L 207.738 160.216 A 86 86 0 1 1 207.738 95.784 Z" />
      <path d="M 136.855 55.349 A 68.40 68.40 0 0 1 192.345 95.342 L 113.766 95.342 Z" />
      <path d="M 195.345 99.344 A 68.40 68.40 0 0 1 188.455 167.396 L 149.166 99.344 Z" />
      <path d="M 186.490 171.995 A 68.40 68.40 0 0 1 124.110 200.054 L 163.400 132.002 Z" />
      <path d="M 119.145 200.651 A 68.40 68.40 0 0 1 63.655 160.658 L 142.234 160.658 Z" />
      <path d="M 60.655 156.656 A 68.40 68.40 0 0 1 67.545 88.604 L 106.834 156.656 Z" />
      <path d="M 69.510 84.005 A 68.40 68.40 0 0 1 131.890 55.946 L 92.600 123.998 Z" />
      <circle cx="128" cy="128" r="12" />
    </svg>
  )
}

/** Mark plus wordmark. The name is set tight and heavy; the mark carries the accent. */
export function Brand({ className }: { className?: string }) {
  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <ApertureMark className="size-6 text-accent" />
      <span className="text-section font-bold tracking-brand text-fg">cinomni</span>
    </span>
  )
}
