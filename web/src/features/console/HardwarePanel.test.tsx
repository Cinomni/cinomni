import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { playbackApi } from '@/api/endpoints'
import type { HardwareReport } from '@/api/types'
import { ApiError } from '@/lib/api'
import { renderWithProviders } from '@/test/render'
import { HardwarePanel } from './HardwarePanel'

vi.mock('@/api/endpoints', () => ({
  playbackApi: {
    hardware: vi.fn(),
    probeHardware: vi.fn(),
  },
}))

function aReport(overrides: Partial<HardwareReport> = {}): HardwareReport {
  return {
    platform: 'Linux x64',
    ffmpegVersion: 'ffmpeg 7.1',
    probedAt: '2026-09-20T10:00:00Z',
    hwaccels: ['vaapi'],
    backends: [{ backend: 'Vaapi', encodes: ['H264', 'Hevc'], decodes: ['h264', 'hevc'] }],
    softwareHevc: true,
    toneMapping: true,
    subtitleOverlay: true,
    tests: [
      { backend: 'Vaapi', kind: 'Encode', codec: 'H264', passed: true, failure: null },
      { backend: 'Nvenc', kind: 'Encode', codec: 'H264', passed: false, failure: 'Cannot load libcuda.so.1' },
    ],
    ...overrides,
  }
}

const NOTHING_PASSED = aReport({ backends: [], tests: [], hwaccels: [] })

beforeEach(() => {
  vi.mocked(playbackApi.hardware).mockReset()
  vi.mocked(playbackApi.probeHardware).mockReset()
})

describe('HardwarePanel', () => {
  it('HardwarePanel_lists_each_backend_that_passed_with_what_it_encodes_and_decodes', async () => {
    // Arrange
    vi.mocked(playbackApi.hardware).mockResolvedValue(aReport())

    // Act
    renderWithProviders(<HardwarePanel />)

    // Assert
    expect(await screen.findByText('VAAPI')).toBeInTheDocument()
    expect(screen.getByText('encodes H.264, HEVC · decodes H.264, HEVC')).toBeInTheDocument()
    expect(screen.getByText('HDR tone mapping: available')).toBeInTheDocument()
    expect(screen.queryByText(/No hardware backend passed its test/)).not.toBeInTheDocument()
  })

  it('HardwarePanel_says_every_conversion_runs_on_the_cpu_when_no_backend_passed', async () => {
    // Arrange
    vi.mocked(playbackApi.hardware).mockResolvedValue(NOTHING_PASSED)

    // Act
    renderWithProviders(<HardwarePanel />)

    // Assert
    expect(await screen.findByText(/No hardware backend passed its test/)).toBeInTheDocument()
  })

  it('HardwarePanel_replaces_the_report_with_what_a_new_hardware_test_found', async () => {
    // Arrange — nothing passed at startup; the operator fixed the container and tests again.
    const user = userEvent.setup()
    vi.mocked(playbackApi.hardware).mockResolvedValue(NOTHING_PASSED)
    vi.mocked(playbackApi.probeHardware).mockResolvedValue(
      aReport({ backends: [{ backend: 'Nvenc', encodes: ['H264'], decodes: [] }] }),
    )
    renderWithProviders(<HardwarePanel />)
    expect(await screen.findByText(/No hardware backend passed its test/)).toBeInTheDocument()

    // Act
    await user.click(screen.getByRole('button', { name: 'Run hardware test' }))

    // Assert
    expect(await screen.findByText('NVENC')).toBeInTheDocument()
    expect(screen.getByText('encodes H.264 · decodes in software')).toBeInTheDocument()
    expect(screen.queryByText(/No hardware backend passed its test/)).not.toBeInTheDocument()
    expect(playbackApi.probeHardware).toHaveBeenCalledTimes(1)
  })

  it('HardwarePanel_explains_a_hardware_test_that_could_not_run_and_keeps_the_last_report', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.hardware).mockResolvedValue(aReport())
    vi.mocked(playbackApi.probeHardware).mockRejectedValue(
      new ApiError(409, 'playback.probe_running', 'A hardware test is already running.'),
    )
    renderWithProviders(<HardwarePanel />)
    expect(await screen.findByText('VAAPI')).toBeInTheDocument()

    // Act
    await user.click(screen.getByRole('button', { name: 'Run hardware test' }))

    // Assert
    expect(await screen.findByText('A hardware test is already running.')).toBeInTheDocument()
    expect(screen.getByText('VAAPI')).toBeInTheDocument()
  })

  it('HardwarePanel_offers_a_retry_when_the_report_fails_to_load', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.hardware)
      .mockRejectedValueOnce(new ApiError(500, 'internal', 'Could not load the hardware report.'))
      .mockResolvedValueOnce(aReport())
    renderWithProviders(<HardwarePanel />)

    // Act
    expect(await screen.findByText('Could not load the hardware report.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Retry' }))

    // Assert
    expect(await screen.findByText('VAAPI')).toBeInTheDocument()
  })

  it('HardwarePanel_toggles_the_test_details_with_an_expanded_state', async () => {
    // Arrange
    const user = userEvent.setup()
    vi.mocked(playbackApi.hardware).mockResolvedValue(aReport())
    renderWithProviders(<HardwarePanel />)
    const toggle = await screen.findByRole('button', { name: 'Show test details (2)' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('Cannot load libcuda.so.1')).not.toBeInTheDocument()

    // Act
    await user.click(toggle)

    // Assert — the same control now reports itself expanded and points at the list it opened.
    await waitFor(() => expect(toggle).toHaveAttribute('aria-expanded', 'true'))
    expect(toggle).toHaveAccessibleName('Hide test details')
    const controlled = document.getElementById(toggle.getAttribute('aria-controls') ?? '')
    expect(controlled).toContainElement(screen.getByText('Cannot load libcuda.so.1'))
    expect(screen.getByText('Failed')).toBeInTheDocument()
  })
})
