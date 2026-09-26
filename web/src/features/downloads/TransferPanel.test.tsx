import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useQuery } from '@tanstack/react-query'
import { downloadsApi } from '@/api/endpoints'
import type { DownloadTaskSummary, TunnelEgressStatus } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { TransferPanel } from './TransferPanel'

vi.mock('@/api/endpoints', () => ({
  downloadsApi: {
    list: vi.fn(),
    tunnel: vi.fn(),
    pause: vi.fn(),
    resume: vi.fn(),
    remove: vi.fn(),
    get: vi.fn(),
    setPriorities: vi.fn(),
  },
}))

function aTask(overrides: Partial<DownloadTaskSummary> = {}): DownloadTaskSummary {
  return {
    id: 'task-1',
    infoHash: 'abc123',
    name: 'Arrival.2016.1080p',
    state: 'Downloading',
    progress: 0.4,
    downloadRate: 1_000_000,
    uploadRate: 0,
    numPeers: 3,
    numSeeds: 5,
    networkHeld: false,
    intentIds: ['intent-1'],
    ...overrides,
  }
}

function aTunnel(overrides: Partial<TunnelEgressStatus> = {}): TunnelEgressStatus {
  return {
    configured: true,
    policy: 'PauseAndAlert',
    verified: true,
    reason: 'tunnel-egress-verified',
    tunnelDevice: 'tun0',
    heldTaskCount: 0,
    transitionSequence: 1,
    observedAt: '2026-07-30T10:00:00Z',
    ...overrides,
  }
}

/**
 * Renders a panel per listed task against the tunnel read, the way Activity does: the panel is always
 * embedded under something that owns those two queries.
 */
function Transfers() {
  const list = useQuery({ queryKey: ['downloads'], queryFn: downloadsApi.list })
  const tunnel = useQuery({ queryKey: ['downloads', 'tunnel'], queryFn: downloadsApi.tunnel })
  return (
    <ul>
      {list.data?.map((task) => (
        <li key={task.id}>
          <TransferPanel task={task} tunnel={tunnel.data} />
        </li>
      ))}
    </ul>
  )
}

const renderTransfers = () => renderWithProviders(<Transfers />)

beforeEach(() => {
  vi.mocked(downloadsApi.tunnel).mockResolvedValue(aTunnel())
})

describe('TransferPanel transfer figures', () => {
  it('TransferPanel_shows_progress_both_rates_and_the_swarm_for_each_transfer', async () => {
    // Arrange
    vi.mocked(downloadsApi.list).mockResolvedValue([
      aTask({ progress: 0.4, downloadRate: 2 * 1024 * 1024, uploadRate: 512 * 1024, numSeeds: 12, numPeers: 30 }),
    ])

    // Act
    renderTransfers()

    // Assert — every figure is labelled, so none of them depends on an arrow glyph to be understood.
    expect(await screen.findByText('40%')).toBeInTheDocument()
    expect(screen.getByText('↓ 2.0 MB/s')).toBeInTheDocument()
    expect(screen.getByText('↑ 512 KB/s')).toBeInTheDocument()
    expect(screen.getByText('12 · 30')).toBeInTheDocument()
    expect(screen.getByText('Download')).toBeInTheDocument()
    expect(screen.getByText('Upload')).toBeInTheDocument()
    expect(screen.getByText('Seeds · Peers')).toBeInTheDocument()
  })

  it('TransferPanel_shows_an_idle_direction_as_a_dash_rather_than_zero', async () => {
    vi.mocked(downloadsApi.list).mockResolvedValue([aTask({ uploadRate: 0 })])

    renderTransfers()

    expect(await screen.findByText('↑ —')).toBeInTheDocument()
  })
})

