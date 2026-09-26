import { useId, useMemo, useState, type KeyboardEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { catalogApi } from '@/api/endpoints'
import type { Work } from '@/api/types'
import { useAuth } from '@/auth/useAuth'
import { StatusGlyph } from '@/components/media/MediaStatus'
import { PosterArt } from '@/components/media/MediaPoster'
import { CONSOLE_SECTIONS } from '@/features/console/ConsoleLayout'
import { cn } from '@/lib/cn'
import { workPath } from '@/lib/routes'
import { availabilityLabel, availabilityOf, mediaStateFromAvailability } from '@/lib/status'
import { ArrowRightIcon, PlusIcon, SearchIcon } from '@/ui/icons'
import { Modal } from '@/ui/Modal'

/** How many library titles a query shows before the provider actions; more is a job for the library page. */
const MAX_LIBRARY_RESULTS = 8

type ResultGroup = 'library' | 'add' | 'pages'

interface Result {
  id: string
  group: ResultGroup
  label: string
  hint?: string
  href: string
  work?: Work
}

const GROUP_LABEL: Record<ResultGroup, string> = {
  library: 'In your library',
  add: 'Find it online',
  pages: 'Go to',
}

interface Page {
  label: string
  href: string
  adminOnly?: boolean
}

const PAGES: readonly Page[] = [
  { label: 'Home', href: '/' },
  { label: 'Movies', href: '/movies' },
  { label: 'Series', href: '/series' },
  { label: 'Upcoming', href: '/calendar' },
  { label: 'Requests', href: '/requests' },
  { label: 'Your account', href: '/account' },
  { label: 'Activity', href: '/activity', adminOnly: true },
  // Kept as a search alias: someone typing "downloads" is looking for the transfers, which Activity shows.
  { label: 'Activity › Downloads', href: '/activity', adminOnly: true },
  ...CONSOLE_SECTIONS.map((section) => ({
    label: `Administration › ${section.label}`,
    href: section.to,
    adminOnly: true,
  })),
]

/** Library matches: titles that start with the term first, then any that contain it. */
function matchLibrary(works: readonly Work[], term: string): Work[] {
  const starts: Work[] = []
  const contains: Work[] = []
  for (const work of works) {
    const title = work.title.toLowerCase()
    if (title.startsWith(term)) starts.push(work)
    else if (title.includes(term)) contains.push(work)
  }
  return [...starts, ...contains].slice(0, MAX_LIBRARY_RESULTS)
}

/**
 * The global search: one field that answers "is it here, can I get it, where is that page". Library
 * titles come first and are marked with their state; the provider search is offered as an explicit
 * action rather than run on every keystroke, so typing never spends a metadata provider's rate limit;
 * pages and administration sections come last. Implements the combobox pattern: arrow keys move the
 * active option, Enter opens it, Escape closes the dialog.
 */
export function SearchOverlay({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Modal open={open} onClose={onClose} title="Search" bare className="max-w-xl">
      {/* Mounted only while open, so every opening starts from an empty field. */}
      {open && <SearchPanel onClose={onClose} />}
    </Modal>
  )
}

function SearchPanel({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()
  const { user } = useAuth()
  const isAdmin = user?.isAdministrator ?? false
  const canRequest = isAdmin || (user?.permissions?.canRequest ?? false)
  const listboxId = useId()
  const [term, setTerm] = useState('')
  const [activeIndex, setActiveIndex] = useState(0)

  // The same entry the activity page reads: the whole visible catalog.
  const works = useQuery({ queryKey: ['works', null], queryFn: () => catalogApi.list() })

  const results = useMemo<Result[]>(() => {
    const needle = term.trim().toLowerCase()
    const all = works.data ?? []
    const library = needle
      ? matchLibrary(all, needle)
      : [...all].sort((a, b) => b.id.localeCompare(a.id)).slice(0, 5)

    const libraryResults: Result[] = library.map((work) => ({
      id: `work-${work.id}`,
      group: 'library',
      label: work.title,
      hint: [work.year, work.kind === 'Series' ? 'Series' : 'Movie'].filter(Boolean).join(' · '),
      href: workPath(work),
      work,
    }))

    const verb = isAdmin ? 'Add' : 'Request'
    const query = encodeURIComponent(term.trim())
    const addResults: Result[] =
      needle && canRequest
        ? [
            { id: 'add-movie', group: 'add', label: `${verb} a movie called “${term.trim()}”`, href: `/add?q=${query}` },
            { id: 'add-series', group: 'add', label: `${verb} a series called “${term.trim()}”`, href: `/add/series?q=${query}` },
          ]
        : []

    const pageResults: Result[] = PAGES.filter((page) => isAdmin || !page.adminOnly)
      .filter((page) => !needle || page.label.toLowerCase().includes(needle))
      .slice(0, needle ? 5 : 6)
      .map((page) => ({ id: `page-${page.href}`, group: 'pages', label: page.label, href: page.href }))

    return [...libraryResults, ...addResults, ...pageResults]
  }, [works.data, term, isAdmin, canRequest])

  const active = results[Math.min(activeIndex, results.length - 1)]

  function open(result: Result) {
    onClose()
    navigate(result.href)
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (results.length === 0) return
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setActiveIndex((index) => (index + 1) % results.length)
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((index) => (index - 1 + results.length) % results.length)
    } else if (event.key === 'Enter' && active) {
      event.preventDefault()
      open(active)
    }
  }

  const groups = (['library', 'add', 'pages'] as const)
    .map((group) => ({ group, items: results.filter((result) => result.group === group) }))
    .filter(({ items }) => items.length > 0)

  return (
    <div className="flex max-h-[70vh] flex-col">
      <div className="flex items-center gap-3 border-b border-line-soft px-4">
        <SearchIcon className="size-5 shrink-0 text-muted" />
        <input
          autoFocus
          role="combobox"
          aria-label="Search titles and pages"
          aria-expanded={results.length > 0}
          aria-controls={listboxId}
          aria-activedescendant={active ? `${listboxId}-${active.id}` : undefined}
          aria-autocomplete="list"
          value={term}
          onChange={(event) => {
            setTerm(event.target.value)
            setActiveIndex(0)
          }}
          onKeyDown={onKeyDown}
          placeholder="Search your library, find a title, jump to a page"
          className="h-14 min-w-0 flex-1 bg-transparent text-body text-fg placeholder:text-faint focus:outline-none"
        />
        <kbd className="hidden rounded bg-elevated px-1.5 py-0.5 text-label text-faint sm:inline">Esc</kbd>
      </div>

      <div className="overflow-y-auto p-2">
        {works.isError && (
          <p role="status" className="px-3 py-2 text-meta text-muted">
            Your library could not be searched right now; pages are still available.
          </p>
        )}
        {results.length === 0 ? (
          <p className="px-3 py-8 text-center text-meta text-muted">
            Nothing matches “{term.trim()}”.
          </p>
        ) : (
          <ul id={listboxId} role="listbox" aria-label="Results">
            {groups.map(({ group, items }) => (
              <li key={group} role="presentation" className="pb-2">
                <p id={`${listboxId}-${group}`} className="px-3 pb-1 pt-2 text-label uppercase text-faint">
                  {group === 'library' && !term.trim() ? 'Recently added' : GROUP_LABEL[group]}
                </p>
                <ul role="group" aria-labelledby={`${listboxId}-${group}`}>
                  {items.map((result) => (
                    <ResultOption
                      key={result.id}
                      id={`${listboxId}-${result.id}`}
                      result={result}
                      active={result === active}
                      onHover={() => setActiveIndex(results.indexOf(result))}
                      onChoose={() => open(result)}
                    />
                  ))}
                </ul>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

function ResultOption({
  id,
  result,
  active,
  onHover,
  onChoose,
}: {
  id: string
  result: Result
  active: boolean
  onHover: () => void
  onChoose: () => void
}) {
  const work = result.work
  const availability = work ? availabilityOf(work) : null
  return (
    <li
      id={id}
      role="option"
      aria-selected={active}
      onMouseMove={onHover}
      // The option is operated from the combobox's keyboard handler; the click is the pointer path.
      onClick={onChoose}
      className={cn(
        'flex cursor-pointer items-center gap-3 rounded-control px-3 py-2',
        active ? 'bg-hover text-fg' : 'text-muted',
      )}
    >
      {work ? (
        <span className="h-12 w-8 shrink-0 overflow-hidden rounded-media bg-elevated">
          <PosterArt work={work} />
        </span>
      ) : (
        <span className="grid size-8 shrink-0 place-items-center text-faint">
          {result.group === 'add' ? <PlusIcon className="size-4" /> : <ArrowRightIcon className="size-4" />}
        </span>
      )}
      <span className="min-w-0 flex-1">
        <span className={cn('block truncate text-card', active ? 'text-fg' : 'text-fg/90')}>{result.label}</span>
        {result.hint && <span className="block truncate text-meta text-faint">{result.hint}</span>}
      </span>
      {availability && (
        <span className="inline-flex shrink-0 items-center gap-1.5 text-meta text-faint">
          <StatusGlyph state={mediaStateFromAvailability[availability]} />
          <span className="sr-only sm:not-sr-only">{availabilityLabel[availability]}</span>
        </span>
      )}
    </li>
  )
}
