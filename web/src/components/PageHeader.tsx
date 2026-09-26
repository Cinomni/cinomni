import type { ReactNode } from 'react'

/** A consistent page title row with an optional action on the right. */
export function PageHeader({
  title,
  subtitle,
  action,
}: {
  title: string
  subtitle?: string
  action?: ReactNode
}) {
  return (
    <div className="mb-8 flex flex-wrap items-end justify-between gap-4">
      <div>
        <h1 className="text-title text-fg">{title}</h1>
        {subtitle && <p className="mt-1 text-meta text-faint">{subtitle}</p>}
      </div>
      {action}
    </div>
  )
}
