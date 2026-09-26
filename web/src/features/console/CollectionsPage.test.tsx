import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { catalogApi, identityApi } from '@/api/endpoints'
import type { Collection, UserAccount } from '@/api/types'
import { renderWithProviders } from '@/test/render'
import { CollectionsPage } from './CollectionsPage'

vi.mock('@/api/endpoints', () => ({
  catalogApi: {
    collections: vi.fn(),
    createCollection: vi.fn(),
    setCollectionAccessMode: vi.fn(),
    collectionGrants: vi.fn(),
    grantCollection: vi.fn(),
    revokeCollection: vi.fn(),
    collectionRules: vi.fn().mockResolvedValue([]),
  },
  identityApi: { users: vi.fn() },
}))

const restricted: Collection = {
  id: 'collection-1',
  name: 'Grown-ups',
  kind: 'Movies',
  accessMode: 'Restricted',
  isDefault: false,
  workCount: 3,
  rulePriority: 1,
}

const nadia: UserAccount = {
  id: 'user-1',
  username: 'nadia',
  role: 'Member',
  isAdministrator: false,
  permissions: { canRequest: true, requestsAutoApproved: false, openRequestLimit: null },
  isDisabled: false,
  createdAt: '2026-01-01T00:00:00Z',
  lastLoginAt: null,
}

beforeEach(() => {
  vi.mocked(catalogApi.collections).mockResolvedValue([restricted])
  vi.mocked(identityApi.users).mockResolvedValue([nadia])
  vi.mocked(catalogApi.collectionGrants).mockResolvedValue([])
})

describe('CollectionsPage', () => {
  it('CollectionsPage_says_so_when_granting_access_fails', async () => {
    // Arrange — the box would otherwise snap back unchecked with nothing to say the grant never landed.
    const user = userEvent.setup()
    vi.mocked(catalogApi.grantCollection).mockRejectedValue(new Error('network down'))
    renderWithProviders(<CollectionsPage />)

    // Act
    await user.click(await screen.findByLabelText('nadia'))

    // Assert
    expect(await screen.findByText('network down')).toBeInTheDocument()
  })

  it('CollectionsPage_shows_a_failed_grant_read_rather_than_nobody_having_access', async () => {
    vi.mocked(catalogApi.collectionGrants).mockRejectedValue(new Error('network down'))
    renderWithProviders(<CollectionsPage />)

    expect(await screen.findByText('network down')).toBeInTheDocument()
    expect(screen.queryByLabelText('nadia')).not.toBeInTheDocument()
  })
})
