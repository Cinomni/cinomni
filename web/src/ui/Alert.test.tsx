import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Alert } from './Alert'

describe('Alert', () => {
  it('Alert_danger_tone_is_announced_as_an_alert_with_its_message', () => {
    // Arrange / Act
    render(<Alert tone="danger">The request could not be saved.</Alert>)

    // Assert
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('The request could not be saved.')
  })

  it('Alert_info_tone_is_announced_as_status_not_alert', () => {
    // Arrange / Act
    render(<Alert tone="info">Changes are saved automatically.</Alert>)

    // Assert — a routine acknowledgement must not interrupt a screen reader.
    expect(screen.getByRole('status')).toHaveTextContent('Changes are saved automatically.')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('Alert_renders_an_optional_title_above_the_message', () => {
    render(
      <Alert tone="warning" title="Heads up">
        The scan is still running.
      </Alert>,
    )

    expect(screen.getByText('Heads up')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Heads upThe scan is still running.')
  })
})
