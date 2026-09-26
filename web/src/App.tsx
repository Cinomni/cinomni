import { Navigate, Route, Routes } from 'react-router'
import { useAuth } from './auth/useAuth'
import { RealtimeProvider } from './realtime/RealtimeProvider'
import { AppLayout } from './components/AppLayout'
import { PageFrame } from './components/PageFrame'
import { ErrorState } from './ui/ErrorState'
import { ApertureMark } from './ui/Brand'
import { AuthPage } from './features/auth/AuthPage'
import { AccountPage } from './features/account/AccountPage'
import { CalendarPage } from './features/calendar/CalendarPage'
import { HomePage } from './features/home/HomePage'
import { LibraryPage } from './features/library/LibraryPage'
import { WorkDetailPage } from './features/work/WorkDetailPage'
import { SeriesDetailPage } from './features/series/SeriesDetailPage'
import { AddMoviePage } from './features/add/AddMoviePage'
import { AddSeriesPage } from './features/add/AddSeriesPage'
import { ActivityPage } from './features/activity/ActivityPage'
import { RequestsPage } from './features/requests/RequestsPage'
import { ConsoleLayout } from './features/console/ConsoleLayout'
import { SystemPage } from './features/console/SystemPage'
import { OperationsPage } from './features/console/OperationsPage'
import { SettingsPage } from './features/console/SettingsPage'
import { ImportListPage } from './features/console/ImportListPage'
import { ImportsPage } from './features/console/ImportsPage'
import { WantedPage } from './features/console/WantedPage'
import { IndexersPage } from './features/console/IndexersPage'
import { ProfilesPage } from './features/console/ProfilesPage'
import { ChannelsPage } from './features/console/ChannelsPage'
import { CollectionsPage } from './features/console/CollectionsPage'
import { UsersPage } from './features/console/UsersPage'
import { PlayerPage } from './features/player/PlayerPage'

export function App() {
  const { status, user, retrySession } = useAuth()

  if (status === 'loading') {
    return (
      <div role="status" aria-label="Loading Cinomni" className="grid min-h-dvh place-items-center bg-bg">
        <ApertureMark className="size-10 animate-shimmer text-accent" />
      </div>
    )
  }

  if (status === 'unavailable') {
    // The stored session may be perfectly good; the server just did not answer. Signing out here
    // would make the viewer type their password again for a restart they had nothing to do with.
    return (
      <div className="grid min-h-dvh place-items-center bg-bg px-4">
        <ErrorState
          title="Cinomni can't be reached"
          message="You are still signed in. Check that the server is running, then try again."
          onRetry={() => void retrySession()}
        />
      </div>
    )
  }

  if (status === 'setup' || status === 'anon') {
    return <AuthPage mode={status === 'setup' ? 'setup' : 'login'} />
  }

  // The API refuses these to a regular account, so the client does not offer them either.
  const isAdmin = user?.isAdministrator ?? false

  return (
    // Held open for the whole signed-in session: the stream is what keeps downloads, activity and the
    // inbox current, so it outlives any one page. Keyed by account: when another tab signs in as someone
    // else, the stream is reopened with that account's token rather than kept on the previous one's.
    <RealtimeProvider key={user?.id ?? ''}>
      <Routes>
        {/* The player is full-bleed, outside the app chrome. */}
        <Route path="/watch/:assetId" element={<PlayerPage />} />

        <Route element={<AppLayout />}>
          {/* Media surfaces: full-bleed, artwork edge to edge. */}
          <Route path="/" element={<HomePage />} />
          <Route path="/movies" element={<LibraryPage key="Movie" kind="Movie" />} />
          <Route path="/series" element={<LibraryPage key="Series" kind="Series" />} />
          <Route path="/library" element={<LibraryPage key="All" />} />
          <Route path="/works/:id" element={<WorkDetailPage />} />
          <Route path="/series/:id" element={<SeriesDetailPage />} />

          {/* Operational surfaces: a readable column. */}
          <Route element={<PageFrame />}>
            <Route path="/calendar" element={<CalendarPage />} />
            <Route path="/add" element={<AddMoviePage />} />
            <Route path="/add/series" element={<AddSeriesPage />} />
            <Route path="/requests" element={<RequestsPage />} />
            <Route path="/account" element={<AccountPage />} />
            {/* Transfers live under the titles they serve now; an old bookmark lands there. */}
            {isAdmin && <Route path="/downloads" element={<Navigate to="/activity" replace />} />}
            {isAdmin && <Route path="/activity" element={<ActivityPage />} />}
            {/*
              The console stays mapped for every account, unlike the two routes above, and refuses from
              inside through RequireAdmin. Dropping the route entirely would send a member who followed
              a shared link to the library with no explanation, which reads as a broken link rather than
              as a surface that was never theirs.
            */}
            <Route path="/console" element={<ConsoleLayout />}>
              <Route index element={<SystemPage />} />
              <Route path="operations" element={<OperationsPage />} />
              <Route path="settings" element={<SettingsPage />} />
              <Route path="imports" element={<ImportsPage />} />
              <Route path="import-list" element={<ImportListPage />} />
              <Route path="wanted" element={<WantedPage />} />
              <Route path="indexers" element={<IndexersPage />} />
              <Route path="profiles" element={<ProfilesPage />} />
              <Route path="channels" element={<ChannelsPage />} />
              <Route path="collections" element={<CollectionsPage />} />
              <Route path="users" element={<UsersPage />} />
            </Route>
          </Route>
          {/* The two administrative surfaces merged into the console; an old bookmark still lands somewhere sensible. */}
          <Route path="/settings" element={<Navigate to="/console" replace />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </RealtimeProvider>
  )
}
