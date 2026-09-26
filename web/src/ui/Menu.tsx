import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { cn } from '@/lib/cn'
import { buttonClass } from './Button'
import { MoreIcon } from './icons'

export interface MenuItem {
  label: string
  onSelect: () => void
  icon?: ReactNode
  disabled?: boolean
  /** Destructive: drawn in the danger tone. It still has to confirm on its own. */
  danger?: boolean
}

/**
 * An overflow menu for the secondary actions of a title, a season or an episode — the ones that would
 * otherwise sit as a row of equal-weight buttons. Implements the menu-button pattern: the trigger
 * announces the popup and its state, arrow keys / Home / End move between items, Escape closes and
 * returns focus to the trigger, and Tab or a click outside simply closes it.
 */
export function Menu({
  label,
  items,
  align = 'end',
  variant = 'ghost',
  size = 'icon',
  className,
}: {
  /** The trigger's accessible name, e.g. "More actions for Arrival". */
  label: string
  items: readonly MenuItem[]
  align?: 'start' | 'end'
  variant?: 'ghost' | 'overlay' | 'subtle'
  size?: 'icon' | 'icon-sm' | 'icon-lg'
  className?: string
}) {
  const menuId = useId()
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLUListElement>(null)

  const itemButtons = () =>
    Array.from(listRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not([disabled])') ?? [])

  useEffect(() => {
    if (!open) return
    itemButtons()[0]?.focus()
    const onPointerDown = (event: MouseEvent) => {
      if (rootRef.current && event.target instanceof Node && !rootRef.current.contains(event.target)) setOpen(false)
    }
    document.addEventListener('mousedown', onPointerDown)
    return () => document.removeEventListener('mousedown', onPointerDown)
  }, [open])

  function close(returnFocus: boolean) {
    setOpen(false)
    if (returnFocus) triggerRef.current?.focus()
  }

  function onMenuKeyDown(event: KeyboardEvent<HTMLUListElement>) {
    const buttons = itemButtons()
    const index = buttons.findIndex((button) => button === document.activeElement)
    const focusAt = (next: number) => buttons[(next + buttons.length) % buttons.length]?.focus()
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault()
        focusAt(index + 1)
        break
      case 'ArrowUp':
        event.preventDefault()
        focusAt(index - 1)
        break
      case 'Home':
        event.preventDefault()
        focusAt(0)
        break
      case 'End':
        event.preventDefault()
        focusAt(buttons.length - 1)
        break
      case 'Escape':
        event.preventDefault()
        event.stopPropagation()
        close(true)
        break
      case 'Tab':
        close(false)
        break
    }
  }

  return (
    <div ref={rootRef} className={cn('relative inline-flex', className)}>
      <button
        ref={triggerRef}
        type="button"
        aria-label={label}
        title={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((current) => !current)}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' && !open) {
            event.preventDefault()
            setOpen(true)
          }
        }}
        className={buttonClass(variant, size)}
      >
        <MoreIcon className="size-5" />
      </button>

      {open && (
        <ul
          ref={listRef}
          id={menuId}
          role="menu"
          aria-label={label}
          onKeyDown={onMenuKeyDown}
          className={cn(
            'absolute top-full z-40 mt-2 min-w-52 rounded-control bg-elevated p-1 shadow-[var(--shadow-lift)] ring-1 ring-line',
            'origin-top animate-menu-in',
            align === 'end' ? 'right-0' : 'left-0',
          )}
        >
          {items.map((item) => (
            <li key={item.label} role="none">
              <button
                type="button"
                role="menuitem"
                tabIndex={-1}
                disabled={item.disabled}
                onClick={() => {
                  close(true)
                  item.onSelect()
                }}
                className={cn(
                  'flex w-full items-center gap-3 rounded-[calc(var(--radius-control)-2px)] px-3 py-2 text-left text-card',
                  'transition-colors focus:bg-hover focus:outline-none hover:bg-hover disabled:opacity-50',
                  item.danger ? 'text-danger' : 'text-fg',
                )}
              >
                {item.icon && <span className="text-muted [&>svg]:size-4">{item.icon}</span>}
                {item.label}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
