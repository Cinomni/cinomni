import { useCallback, useEffect, useState, type ComponentType, type SVGProps } from 'react'
import { Link, NavLink, Outlet } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { requestsApi } from '@/api/endpoints'
import { useAuth } from '@/auth/useAuth'
import { cn } from '@/lib/cn'
import { BuildFooter } from '@/components/BuildFooter'
import { NotificationBell } from '@/features/notifications/NotificationBell'
import { SearchOverlay } from '@/features/search/SearchOverlay'
import { useFallbackRefetchInterval } from '@/realtime/useRealtimeStatus'
import { Brand } from '@/ui/Brand'
import {
  ActivityIcon,
  CalendarIcon,
  FilmIcon,
  HomeIcon,
  InboxIcon,
  LogOutIcon,
  MoreIcon,
  SearchIcon,
  ServerIcon,
  TvIcon,
  UserIcon,
} from '@/ui/icons'
import { Modal } from '@/ui/Modal'

interface NavEntry {
  to: string
  label: string
  icon: ComponentType<SVGProps<SVGSVGElement>>
  end?: boolean
}

const REQUESTS: NavEntry = { to: '/requests', label: 'Requests', icon: InboxIcon }
const ACCOUNT: NavEntry = { to: '/account', label: 'Your account', icon: UserIcon }

/** Watching: what every account is here for. */
const WATCH: readonly NavEntry[] = [
  { to: '/', label: 'Home', icon: HomeIcon, end: true },
  { to: '/movies', label: 'Movies', icon: FilmIcon },
  { to: '/series', label: 'Series', icon: TvIcon },
  { to: '/calendar', label: 'Upcoming', icon: CalendarIcon },
  REQUESTS,
]

/**
 * Operating: administrator-only, because the API behind each refuses a regular account. Kept in its
 * own quieter group so the plumbing never competes with the library for attention.
 */
const MANAGE: readonly NavEntry[] = [
  { to: '/activity', label: 'Activity', icon: ActivityIcon },
  { to: '/console', label: 'Administration', icon: ServerIcon },
]

/** What the phone's bottom bar carries; everything else is one tap away under "More". */
const MOBILE_TABS: readonly NavEntry[] = WATCH.filter((entry) => entry !== REQUESTS)

/** Target of the skip link; `<main>` takes it so the jump lands on the content landmark itself. */
const MAIN_CONTENT_ID = 'main-content'

/** Typing in any of these must never be hijacked by the "/" search shortcut. */
function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)
}

/** Opens the global search on "/" (outside a text field) and on Ctrl/⌘+K (anywhere). */
function useSearchShortcut(onOpen: () => void) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const isCommandK = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k'
      const isSlash = event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey && !isEditable(event.target)
      if (!isCommandK && !isSlash) return
      event.preventDefault()
      onOpen()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onOpen])
}

function sidebarLinkClass({ isActive }: { isActive: boolean }): string {
  return cn(
    'relative flex items-center gap-3 rounded-control px-3 py-2 text-card transition-colors',
    // The marquee line: the active destination is marked by a short amber bar, not a filled pill.
    'before:absolute before:inset-y-2 before:-left-3 before:w-0.75 before:rounded-full before:bg-accent before:transition-opacity',
    isActive ? 'text-fg before:opacity-100' : 'text-muted before:opacity-0 hover:bg-hover hover:text-fg',
  )
}