describe('TransferPanel removal', () => {
  it('TransferPanel_does_not_remove_a_task_until_the_confirmation_is_confirmed', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(downloadsApi.list).mockResolvedValue([aTask()])

    // Act — open the confirmation but never confirm it.
    renderTransfers()
    await user.click(await screen.findByRole('button', { name: 'Actions for Arrival.2016.1080p' }))
    await user.click(screen.getByRole('menuitem', { name: 'Remove' }))
    expect(await screen.findByRole('dialog', { name: 'Remove download' })).toBeInTheDocument()

    // Assert — the click that opened the dialog must not itself have removed anything.
    expect(downloadsApi.remove).not.toHaveBeenCalled()
  })

  it('TransferPanel_passes_the_chosen_delete_files_flag_through_on_confirm', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(downloadsApi.list).mockResolvedValue([aTask()])
    vi.mocked(downloadsApi.remove).mockResolvedValue(undefined)

    // Act
    renderTransfers()
    await user.click(await screen.findByRole('button', { name: 'Actions for Arrival.2016.1080p' }))
    await user.click(screen.getByRole('menuitem', { name: 'Remove' }))
    const dialog = await screen.findByRole('dialog', { name: 'Remove download' })
    await user.click(within(dialog).getByLabelText('Also delete the downloaded files from disk'))
    await user.click(within(dialog).getByRole('button', { name: 'Remove' }))

    // Assert — the checkbox default is OFF, so this confirms the chosen value actually reaches the call.
    await waitFor(() => expect(downloadsApi.remove).toHaveBeenCalledWith('task-1', true))
  })

  it('TransferPanel_defaults_delete_files_to_off_when_confirmed_without_checking_it', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(downloadsApi.list).mockResolvedValue([aTask()])
    vi.mocked(downloadsApi.remove).mockResolvedValue(undefined)

    // Act
    renderTransfers()
    await user.click(await screen.findByRole('button', { name: 'Actions for Arrival.2016.1080p' }))
    await user.click(screen.getByRole('menuitem', { name: 'Remove' }))
    const dialog = await screen.findByRole('dialog', { name: 'Remove download' })
    await user.click(within(dialog).getByRole('button', { name: 'Remove' }))

    // Assert
    await waitFor(() => expect(downloadsApi.remove).toHaveBeenCalledWith('task-1', false))
  })

  it('TransferPanel_disables_the_confirm_button_while_removal_is_pending', async () => {
    // Arrange — a double-click while the request is in flight must not fire it twice.
    const user = userEvent.setup()
    vi.mocked(downloadsApi.list).mockResolvedValue([aTask()])
    let resolveRemove: () => void = () => undefined
    vi.mocked(downloadsApi.remove).mockReturnValue(
      new Promise<void>((resolve) => {
        resolveRemove = resolve
      }),
    )

    // Act
    renderTransfers()
    await user.click(await screen.findByRole('button', { name: 'Actions for Arrival.2016.1080p' }))
    await user.click(screen.getByRole('menuitem', { name: 'Remove' }))
    const dialog = await screen.findByRole('dialog', { name: 'Remove download' })
    const confirmButton = within(dialog).getByRole('button', { name: 'Remove' })
    await user.click(confirmButton)

    // Assert
    await waitFor(() => expect(confirmButton).toBeDisabled())
    expect(downloadsApi.remove).toHaveBeenCalledTimes(1)
    resolveRemove()
  })
})

describe('TransferPanel pause', () => {
  it('TransferPanel_explains_a_refused_pause_instead_of_leaving_the_row_looking_ignored', async () => {
    // Arrange — pausing is refused. The row keeps reporting Downloading either way, so with nothing
    // on screen the operator can only conclude their click did not register.
    const user = userEvent.setup()
    vi.mocked(downloadsApi.list).mockResolvedValue([aTask({ id: 'task-6', state: 'Downloading' })])
    vi.mocked(downloadsApi.pause).mockRejectedValue(
      new ApiError(502, 'downloads.engine_unreachable', 'The torrent engine did not answer.'),
    )

    // Act
    renderTransfers()
    await user.click(await screen.findByRole('button', { name: 'Pause' }))

    // Assert
    expect(await screen.findByText('The torrent engine did not answer.')).toBeInTheDocument()
  })
})

describe('TransferPanel network-held state', () => {
  it('TransferPanel_badges_a_held_task_and_explains_the_hold_instead_of_offering_a_plain_resume', async () => {
    // Arrange
    vi.mocked(downloadsApi.list).mockResolvedValue([
      aTask({ id: 'task-2', state: 'Paused', networkHeld: true }),
    ])
    vi.mocked(downloadsApi.tunnel).mockResolvedValue(
      aTunnel({ policy: 'PauseAndAlert', reason: 'default-route-not-via-tunnel' }),
    )

    // Act
    renderTransfers()

    // Assert — a held transfer is explained, not presented as an ordinary pause.
    expect(await screen.findByText('Network held')).toBeInTheDocument()
    expect(
      screen.getByText(/default route no longer points at the tunnel/i),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Resume' })).not.toBeInTheDocument()
  })

  it('TransferPanel_offers_a_force_override_only_when_the_policy_is_PauseAndAlert', async () => {
    // Arrange
    vi.mocked(downloadsApi.list).mockResolvedValue([
      aTask({ id: 'task-3', state: 'Paused', networkHeld: true }),
    ])
    vi.mocked(downloadsApi.tunnel).mockResolvedValue(aTunnel({ policy: 'PauseAndAlert' }))

    // Act
    renderTransfers()

    // Assert
    expect(await screen.findByRole('button', { name: 'Resume anyway' })).toBeInTheDocument()
  })

  it('TransferPanel_refuses_a_force_override_when_the_policy_is_Block', async () => {
    // Arrange
    vi.mocked(downloadsApi.list).mockResolvedValue([
      aTask({ id: 'task-4', state: 'Paused', networkHeld: true }),
    ])
    vi.mocked(downloadsApi.tunnel).mockResolvedValue(aTunnel({ policy: 'Block' }))

    // Act
    renderTransfers()

    // Assert
    await screen.findByText('Network held')
    expect(screen.queryByRole('button', { name: 'Resume anyway' })).not.toBeInTheDocument()
    expect(screen.getByText(/refused until egress verifies again/i)).toBeInTheDocument()
  })

  it('TransferPanel_calls_resume_with_force_when_the_override_is_used', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(downloadsApi.list).mockResolvedValue([
      aTask({ id: 'task-5', state: 'Paused', networkHeld: true }),
    ])
    vi.mocked(downloadsApi.tunnel).mockResolvedValue(aTunnel({ policy: 'PauseAndAlert' }))
    vi.mocked(downloadsApi.resume).mockResolvedValue(undefined)

    // Act
    renderTransfers()
    await user.click(await screen.findByRole('button', { name: 'Resume anyway' }))

    // Assert
    await waitFor(() => expect(downloadsApi.resume).toHaveBeenCalledWith('task-5', true))
  })
})
