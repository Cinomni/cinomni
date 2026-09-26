import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi } from '@/api/endpoints'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { RemoveWorkDialog } from './RemoveWorkDialog'

vi.mock('@/api/endpoints', () => ({
  catalogApi: { remove: vi.fn() },
}))

const navigate = vi.fn()
vi.mock('react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('react-router')>()),
  useNavigate: () => navigate,
}))

const film = { id: 'work-1', title: 'World War Z', kind: 'Movie' as const }

beforeEach(() => {
  vi.mocked(catalogApi.remove).mockReset()
  navigate.mockReset()
})

describe('RemoveWorkDialog', () => {
  it('RemoveWorkDialog_keeps_the_files_unless_asked_and_returns_to_the_movies', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(catalogApi.remove).mockResolvedValue(undefined)

    // Act
    renderWithProviders(<RemoveWorkDialog work={film} open onClose={vi.fn()} />)
    expect(screen.getByLabelText('Also delete the files from disk')).not.toBeChecked()
    await user.click(screen.getByRole('button', { name: 'Remove' }))

    // Assert
    await waitFor(() => expect(catalogApi.remove).toHaveBeenCalledWith('work-1', false))
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/movies', { replace: true }))
  })

  it('RemoveWorkDialog_deletes_the_files_only_when_the_box_is_ticked', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.remove).mockResolvedValue(undefined)

    renderWithProviders(<RemoveWorkDialog work={{ ...film, kind: 'Series' }} open onClose={vi.fn()} />)
    await user.click(screen.getByLabelText('Also delete the files from disk'))
    await user.click(screen.getByRole('button', { name: 'Remove and delete files' }))

    await waitFor(() => expect(catalogApi.remove).toHaveBeenCalledWith('work-1', true))
    await waitFor(() => expect(navigate).toHaveBeenCalledWith('/series', { replace: true }))
  })

  it('RemoveWorkDialog_stays_open_and_says_why_when_the_server_refuses', async () => {
    const user = userEvent.setup()
    vi.mocked(catalogApi.remove).mockRejectedValue(
      new ApiError(404, 'catalog.work_not_found', 'No work with this id is in the catalog.'),
    )

    renderWithProviders(<RemoveWorkDialog work={film} open onClose={vi.fn()} />)
    await user.click(screen.getByRole('button', { name: 'Remove' }))

    expect(await screen.findByText('No work with this id is in the catalog.')).toBeInTheDocument()
    expect(navigate).not.toHaveBeenCalled()
  })
})
