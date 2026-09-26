import type { CSSProperties, ReactNode } from 'react'
import { cn } from '@/lib/cn'
import { Spinner } from './Spinner'

export type ColumnAlign = 'start' | 'end'

export interface Column<T> {
  /** Stable identity for the column; also used as the React key for its header/cells. */
  key: string
  header: ReactNode
  render: (row: T) => ReactNode
  align?: ColumnAlign
  /** Keeps the header text for the accessible column name but hides it visually (e.g. an icon or action column). */
  srOnlyHeader?: boolean
  /** A CSS width (e.g. `'4rem'`) applied as a column hint via `<col>`. */
  width?: string
}

export interface DataTableProps<T> {
  columns: Column<T>[]
  rows: readonly T[]
  rowKey: (row: T) => string
  /** Visually hidden; this is the table's accessible name. */
  caption: string
  /** Shown, spanning every column, when `rows` is empty and the table is not loading. */
  empty?: ReactNode
  /** When true, shows a loading row spanning every column instead of `rows`. */
  loading?: boolean
  onRowClick?: (row: T) => void
}

const ALIGN_CLASSES: Record<ColumnAlign, string> = {
  start: 'text-left',
  end: 'text-right',
}

/**
 * A dense, horizontally-scrollable table for operator lists. Cells do not wrap: on a narrow
 * viewport the table scrolls sideways inside its own bordered container instead of squeezing
 * columns into illegible widths or letting the page itself scroll horizontally.
 *
 * When `onRowClick` is supplied, the first column's cell renders as a real `<button>` filling the
 * cell rather than a click handler on the `<tr>`. That keeps every row reachable and operable from
 * the keyboard through one focusable control per row. An overlay stretched across the whole row
 * (the common "clickable card" trick) was deliberately avoided: a positioned overlay paints above
 * normal-flow content, so it would sit on top of and swallow clicks on any interactive control a
 * caller renders in a later column (a row action button, a link).
 */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  caption,
  empty,
  loading = false,
  onRowClick,
}: DataTableProps<T>) {
  const columnCount = columns.length

  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-card">
        <caption className="sr-only">{caption}</caption>
        <colgroup>
          {columns.map((column) => {
            const colStyle: CSSProperties | undefined = column.width ? { width: column.width } : undefined
            return <col key={column.key} style={colStyle} />
          })}
        </colgroup>
        <thead>
          <tr className="border-b border-line">
            {columns.map((column) => (
              <th
                key={column.key}
                scope="col"
                className={cn(
                  'whitespace-nowrap px-3 py-2 text-label uppercase text-faint',
                  ALIGN_CLASSES[column.align ?? 'start'],
                )}
              >
                {column.srOnlyHeader ? <span className="sr-only">{column.header}</span> : column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {loading ? (
            <tr>
              <td colSpan={columnCount} className="px-3 py-10">
                <div className="flex items-center justify-center gap-2 text-muted">
                  <Spinner className="size-4" />
                  <span>Loading…</span>
                </div>
              </td>
            </tr>
          ) : rows.length === 0 ? (
            <tr>
              <td colSpan={columnCount} className="px-3 py-10">
                {empty}
              </td>
            </tr>
          ) : (
            rows.map((row) => (
              <DataTableRow key={rowKey(row)} row={row} columns={columns} onRowClick={onRowClick} />
            ))
          )}
        </tbody>
      </table>
    </div>
  )
}

function DataTableRow<T>({
  row,
  columns,
  onRowClick,
}: {
  row: T
  columns: Column<T>[]
  onRowClick?: (row: T) => void
}) {
  return (
    <tr className={cn('border-b border-line-soft last:border-b-0', onRowClick && 'hover:bg-surface')}>
      {columns.map((column, index) => {
        const isPrimaryClickCell = index === 0 && Boolean(onRowClick)
        const alignClass = ALIGN_CLASSES[column.align ?? 'start']
        const content = column.render(row)

        return (
          <td
            key={column.key}
            className={cn('whitespace-nowrap text-fg', alignClass, isPrimaryClickCell ? 'p-0' : 'px-3 py-2.5')}
          >
            {isPrimaryClickCell && onRowClick ? (
              <button
                type="button"
                onClick={() => onRowClick(row)}
                className={cn('block w-full px-3 py-2.5 text-fg transition-colors hover:text-accent-strong', alignClass)}
              >
                {content}
              </button>
            ) : (
              content
            )}
          </td>
        )
      })}
    </tr>
  )
}
