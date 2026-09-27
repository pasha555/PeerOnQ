import { Route, Switch } from 'wouter';
import { useAuth } from './auth/AuthProvider';
import { AppShell } from './components/AppShell';
import { LoginPage } from './pages/LoginPage';
import { AdminSessionsPage } from './pages/AdminSessionsPage';
import { OverviewPage } from './pages/OverviewPage';
import { ResourcePage } from './pages/ResourcePage';
import { UpgradePage } from './pages/UpgradePage';
import type { ResourceName } from './types/api';

const resourceRoutes: Array<{ path: string; resource: ResourceName }> = [
  { path: '/devices', resource: 'devices' },
  { path: '/installations', resource: 'installations' },
  { path: '/presence', resource: 'presence' },
  { path: '/sessions', resource: 'sessions' },
  { path: '/downloads', resource: 'downloads' },
  { path: '/releases', resource: 'releases' },
  { path: '/diagnostics', resource: 'diagnostics' },
  { path: '/infrastructure', resource: 'infrastructure' },
  { path: '/audit', resource: 'audit' },
  { path: '/alerts', resource: 'alerts' },
];

function NotFound() {
  return (
    <div className="page">
      <section className="state-panel">
        <h1>Page not found</h1>
        <p>This administration route does not exist or is not available in this release.</p>
        <a className="button primary" href="/">Return to overview</a>
      </section>
    </div>
  );
}

export function App() {
  const { status } = useAuth();
  if (status !== 'authenticated') return <LoginPage />;

  return (
    <AppShell>
      <Switch>
        <Route path="/" component={OverviewPage} />
        <Route path="/upgrade" component={UpgradePage} />
        <Route path="/admin-sessions" component={AdminSessionsPage} />
        {resourceRoutes.map(({ path, resource }) => (
          <Route key={path} path={path}>{() => <ResourcePage resource={resource} />}</Route>
        ))}
        <Route><NotFound /></Route>
      </Switch>
    </AppShell>
  );
}
