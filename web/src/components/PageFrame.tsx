import { Outlet } from 'react-router'

/**
 * The reading column for operational pages — forms, lists, administration. Media pages (home, library,
 * a title) render outside it so their artwork can run edge to edge; everything in here keeps a
 * comfortable line length on a wide screen.
 */
export function PageFrame() {
  return (
    <div className="gutter mx-auto w-full max-w-6xl py-6 md:py-8">
      <Outlet />
    </div>
  )
}
