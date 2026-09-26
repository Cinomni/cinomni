import type { ComponentPropsWithoutRef, ElementType, ReactNode } from 'react'
import { cn } from '@/lib/cn'

type CardElement = 'div' | 'li' | 'section' | 'article'
type Padding = 'none' | 'sm' | 'md'

const PADDING: Record<Padding, string> = {
  none: '',
  sm: 'p-3',
  md: 'p-4',
}

interface CardOwnProps<T extends CardElement> {
  /** The rendered element. Use 'li' inside a list, 'section'/'article' for a standalone panel. */
  as?: T
  /** Inner spacing: 'sm' matches a dense row, 'md' the common row/panel padding, 'none' for custom layouts. */
  padding?: Padding
  /** Adds the hover/focus-within treatment already used by rows that contain a clickable child. */
  interactive?: boolean
  className?: string
  children?: ReactNode
}

type CardProps<T extends CardElement> = CardOwnProps<T> & Omit<ComponentPropsWithoutRef<T>, keyof CardOwnProps<T>>

/**
 * The quiet panel surface (no border: the surface step alone separates it from the page) shared by
 * operational list rows and settings panels. Media surfaces do not use it — artwork carries those.
 *
 * Originally the dark bordered surface shared by list rows and standalone panels across the app: a rounded
 * `bg-surface` block with a `border-line` edge. Generalizes the copy-pasted
 * `rounded-panel border border-line bg-surface` shell; it does not change how any adopting page looks.
 */
export function Card<T extends CardElement = 'div'>({
  as,
  padding = 'md',
  interactive = false,
  className,
  children,
  ...rest
}: CardProps<T>) {
  const Component = (as ?? 'div') as ElementType

  return (
    <Component
      className={cn(
        'rounded-panel bg-surface',
        PADDING[padding],
        interactive && 'transition-colors hover:bg-elevated focus-within:ring-1 focus-within:ring-accent',
        className,
      )}
      {...rest}
    >
      {children}
    </Component>
  )
}
