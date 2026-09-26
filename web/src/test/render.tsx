import type { ReactElement, ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { render } from '@testing-library/react'

/**
 * A query client for one test: no retries and no stale window, so a mocked rejection surfaces at once.
 * Garbage collection is off because a test seeds the cache before anything observes it, and the
 * default sweep would drop that entry before the assertion runs — every test builds its own client,
 * so nothing leaks between them anyway.
 */
export function testQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: Infinity },
      mutations: { retry: false },
    },
  })
}

/** Renders a component inside the providers every page of the app assumes: a router and a query client. */
export function renderWithProviders(ui: ReactElement, queryClient: QueryClient = testQueryClient()) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  )
  return { ...render(ui, { wrapper }), queryClient }
}
