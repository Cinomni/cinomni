import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { renderWithProviders } from '@/test/render'
import { aWork } from '@/test/factories'
import { AwaitingMetadataNotice } from './AwaitingMetadataNotice'

describe('AwaitingMetadataNotice', () => {
  it('AwaitingMetadataNotice_tells_the_operator_why_members_cannot_see_a_held_title', () => {
    renderWithProviders(<AwaitingMetadataNotice work={aWork({ awaitingMetadata: true })} />)

    expect(screen.getByRole('status')).toHaveTextContent('Hidden from members for now')
  })

  it('AwaitingMetadataNotice_says_nothing_about_a_title_members_can_already_see', () => {
    renderWithProviders(<AwaitingMetadataNotice work={aWork()} />)

    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })
})
