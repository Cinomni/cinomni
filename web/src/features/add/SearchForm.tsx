import type { FormEvent } from 'react'
import { Button } from '@/ui/Button'
import { SearchIcon } from '@/ui/icons'

/**
 * The title/year search bar shared by the movie and series add pages. Purely presentational: term,
 * year and submit all live in the caller (`useMetadataSearch`), so this component never talks to
 * the API itself.
 */
export function SearchForm({
  term,
  onTermChange,
  year,
  onYearChange,
  onSubmit,
  isSearching,
}: {
  term: string
  onTermChange: (value: string) => void
  year: string
  onYearChange: (value: string) => void
  onSubmit: (event: FormEvent) => void
  isSearching: boolean
}) {
  return (
    <form onSubmit={onSubmit} className="mb-6 flex flex-col gap-2 sm:flex-row">
      <div className="flex flex-1 items-center gap-2 rounded-control border border-line bg-surface px-3 focus-within:border-accent">
        <SearchIcon className="size-4 text-faint" />
        <input
          value={term}
          onChange={(e) => onTermChange(e.target.value)}
          placeholder="Search by title"
          aria-label="Search by title"
          autoFocus
          className="h-11 flex-1 bg-transparent text-fg placeholder:text-faint focus:outline-none"
        />
      </div>
      <input
        value={year}
        onChange={(e) => onYearChange(e.target.value.replace(/\D/g, '').slice(0, 4))}
        placeholder="Year"
        inputMode="numeric"
        aria-label="Year"
        className="h-11 w-full rounded-control border border-line bg-surface px-3 text-fg placeholder:text-faint focus:border-accent focus:outline-none sm:w-24"
      />
      <Button type="submit" size="md" loading={isSearching} className="h-11 sm:w-28">
        Search
      </Button>
    </form>
  )
}
