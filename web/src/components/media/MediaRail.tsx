import { useId, type ReactNode } from 'react'
import { Link } from 'react-router'
import { cn } from '@/lib/cn'
import { useScrollEdges } from '@/lib/useScrollEdges'
import { ArrowRightIcon, ChevronLeftIcon, ChevronRightIcon } from '@/ui/icons'

/** Item widths per rail shape: portrait posters and landscape stills scroll at different paces. */
const ITEM_WIDTH = {
  poster: 'w-36 sm:w-40 lg:w-44',
  posterLarge: 'w-40 sm:w-48 lg:w-52',
  landscape: 'w-72 sm:w-80 lg:w-96',
} as const

export type RailShape = keyof typeof ITEM_WIDTH

/**
 * A titled horizontal row that bleeds to the right edge of the page while its first item aligns with
 * the page gutter. Touch and trackpads scroll it natively (with snap points); a pointer gets arrow
 * buttons that disable at each end; a keyboard user tabs through the items, which scroll into view.
 */
export function MediaRail<T>({
  title,
  description,
  items,
  itemKey,
  renderItem,
  shape = 'poster',
  seeAllHref,
  className,
}: {
  title: string
  description?: string
  items: readonly T[]
  itemKey: (item: T) => string
  renderItem: (item: T) => ReactNode
  shape?: RailShape
  seeAllHref?: string
  className?: string
}) {
  const headingId = useId()
  const { ref, atStart, atEnd } = useScrollEdges<HTMLUListElement>()

  const scrollBy = (direction: 1 | -1) => {
    const list = ref.current
    if (!list) return
    list.scrollBy({ left: direction * list.clientWidth * 0.8, behavior: 'smooth' })
  }

  return (
    <section aria-labelledby={headingId} className={cn('space-y-3', className)}>
      <div className="gutter flex items-end justify-between gap-4">
        <div className="min-w-0">
          <h2 id={headingId} className="text-section text-fg">
            {title}
          </h2>
          {description && <p className="mt-0.5 text-meta text-faint">{description}</p>}
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {seeAllHref && (
            <Link
              to={seeAllHref}
              className="mr-2 inline-flex items-center gap-1 rounded-control px-2 py-1 text-meta text-muted transition-colors hover:text-fg"
            >
              See all
              <span className="sr-only"> {title}</span>
              <ArrowRightIcon className="size-3.5" />
            </Link>
          )}
          <div className="hidden gap-1 md:flex">
            <RailArrow label={`Scroll ${title} back`} disabled={atStart} onClick={() => scrollBy(-1)}>
              <ChevronLeftIcon className="size-4" />
            </RailArrow>
            <RailArrow label={`Scroll ${title} forward`} disabled={atEnd} onClick={() => scrollBy(1)}>
              <ChevronRightIcon className="size-4" />
            </RailArrow>
          </div>
        </div>
      </div>

      <ul
        ref={ref}
        className="gutter scroll-gutter scrollbar-none flex snap-x snap-mandatory gap-4 overflow-x-auto pb-2 pt-1"
      >
        {items.map((item) => (
          <li key={itemKey(item)} className={cn('shrink-0 snap-start', ITEM_WIDTH[shape])}>
            {renderItem(item)}
          </li>
        ))}
      </ul>
    </section>
  )
}

function RailArrow({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string
  disabled: boolean
  onClick: () => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="grid size-8 place-items-center rounded-full text-muted transition-colors hover:bg-hover hover:text-fg disabled:opacity-30 disabled:hover:bg-transparent"
    >
      {children}
    </button>
  )
}