export function AppLayout() {
  const { user, logout } = useAuth()
  const isAdmin = user?.isAdministrator ?? false
  const [searchOpen, setSearchOpen] = useState(false)
  const [moreOpen, setMoreOpen] = useState(false)

  const openSearch = useCallback(() => setSearchOpen(true), [])
  useSearchShortcut(openSearch)

  // Only an administrator can act on a pending request, so only they get the count (and the endpoint).
  // A submitted or resolved request pushes the count; the interval covers a stream that is down.
  const { data: pending } = useQuery({
    queryKey: ['requests', 'pending-count'],
    queryFn: requestsApi.pendingCount,
    enabled: isAdmin,
    refetchInterval: useFallbackRefetchInterval(30_000),
  })
  const pendingCount = isAdmin ? (pending?.count ?? 0) : 0

  const accessibleLabel = (entry: NavEntry) =>
    entry.to === '/requests' && pendingCount > 0 ? `${entry.label}, ${pendingCount} pending` : entry.label

  const renderSidebarEntry = (entry: NavEntry) => (
    <li key={entry.to}>
      <NavLink to={entry.to} end={entry.end} className={sidebarLinkClass} aria-label={accessibleLabel(entry)}>
        <entry.icon className="size-5" />
        <span>{entry.label}</span>
        {entry.to === '/requests' && pendingCount > 0 && (
          <span aria-hidden="true" className="ml-auto text-meta tabular-nums text-accent">
            {pendingCount > 99 ? '99+' : pendingCount}
          </span>
        )}
      </NavLink>
    </li>
  )

  return (
    <div className="flex min-h-dvh bg-bg">
      {/* The whole navigation stands between a keyboard user and the page they asked for, on every
          navigation. Hidden until focused, so it costs a pointer user nothing. */}
      <a
        href={`#${MAIN_CONTENT_ID}`}
        className="sr-only focus:not-sr-only focus:absolute focus:left-3 focus:top-3 focus:z-50 focus:rounded-control focus:bg-elevated focus:px-3 focus:py-2 focus:text-card focus:text-fg"
      >
        Skip to main content
      </a>

      <aside className="sticky top-0 hidden h-dvh w-58 shrink-0 flex-col border-r border-line-soft bg-surface md:flex">
        <div className="flex items-center justify-between px-5 pb-5 pt-6">
          <Link to="/" aria-label="Cinomni home" className="rounded-control">
            <Brand />
          </Link>
          <NotificationBell />
        </div>

        <div className="px-3">
          <button
            type="button"
            onClick={openSearch}
            className="flex h-10 w-full items-center gap-3 rounded-control bg-bg/60 px-3 text-card text-muted ring-1 ring-inset ring-line-soft transition-colors hover:text-fg hover:ring-line"
          >
            <SearchIcon className="size-4" />
            <span className="flex-1 text-left">Search</span>
            <kbd className="rounded bg-elevated px-1.5 text-label text-faint">Ctrl K</kbd>
          </button>
        </div>

        <nav aria-label="Main" className="mt-6 flex flex-1 flex-col gap-6 overflow-y-auto px-3">
          <ul className="flex flex-col gap-0.5">{WATCH.map(renderSidebarEntry)}</ul>
          {isAdmin && (
            <div>
              <p className="px-3 pb-2 text-label uppercase text-faint">Manage</p>
              <ul className="flex flex-col gap-0.5">{MANAGE.map(renderSidebarEntry)}</ul>
            </div>
          )}
        </nav>

        <div className="flex items-center gap-1 border-t border-line-soft p-3">
          <NavLink
            to="/account"
            className={({ isActive }) =>
              cn(
                'flex min-w-0 flex-1 items-center gap-3 rounded-control px-2 py-1.5 transition-colors hover:bg-hover',
                isActive && 'bg-hover',
              )
            }
          >
            <span className="grid size-8 shrink-0 place-items-center rounded-full bg-elevated text-card font-semibold uppercase text-muted">
              {user?.username?.slice(0, 1) ?? '?'}
            </span>
            <span className="min-w-0">
              <span className="block truncate text-card text-fg">{user?.username ?? 'Signed in'}</span>
              <span className="block text-meta text-faint">{isAdmin ? 'Administrator' : 'Member'}</span>
            </span>
          </NavLink>
          <button
            type="button"
            onClick={() => void logout()}
            aria-label="Sign out"
            title="Sign out"
            className="grid size-9 place-items-center rounded-control text-muted transition-colors hover:bg-hover hover:text-danger"
          >
            <LogOutIcon className="size-5" />
          </button>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        {/* Phone top bar: brand, search, inbox. Navigation itself lives in the bottom bar. */}
        <header className="gutter sticky top-0 z-30 flex h-14 items-center gap-2 bg-bg/95 md:hidden">
          <Link to="/" aria-label="Cinomni home" className="mr-auto rounded-control">
            <Brand />
          </Link>
          <button
            type="button"
            onClick={openSearch}
            aria-label="Search"
            className="grid size-10 place-items-center rounded-control text-muted transition-colors hover:bg-hover hover:text-fg"
          >
            <SearchIcon className="size-5" />
          </button>
          <NotificationBell />
        </header>

        <main id={MAIN_CONTENT_ID} tabIndex={-1} className="min-w-0 flex-1 pb-20 outline-none md:pb-0">
          <Outlet />
        </main>

        <div className="pb-20 md:pb-0">
          <BuildFooter />
        </div>
      </div>

      <nav
        aria-label="Main"
        className="fixed inset-x-0 bottom-0 z-30 border-t border-line-soft bg-surface pb-[env(safe-area-inset-bottom)] md:hidden"
      >
        <ul className="grid grid-cols-5">
          {MOBILE_TABS.map((entry) => (
            <li key={entry.to}>
              <NavLink
                to={entry.to}
                end={entry.end}
                className={({ isActive }) =>
                  cn(
                    'relative flex h-16 flex-col items-center justify-center gap-1 text-label normal-case tracking-normal transition-colors',
                    'before:absolute before:inset-x-5 before:top-0 before:h-0.75 before:rounded-b-full before:bg-accent',
                    isActive ? 'text-fg before:opacity-100' : 'text-faint before:opacity-0',
                  )
                }
              >
                <entry.icon className="size-5" />
                {entry.label}
              </NavLink>
            </li>
          ))}
          <li>
            <button
              type="button"
              onClick={() => setMoreOpen(true)}
              aria-label={pendingCount > 0 ? `More, ${pendingCount} pending requests` : 'More'}
              className="relative flex h-16 w-full flex-col items-center justify-center gap-1 text-label normal-case tracking-normal text-faint"
            >
              <MoreIcon className="size-5" />
              More
              {pendingCount > 0 && <span aria-hidden="true" className="absolute right-1/3 top-3 size-2 rounded-full bg-accent" />}
            </button>
          </li>
        </ul>
      </nav>

      <Modal open={moreOpen} onClose={() => setMoreOpen(false)} title="More">
        <ul className="-mx-2 flex flex-col">
          {[REQUESTS, ...(isAdmin ? MANAGE : []), ACCOUNT].map((entry) => (
            <li key={entry.to}>
              <NavLink
                to={entry.to}
                // Choosing a destination is what the sheet was opened for; it closes behind the navigation.
                onClick={() => setMoreOpen(false)}
                aria-label={accessibleLabel(entry)}
                className={({ isActive }) =>
                  cn(
                    'flex items-center gap-3 rounded-control px-3 py-3 text-body transition-colors hover:bg-hover',
                    isActive ? 'text-fg' : 'text-muted',
                  )
                }
              >
                <entry.icon className="size-5" />
                {entry.label}
                {entry.to === '/requests' && pendingCount > 0 && (
                  <span aria-hidden="true" className="ml-auto text-meta tabular-nums text-accent">
                    {pendingCount}
                  </span>
                )}
              </NavLink>
            </li>
          ))}
          <li className="mt-2 border-t border-line-soft pt-2">
            <button
              type="button"
              onClick={() => void logout()}
              className="flex w-full items-center gap-3 rounded-control px-3 py-3 text-body text-muted transition-colors hover:bg-hover hover:text-danger"
            >
              <LogOutIcon className="size-5" />
              Sign out
            </button>
          </li>
        </ul>
      </Modal>

      <SearchOverlay open={searchOpen} onClose={() => setSearchOpen(false)} />
    </div>
  )
}
