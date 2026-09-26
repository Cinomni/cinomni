import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { importsApi } from '@/api/endpoints'
import type { PathRepairEntry, PathRepairReport } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { PathRepairPanel } from './PathRepairPanel'

vi.mock('@/api/endpoints', () => ({
  importsApi: {
    pathRepairPreview: vi.fn(),
    runPathRepair: vi.fn(),
  },
}))

function anEntry(overrides: Partial<PathRepairEntry> = {}): PathRepairEntry {
  return {
    assetId: 'asset-1',
    from: '/library/Movies/Film [2020]/Film (2020) *.mkv',
    to: '/library/Movies/Film (2020)/Film (2020).mkv',
    outcome: 'Repairable',
    sidecars: 0,
    ...overrides,
  }
}

function aReport(overrides: Partial<PathRepairReport> = {}): PathRepairReport {
  return {
    entries: [anEntry()],
    repairable: 1,
    blocked: 0,
    ...overrides,
  }
}

beforeEach(() => {
  vi.mocked(importsApi.pathRepairPreview).mockResolvedValue(aReport())
  vi.mocked(importsApi.runPathRepair).mockResolvedValue({ runId: 'run-123' })
})

describe('PathRepairPanel', () => {
  it('PathRepairPanel_does_not_call_the_repair_endpoint_until_the_confirmation_is_confirmed', async () => {
    // Arrange — the preview is a dry run that must load before the trigger button is even enabled.
    const user = userEvent.setup()
    renderWithProviders(<PathRepairPanel />)
    await screen.findByText('1 repairable')

    // Act — the top-level button only opens the confirmation; it must not run anything by itself.
    await user.click(screen.getByRole('button', { name: 'Run repair' }))

    // Assert — nothing was queued yet, and the dialog names the consequence before it can be confirmed.
    expect(importsApi.runPathRepair).not.toHaveBeenCalled()
    const dialog = screen.getByRole('dialog')
    expect(within(dialog).getByText(/never overwrites/i)).toBeInTheDocument()
    expect(within(dialog).getByText(/renaming files in a library that already works is your call/i)).toBeInTheDocument()

    // Act — confirming inside the dialog is what actually fires the request.
    await user.click(within(dialog).getByRole('button', { name: 'Run repair' }))

    // Assert
    expect(importsApi.runPathRepair).toHaveBeenCalledTimes(1)
  })

  it('PathRepairPanel_reports_a_confirmed_run_as_queued_rather_than_completed', async () => {
    // Arrange
    const user = userEvent.setup()
    renderWithProviders(<PathRepairPanel />)
    await screen.findByText('1 repairable')
    await user.click(screen.getByRole('button', { name: 'Run repair' }))
    const dialog = screen.getByRole('dialog')

    // Act
    await user.click(within(dialog).getByRole('button', { name: 'Run repair' }))

    // Assert — a 202 is a receipt, not a result: the pass has not renamed anything yet.
    expect(await screen.findByText('Repair queued')).toBeInTheDocument()
    expect(screen.getByText(/run-123/)).toBeInTheDocument()
    expect(screen.queryByText(/completed/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/\bdone\b/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('PathRepairPanel_disables_the_run_trigger_when_nothing_is_repairable', async () => {
    // Arrange — a blocked-only or empty-but-nonzero-entries library must not offer a no-op run.
    vi.mocked(importsApi.pathRepairPreview).mockResolvedValue(
      aReport({ repairable: 0, blocked: 1, entries: [anEntry({ outcome: 'Blocked' })] }),
    )
    renderWithProviders(<PathRepairPanel />)

    // Act
    await screen.findByText('1 blocked')

    // Assert
    expect(screen.getByRole('button', { name: 'Run repair' })).toBeDisabled()
  })
})
