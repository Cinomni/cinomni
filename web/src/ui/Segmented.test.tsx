import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Segmented, type SegmentedOption } from './Segmented'

const OPTIONS: readonly SegmentedOption[] = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
]

describe('Segmented', () => {
  it('Segmented_marks_the_selected_option_as_pressed', () => {
    // Arrange / Act
    render(<Segmented label="Status" options={OPTIONS} value="pending" onChange={vi.fn()} />)

    // Assert
    expect(screen.getByRole('button', { name: 'Pending' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'false')
    expect(screen.getByRole('button', { name: 'Approved' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('Segmented_reports_the_clicked_option', async () => {
    // Arrange
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(<Segmented label="Status" options={OPTIONS} value="all" onChange={onChange} />)

    // Act
    await user.click(screen.getByRole('button', { name: 'Approved' }))

    // Assert
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith('approved')
  })

  it('Segmented_moves_focus_between_options_with_the_arrow_keys', async () => {
    // Arrange — pressing an arrow should walk the group, wrapping at each end.
    const user = userEvent.setup()
    render(<Segmented label="Status" options={OPTIONS} value="all" onChange={vi.fn()} />)
    screen.getByRole('button', { name: 'All' }).focus()

    // Act / Assert — right walks forward and wraps past the last option.
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('button', { name: 'Pending' })).toHaveFocus()

    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('button', { name: 'Approved' })).toHaveFocus()

    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('button', { name: 'All' })).toHaveFocus()

    // Act / Assert — left wraps back to the last option.
    await user.keyboard('{ArrowLeft}')
    expect(screen.getByRole('button', { name: 'Approved' })).toHaveFocus()
  })

  it('Segmented_shows_a_count_badge_when_one_is_given', () => {
    // Arrange / Act
    const optionsWithCounts: readonly SegmentedOption[] = [
      { value: 'all', label: 'All' },
      { value: 'pending', label: 'Pending', count: 3 },
    ]
    render(<Segmented label="Status" options={optionsWithCounts} value="pending" onChange={vi.fn()} />)

    // Assert — the count is visible text next to the label, not colour alone.
    const pending = screen.getByRole('button', { name: 'Pending 3' })
    expect(pending).toBeInTheDocument()
    expect(screen.queryByText('3', { selector: 'button *' })).toBeInTheDocument()
  })
})
