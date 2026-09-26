import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Select } from './Select'

describe('Select', () => {
  it('Select_is_reachable_by_its_label_and_reports_the_chosen_option', async () => {
    // Arrange
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(
      <Select label="Protocol" defaultValue="Torznab" onChange={onChange}>
        <option value="Torznab">Torznab</option>
        <option value="Newznab">Newznab</option>
      </Select>,
    )

    // Act
    await user.selectOptions(screen.getByLabelText('Protocol'), 'Newznab')

    // Assert
    expect(onChange).toHaveBeenCalledTimes(1)
    const event = onChange.mock.calls[0][0] as { target: HTMLSelectElement }
    expect(event.target.value).toBe('Newznab')
  })

  it('Select_disabled_does_not_fire_onChange', async () => {
    // Arrange
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(
      <Select label="Protocol" defaultValue="Torznab" onChange={onChange} disabled>
        <option value="Torznab">Torznab</option>
        <option value="Newznab">Newznab</option>
      </Select>,
    )

    // Act — a disabled control refuses interaction entirely.
    await user.selectOptions(screen.getByLabelText('Protocol'), 'Newznab').catch(() => undefined)

    // Assert
    expect(onChange).not.toHaveBeenCalled()
  })

  it('Select_shows_the_error_instead_of_the_hint', () => {
    render(
      <Select label="Access" hint="Who may see it" error="Choose an access mode">
        <option value="Open">Open</option>
      </Select>,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('Choose an access mode')
    expect(screen.queryByText('Who may see it')).not.toBeInTheDocument()
  })
})
