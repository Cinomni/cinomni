import { cn } from '@/lib/cn'
import { PIPELINE_STAGES, type PipelinePosition } from '@/lib/status'

/**
 * The acquisition path as five short segments: finished stages solid, the current one in the marquee
 * amber (or red when it is where things stopped), the rest outlined. Each stage is labelled, and the
 * whole line is described in one sentence for assistive technology, so position never rests on color.
 */
export function PipelineSteps({ position, className }: { position: PipelinePosition; className?: string }) {
  const { reached, status } = position
  const current = PIPELINE_STAGES[reached]
  const summary =
    status === 'done'
      ? 'All stages complete'
      : status === 'failed'
        ? `Stopped at ${current}`
        : status === 'waiting'
          ? `Waiting to start ${current}`
          : `${current} in progress`

  return (
    <div className={cn('w-full max-w-md', className)}>
      <p className="sr-only">{summary}</p>
      <ol aria-hidden="true" className="grid grid-cols-5 gap-1">
        {PIPELINE_STAGES.map((stage, index) => {
          const isDone = index < reached || (status === 'done' && index === reached)
          const isCurrent = index === reached && status !== 'done'
          return (
            <li key={stage} className="flex flex-col gap-1.5">
              <span
                className={cn(
                  'h-1 rounded-full',
                  isDone && 'bg-success/70',
                  isCurrent && status === 'active' && 'animate-shimmer bg-accent',
                  isCurrent && status === 'failed' && 'bg-danger',
                  isCurrent && status === 'waiting' && 'bg-line',
                  !isDone && !isCurrent && 'bg-line-soft',
                )}
              />
              <span
                className={cn(
                  'truncate text-meta',
                  isCurrent ? (status === 'failed' ? 'text-danger' : 'text-fg') : isDone ? 'text-muted' : 'text-faint',
                )}
              >
                {stage}
              </span>
            </li>
          )
        })}
      </ol>
    </div>
  )
}
