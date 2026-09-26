import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Modal } from './Modal'

/** A trigger button plus a dialog with two tabbable children, wired the way every real caller wires it. */
function Harness() {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button onClick={() => setOpen(true)}>Open dialog</button>
      <Modal open={open} onClose={() => setOpen(false)} title="Test dialog">
        <button>First</button>
        <button>Second</button>
      </Modal>
    </>
  )
}

/**
 * Renders `Harness` inside `#root`, the element `main.tsx` mounts the real app into. Testing Library's
 * default container is a plain `document.body` child, which is not what `Modal` inerts, so the inert
 * assertions need this real id present.
 */
function renderInAppRoot() {
  const appRoot = document.createElement('div')
  appRoot.id = 'root'
  document.body.append(appRoot)
  return { appRoot, ...render(<Harness />, { container: appRoot }) }
}

describe('Modal', () => {
  it('Modal_moves_focus_into_the_dialog_on_open', async () => {
    // Arrange
    const user = userEvent.setup()
    render(<Harness />)

    // Act
    await user.click(screen.getByRole('button', { name: 'Open dialog' }))

    // Assert — the close button is the first tabbable element in the dialog.
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus()
  })

  it('Modal_wraps_from_the_last_tabbable_element_back_to_the_first_on_tab', async () => {
    // Arrange
    const user = userEvent.setup()
    render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'Open dialog' }))
    screen.getByRole('button', { name: 'Second' }).focus()

    // Act
    await user.tab()

    // Assert
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus()
  })

  it('Modal_wraps_from_the_first_tabbable_element_back_to_the_last_on_shift_tab', async () => {
    // Arrange — opening leaves focus on the close button, the first tabbable element.
    const user = userEvent.setup()
    render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'Open dialog' }))

    // Act
    await user.tab({ shift: true })

    // Assert
    expect(screen.getByRole('button', { name: 'Second' })).toHaveFocus()
  })

  it('Modal_restores_focus_to_the_trigger_on_close', async () => {
    // Arrange
    const user = userEvent.setup()
    render(<Harness />)
    const trigger = screen.getByRole('button', { name: 'Open dialog' })
    await user.click(trigger)

    // Act
    await user.click(screen.getByRole('button', { name: 'Close' }))

    // Assert
    expect(trigger).toHaveFocus()
  })

  it('Modal_still_closes_on_escape', async () => {
    // Arrange
    const user = userEvent.setup()
    render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'Open dialog' }))
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    // Act
    await user.keyboard('{Escape}')

    // Assert
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('Modal_makes_the_app_root_inert_while_open_and_restores_it_on_close', async () => {
    // Arrange
    const user = userEvent.setup()
    const { appRoot } = renderInAppRoot()
    expect(appRoot).not.toHaveAttribute('inert')

    // Act
    await user.click(screen.getByRole('button', { name: 'Open dialog' }))

    // Assert — the rest of the app is unreachable to assistive technology while the dialog is open.
    expect(appRoot).toHaveAttribute('inert')

    // Act
    await user.click(screen.getByRole('button', { name: 'Close' }))

    // Assert — closing gives the app back, not just the dialog.
    expect(appRoot).not.toHaveAttribute('inert')
  })

  it('Modal_restores_the_app_root_inert_state_on_unmount_while_open', async () => {
    // Arrange
    const user = userEvent.setup()
    const appRoot = document.createElement('div')
    appRoot.id = 'root'
    document.body.append(appRoot)

    function UnmountableHarness() {
      const [mounted, setMounted] = useState(true)
      return (
        <>
          <button onClick={() => setMounted(false)}>Unmount</button>
          {mounted && <Harness />}
        </>
      )
    }

    render(<UnmountableHarness />, { container: appRoot })
    await user.click(screen.getByRole('button', { name: 'Open dialog' }))
    expect(appRoot).toHaveAttribute('inert')

    // Act — the dialog unmounts while still open, instead of going through its own close button.
    await user.click(screen.getByRole('button', { name: 'Unmount' }))

    // Assert
    expect(appRoot).not.toHaveAttribute('inert')
  })

  it('Modal_keeps_the_app_root_inert_until_the_last_of_two_open_dialogs_closes', async () => {
    // Arrange — a second, independent dialog nested inside the first one's content.
    function NestedHarness() {
      const [outerOpen, setOuterOpen] = useState(false)
      const [innerOpen, setInnerOpen] = useState(false)
      return (
        <>
          <button onClick={() => setOuterOpen(true)}>Open outer</button>
          <Modal open={outerOpen} onClose={() => setOuterOpen(false)} title="Outer dialog">
            <button onClick={() => setInnerOpen(true)}>Open inner</button>
            <Modal open={innerOpen} onClose={() => setInnerOpen(false)} title="Inner dialog">
              <button>Inner content</button>
            </Modal>
          </Modal>
        </>
      )
    }
    const user = userEvent.setup()
    const appRoot = document.createElement('div')
    appRoot.id = 'root'
    document.body.append(appRoot)
    render(<NestedHarness />, { container: appRoot })

    // Act
    await user.click(screen.getByRole('button', { name: 'Open outer' }))
    await user.click(screen.getByRole('button', { name: 'Open inner' }))
    const innerDialog = screen.getByRole('dialog', { name: 'Inner dialog' })
    await user.click(within(innerDialog).getByRole('button', { name: 'Close' }))

    // Assert — closing the inner dialog must not un-hide the app behind the outer dialog still open.
    expect(appRoot).toHaveAttribute('inert')
  })
})
