import type { EvaluationReason } from '@/api/types'
import { cn } from '@/lib/cn'

/**
 * Why a release was accepted or rejected, as the backend persisted it. Every line is one rule the
 * profile applied: what it checked, what it wanted, and what the release actually had.
 *
 * The outcome is never colour alone — each line states "passed" or "failed" in text, because whether
 * something was rejected is the entire content of this list.
 */
export function ReleaseReasons({ reasons }: { reasons: EvaluationReason[] }) {
  if (reasons.length === 0) {
    return <p className="text-sm text-muted">No reasons were recorded for this release.</p>
  }

  return (
    <ul className="space-y-1.5">
      {reasons.map((reason, index) => (
        <li
          key={`${reason.rule}-${index}`}
          className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 text-xs"
        >
          <span
            className={cn(
              'font-mono font-semibold',
              reason.outcome === 'Pass' ? 'text-success' : 'text-danger',
            )}
            aria-hidden="true"
          >
            {reason.outcome === 'Pass' ? '✓' : '✕'}
          </span>
          <span className="font-medium text-fg">
            {humanizeRule(reason.rule)}
            <span className="sr-only">{reason.outcome === 'Pass' ? ' — passed' : ' — failed'}</span>
          </span>
          {(reason.profileValue || reason.actualValue) && (
            <span className="text-muted">
              {reason.profileValue && <>wanted {reason.profileValue}</>}
              {reason.profileValue && reason.actualValue && <span className="text-faint"> · </span>}
              {reason.actualValue && <>got {truncate(reason.actualValue)}</>}
            </span>
          )}
          {reason.rejection === 'Temporary' && (
            <span className="text-faint">(may pass with a later release)</span>
          )}
        </li>
      ))}
    </ul>
  )
}

/** `QualityAllowedByProfile` → `Quality allowed by profile`. The rule names are the backend's. */
function humanizeRule(rule: string): string {
  const spaced = rule.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  return spaced.charAt(0) + spaced.slice(1).toLowerCase()
}

/** A reason may carry a raw release title; keep one line from swallowing the panel. */
function truncate(value: string, max = 80): string {
  return value.length <= max ? value : `${value.slice(0, max)}…`
}
