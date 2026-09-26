import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Menu } from './Menu'

function renderMenu(onRefresh = vi.fn(), onArtwork = vi.fn()) {
  render(
    <Menu
      label="More actions for Arrival"
      items={[
        { label: 'Refresh metadata', onSelect: onRefresh },
        { label: 'Change artwork', onSelect: onArtwork },
      ]}
    />,
  )
  return { onRefresh, onArtwork }
}

describe('Menu', () => {
  it('Menu_stays_closed_until_asked_and_announces_its_state', async () => {
    const user = userEvent.setup()
    renderMenu()
    const trigger = screen.getByRole('button', { name: 'More actions for Arrival' })

    expect(trigger).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()

    await user.click(trigger)

    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('menuitem', { name: 'Refresh metadata' })).toHaveFocus()
  })

  it('Menu_moves_with_the_arrow_keys_and_runs_the_chosen_item_once', async () => {
    const user = userEvent.setup()
    const { onRefresh, onArtwork } = renderMenu()

    await user.click(screen.getByRole('button', { name: 'More actions for Arrival' }))
    await user.keyboard('{ArrowDown}')
    expect(screen.getByRole('menuitem', { name: 'Change artwork' })).toHaveFocus()
    await user.keyboard('{Enter}')

    expect(onArtwork).toHaveBeenCalledTimes(1)
    expect(onRefresh).not.toHaveBeenCalled()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('Menu_closes_on_Escape_and_gives_focus_back_to_its_trigger', async () => {
    const user = userEvent.setup()
    renderMenu()
    const trigger = screen.getByRole('button', { name: 'More actions for Arrival' })

    await user.click(trigger)
    await user.keyboard('{Escape}')

    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })
})
