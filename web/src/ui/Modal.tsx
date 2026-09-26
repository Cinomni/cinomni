import { useEffect, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { cn } from '@/lib/cn'

/** Elements a keyboard user can reach with Tab. Matches the standard focus-trap allowlist. */
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

/** Tabbable descendants of `container`, in DOM order. Queried live so it reflects current content. */
function getTabbable(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
    (element) => !element.hasAttribute('hidden') && element.getAttribute('aria-hidden') !== 'true',
  )
}

/** Id of the element `main.tsx` mounts the app into (see `web/index.html`). Never `document.body`: the
 *  dialog itself is portaled onto `document.body`, so inerting the body would hide the dialog from
 *  assistive technology along with everything behind it. */
const APP_ROOT_ID = 'root'

// Module-scoped so nested dialogs share one reference count: the app root becomes inert when the
// first dialog opens and stays inert until the last one closes, instead of an inner dialog's close
// prematurely un-hiding the app behind the outer one still open.
let openDialogCount = 0
let restoreAppRoot: (() => void) | null = null

/**
 * Marks the application root `inert` so assistive technology and pointer input cannot reach it while
 * a dialog is open, no matter what `aria-modal` alone implies. Captures whatever inert state the root
 * already had (there is none today, but this does not assume that) so it can be restored exactly
 * rather than unconditionally cleared.
 */
function acquireInertAppRoot(): void {
  const root = document.getElementById(APP_ROOT_ID)
  if (openDialogCount === 0 && root) {
    const hadInertAttribute = root.hasAttribute('inert')
    const previousInertValue = root.getAttribute('inert')
    restoreAppRoot = () => {
      if (hadInertAttribute) {
        root.setAttribute('inert', previousInertValue ?? '')
      } else {
        root.removeAttribute('inert')
      }
    }
    root.setAttribute('inert', '')
  }
  openDialogCount += 1
}

/** Releases one dialog's hold on the app root, restoring it once the last open dialog releases it. */
function releaseInertAppRoot(): void {
  openDialogCount = Math.max(0, openDialogCount - 1)
  if (openDialogCount === 0) {
    restoreAppRoot?.()
    restoreAppRoot = null
  }
}

/** A centered modal dialog. Closes on Escape and backdrop click; locks page scroll while open. */
export function Modal({
  open,
  onClose,
  title,
  children,
  className,
  bare = false,
  placement = 'center',
}: {
  open: boolean
  onClose: () => void
  title: string
  children: ReactNode
  className?: string
  /** No title bar or padding: the content owns the whole panel (the search overlay). `title` still names the dialog. */
  bare?: boolean
  /**
   * `side` docks the panel to the right edge at full height: for read-only detail (attempts, files)
   * that the person reads alongside the list they opened it from, rather than a decision to confirm.
   */
  placement?: 'center' | 'side'
}) {
  const dialogRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLElement | null>(null)

  // Hide the rest of the app from assistive technology and pointer input while this dialog is open.
  // Declared before the focus-management effect below on purpose: React runs cleanup functions in
  // declaration order, and that effect's cleanup focuses the trigger element, which lives inside the
  // app root — the root must already be un-inerted by then or a real inert-supporting browser will
  // refuse to focus it.
  useEffect(() => {
    if (!open) return
    acquireInertAppRoot()
    return releaseInertAppRoot
  }, [open])

  // Move focus into the dialog on open, and give it back to whatever opened the dialog on close
  // (including an unmount while focus is still inside, since this cleanup runs then too).
  useEffect(() => {
    if (!open) return
    triggerRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null

    const dialog = dialogRef.current
    if (dialog && !dialog.contains(document.activeElement)) {
      const [first] = getTabbable(dialog)
      ;(first ?? dialog).focus()
    }

    return () => {
      const trigger = triggerRef.current
      if (trigger && document.body.contains(trigger)) trigger.focus()
    }
  }, [open])

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose()
        return
      }
      if (event.key !== 'Tab') return

      const dialog = dialogRef.current
      if (!dialog) return

      // Queried on every Tab press, not cached, so content that changes while open stays trapped.
      const tabbable = getTabbable(dialog)
      if (tabbable.length === 0) {
        event.preventDefault()
        dialog.focus()
        return
      }

      const first = tabbable[0]
      const last = tabbable[tabbable.length - 1]
      const active = document.activeElement
      if (event.shiftKey) {
        if (active === first || !dialog.contains(active)) {
          event.preventDefault()
          last.focus()
        }
      } else if (active === last || !dialog.contains(active)) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', onKey)
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = ''
    }
  }, [open, onClose])

  if (!open) return null

  return createPortal(
    <div
      className={cn(
        'fixed inset-0 z-50 flex bg-black/70',
        placement === 'side' ? 'justify-end' : 'items-start justify-center overflow-y-auto p-4 py-[8vh]',
      )}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose()
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        tabIndex={-1}
        className={cn(
          placement === 'side'
            ? 'h-full w-full max-w-xl animate-side-in overflow-y-auto bg-surface shadow-[var(--shadow-lift)] ring-1 ring-line'
            : 'w-full max-w-lg animate-sheet-in rounded-panel bg-surface shadow-[var(--shadow-lift)] ring-1 ring-line',
          className,
        )}
      >
        {bare ? (
          children
        ) : (
          <>
            <div className="flex items-center justify-between border-b border-line-soft px-5 py-4">
              <h2 className="text-section text-fg">{title}</h2>
              <button
                onClick={onClose}
                aria-label="Close"
                className="rounded-control p-1.5 text-muted transition-colors hover:bg-hover hover:text-fg"
              >
                <svg viewBox="0 0 24 24" className="size-5" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
                  <path d="M6 6l12 12M18 6L6 18" strokeLinecap="round" />
                </svg>
              </button>
            </div>
            <div className="p-5">{children}</div>
          </>
        )}
      </div>
    </div>,
    document.body,
  )
}
