import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { DataTable, type Column } from './DataTable'

interface Job {
  id: string
  name: string
  status: string
}

const columns: Column<Job>[] = [
  { key: 'name', header: 'Name', render: (job) => job.name },
  { key: 'status', header: 'Status', render: (job) => job.status },
]

function aJob(overrides: Partial<Job> = {}): Job {
  return { id: 'job-1', name: 'Import batch', status: 'Running', ...overrides }
}

describe('DataTable', () => {
  it('DataTable_renders_one_row_per_item_with_its_cells', () => {
    // Arrange
    const jobs = [aJob(), aJob({ id: 'job-2', name: 'Subtitle scan', status: 'Queued' })]

    // Act
    render(<DataTable columns={columns} rows={jobs} rowKey={(job) => job.id} caption="Background jobs" />)

    // Assert
    expect(screen.getByText('Import batch')).toBeInTheDocument()
    expect(screen.getByText('Running')).toBeInTheDocument()
    expect(screen.getByText('Subtitle scan')).toBeInTheDocument()
    expect(screen.getByText('Queued')).toBeInTheDocument()
    expect(screen.getAllByRole('row')).toHaveLength(3) // one header row + two data rows
  })

  it('DataTable_shows_the_empty_slot_when_there_are_no_rows', () => {
    // Arrange & Act
    render(
      <DataTable
        columns={columns}
        rows={[]}
        rowKey={(job: Job) => job.id}
        caption="Background jobs"
        empty={<p>No jobs yet</p>}
      />,
    )

    // Assert
    expect(screen.getByText('No jobs yet')).toBeInTheDocument()
    expect(screen.queryByText('Import batch')).not.toBeInTheDocument()
  })

  it('DataTable_shows_a_loading_row_instead_of_the_data_rows', () => {
    // Arrange & Act
    render(
      <DataTable columns={columns} rows={[aJob()]} rowKey={(job) => job.id} caption="Background jobs" loading />,
    )

    // Assert
    expect(screen.queryByText('Import batch')).not.toBeInTheDocument()
    expect(screen.getByRole('status', { name: 'Loading' })).toBeInTheDocument()
  })

  it('DataTable_exposes_the_caption_as_the_accessible_table_name', () => {
    // Arrange & Act
    render(<DataTable columns={columns} rows={[aJob()]} rowKey={(job) => job.id} caption="Background jobs" />)

    // Assert
    expect(screen.getByRole('table', { name: 'Background jobs' })).toBeInTheDocument()
  })

  it('DataTable_lets_a_keyboard_user_trigger_onRowClick', async () => {
    // Arrange — a keyboard user has no mouse to click the row with.
    const user = userEvent.setup()
    const onRowClick = vi.fn()
    const job = aJob()
    render(
      <DataTable
        columns={columns}
        rows={[job]}
        rowKey={(j) => j.id}
        caption="Background jobs"
        onRowClick={onRowClick}
      />,
    )

    // Act
    await user.tab()
    expect(screen.getByRole('button', { name: 'Import batch' })).toHaveFocus()
    await user.keyboard('{Enter}')

    // Assert
    expect(onRowClick).toHaveBeenCalledTimes(1)
    expect(onRowClick).toHaveBeenCalledWith(job)
  })
})
