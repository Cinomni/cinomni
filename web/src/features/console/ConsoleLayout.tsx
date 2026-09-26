import { NavLink, Outlet } from 'react-router'
import { RequireAdmin } from '@/components/RequireAdmin'
import { PageHeader } from '@/components/PageHeader'
import { cn } from '@/lib/cn'
import { useScrollEdges } from '@/lib/useScrollEdges'

export type ConsoleGroup = 'System' | 'Library' | 'Acquisition' | 'Notifications' | 'People'

/** The order the groups are read in: health first, then what the platform acts on, then who uses it. */
export const CONSOLE_GROUPS: readonly ConsoleGroup[] = ['System', 'Library', 'Acquisition', 'Notifications', 'People']

/**
 * The sections of the console, grouped by the domain an operator is thinking in when they come here:
 * is it healthy (System), what is in or wanted by the library (Library), where releases come from and
 * which are acceptable (Acquisition), who hears about it (Notifications), who uses it (People).
 *
 * A section is added here only once its page exists. A sub-navigation entry pointing at a route that
 * is not mapped would answer with the catch-all redirect, which reads as the console losing the
 * click rather than as a page that has not been built.
 */
export const CONSOLE_SECTIONS: readonly { to: string; label: string; group: ConsoleGroup; end?: boolean }[] = [
  { to: '/console', label: 'System', group: 'System', end: true },
  { to: '/console/operations', label: 'Operations', group: 'System' },
  { to: '/console/settings', label: 'Settings', group: 'System' },
  { to: '/console/collections', label: 'Collections', group: 'Library' },
  { to: '/console/wanted', label: 'Wanted', group: 'Library' },
  { to: '/console/imports', label: 'Imports', group: 'Library' },
  { to: '/console/import-list', label: 'Trending', group: 'Library' },
  { to: '/console/indexers', label: 'Indexers', group: 'Acquisition' },
  { to: '/console/profiles', label: 'Profiles', group: 'Acquisition' },
  { to: '/console/channels', label: 'Channels', group: 'Notifications' },
  { to: '/console/users', label: 'Users', group: 'People' },
]

function sectionClass({ isActive }: { isActive: boolean }): string {
  return cn(
    'relative whitespace-nowrap rounded-control px-3 py-1.5 text-card transition-colors',
    // Wide screens: the same marquee bar the main navigation uses. Narrow: a filled pill.
    'lg:before:absolute lg:before:inset-y-1.5 lg:before:left-0 lg:before:w-0.75 lg:before:rounded-full lg:before:bg-accent',
    isActive
      ? 'bg-hover text-fg lg:bg-transparent lg:before:opacity-100'
      : 'text-muted hover:bg-hover hover:text-fg lg:before:opacity-0',
  )
}

/**
 * Shell for the operator console: the pages that configure and diagnose the installation itself,
 * rather than the ones a household uses to watch something. A grouped column beside the content on a
 * wide screen; one scrolling strip above it on a narrow one.
 *
 * `RequireAdmin` wraps the whole outlet so a member meets one explanation instead of a page whose
 * every panel answers 403. It is an experience guard and nothing more — what actually refuses a
 * member is the administrator policy on each endpoint behind these pages.
 */
export function ConsoleLayout() {
  const { ref: stripRef, atStart, atEnd } = useScrollEdges<HTMLDivElement>()

  return (
    <RequireAdmin>
      <PageHeader title="Administration" subtitle="Configure and diagnose this installation." />
      <div className="lg:grid lg:grid-cols-[11rem_minmax(0,1fr)] lg:gap-10">
        <nav aria-label="Console sections" className="relative mb-6 lg:mb-0">
          <div
            ref={stripRef}
            className="scrollbar-none flex gap-1 overflow-x-auto pb-1 lg:sticky lg:top-8 lg:flex-col lg:gap-6 lg:overflow-visible"
          >
            {CONSOLE_GROUPS.map((group, index) => (
              <div key={group} className="flex shrink-0 items-center gap-1 lg:flex-col lg:items-stretch lg:gap-0.5">
                {index > 0 && <span aria-hidden="true" className="mx-1 h-4 w-px bg-line lg:hidden" />}
                <p className="hidden px-3 pb-1 text-label uppercase text-faint lg:block">{group}</p>
                {CONSOLE_SECTIONS.filter((section) => section.group === group).map((section) => (
                  <NavLink key={section.to} to={section.to} end={section.end} className={sectionClass}>
                    {section.label}
                  </NavLink>
                ))}
              </div>
            ))}
          </div>
          {/* Edge fades hint that more sections sit off-screen; each disappears once scrolled to its side. */}
          {!atStart && (
            <div
              aria-hidden="true"
              className="pointer-events-none absolute inset-y-0 left-0 w-8 bg-linear-to-r from-bg to-transparent lg:hidden"
            />
          )}
          {!atEnd && (
            <div
              aria-hidden="true"
              className="pointer-events-none absolute inset-y-0 right-0 w-8 bg-linear-to-l from-bg to-transparent lg:hidden"
            />
          )}
        </nav>
        <div className="min-w-0">
          <Outlet />
        </div>
      </div>
    </RequireAdmin>
  )
}
