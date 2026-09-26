import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { monitoringApi } from '@/api/endpoints'
import { renderWithProviders } from '@/test/render'
import { aTarget, aTargetTree } from '@/test/factories'
import { MonitoringModePicker } from './MonitoringModePicker'
import { useFanOutWindow, useSeriesMonitoring } from './useSeries'

vi.mock('@/api/endpoints', () => ({
  monitoringApi: { targetsForWork: vi.fn(), applyPolicy: vi.fn() },
}))

/** The picker as the series page wires it: the current mode comes from the tree, Apply posts a policy. */
function PolicyHarness() {
  const fanOut = useFanOutWindow()
  const { tree, applyPolicy } = useSeriesMonitoring('work-1', 0, fanOut)
  return (
    <MonitoringModePicker
      seriesTitle="A Show"
      currentMode={tree.data?.root?.mode ?? null}
      totalEpisodeCount={tree.data?.root?.totalCount ?? null}
      pending={applyPolicy.isPending}
      onApply={(mode) => applyPolicy.mutateAsync(mode)}
    />
  )
}

beforeEach(() => {
  vi.mocked(monitoringApi.targetsForWork).mockResolvedValue(
    aTargetTree({
      root: aTarget({ id: 'root', kind: 'Series', mode: 'All', monitored: true, totalCount: 214 }),
    }),
  )
  vi.mocked(monitoringApi.applyPolicy).mockResolvedValue({ targetId: 'root' })
})

describe('MonitoringModePicker', () => {
  it('MonitoringModePicker_posts_the_selected_mode_only_after_the_change_is_confirmed', async () => {
    // Arrange — the show is currently on "All", so Apply has nothing to do yet.
    const user = userEvent.setup()
    renderWithProviders(<PolicyHarness />)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Applied' })).toBeDisabled())

    // Act — pick a narrower policy. Nothing is posted until the cascading change is confirmed.
    await user.selectOptions(screen.getByLabelText('Monitoring'), 'Pilot')
    await user.click(screen.getByRole('button', { name: 'Apply' }))
    expect(monitoringApi.applyPolicy).not.toHaveBeenCalled()

    await user.click(await screen.findByRole('button', { name: 'Apply monitoring change' }))

    // Assert — the mode the user chose is what reaches the API.
    await waitFor(() => expect(monitoringApi.applyPolicy).toHaveBeenCalledWith('work-1', 'Pilot'))
    expect(monitoringApi.applyPolicy).toHaveBeenCalledTimes(1)

    // The modal closes itself once the mutation settles.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Apply monitoring change' })).not.toBeInTheDocument(),
    )
  })

  it('explains the mode that is selected', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PolicyHarness />)

    await user.selectOptions(screen.getByLabelText('Monitoring'), 'Future')

    expect(screen.getByText('Only episodes that have not aired yet.')).toBeInTheDocument()
  })

  it('states the blast radius from the tree’s own episode total when stopping monitoring', async () => {
    // Arrange — the tree's root reports 214 episodes; "Nothing" turns all of them off.
    const user = userEvent.setup()
    renderWithProviders(<PolicyHarness />)

    // Act
    await user.selectOptions(screen.getByLabelText('Monitoring'), 'None')
    await user.click(screen.getByRole('button', { name: 'Apply' }))

    // Assert — the modal names the show and states the real episode count, not an invented one.
    expect(await screen.findByRole('dialog', { name: 'Change monitoring for A Show?' })).toBeInTheDocument()
    expect(screen.getByText(/This stops monitoring all 214 episodes of this show/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(monitoringApi.applyPolicy).not.toHaveBeenCalled()
  })

  it('describes the consequence qualitatively when the tree has not reported a count yet', async () => {
    // Arrange — no tree loaded, so there is no real counter to quote.
    const user = userEvent.setup()
    renderWithProviders(
      <MonitoringModePicker
        seriesTitle="A Show"
        currentMode="All"
        totalEpisodeCount={null}
        pending={false}
        onApply={vi.fn().mockResolvedValue(undefined)}
      />,
    )

    // Act
    await user.selectOptions(screen.getByLabelText('Monitoring'), 'None')
    await user.click(screen.getByRole('button', { name: 'Apply' }))

    // Assert — no fabricated number, just what the change means.
    expect(await screen.findByText(/This stops monitoring every episode of this show/)).toBeInTheDocument()
  })
})
