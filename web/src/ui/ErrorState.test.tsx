import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ErrorState } from './ErrorState'

describe('ErrorState', () => {
  it('ErrorState_renders_the_message_the_caller_narrowed_from_the_error', () => {
    render(<ErrorState message="Could not reach the catalog service." onRetry={vi.fn()} />)

    expect(screen.getByText('Could not reach the catalog service.')).toBeInTheDocument()
    expect(screen.getByText('Something went wrong')).toBeInTheDocument()
  })

  it('ErrorState_retry_button_invokes_onRetry', async () => {
    const user = userEvent.setup()
    const onRetry = vi.fn()
    render(<ErrorState message="Could not reach the catalog service." onRetry={onRetry} />)

    await user.click(screen.getByRole('button', { name: 'Retry' }))

    expect(onRetry).toHaveBeenCalledTimes(1)
  })

  it('ErrorState_accepts_a_custom_title', () => {
    render(<ErrorState title="Search failed" message="Timed out." onRetry={vi.fn()} />)

    expect(screen.getByText('Search failed')).toBeInTheDocument()
  })
})
