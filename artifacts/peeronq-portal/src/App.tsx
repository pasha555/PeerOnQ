import { Route, Switch } from 'wouter';
import { useAuth } from './auth';
import { LoadingState } from './components';
import {
  AcceptInvitationPage, AuditPage, AuthPage, InvitationsPage, MembersPage, OrganizationsPage,
  PolicyPage, PrivacyPage, ProfilePage, ResetPasswordPage, SecurityPage, SessionsPage, TeamsPage,
  TrustedDevicesPage, VerifyEmailPage,
} from './pages';
import { OrganizationProvider, Shell } from './shell';
import { OrganizationPage } from './portalShell';
import { PortalThemeProvider } from './theme';
import { DevicesPage, DownloadsPage, OverviewPage, RemoteSessionsPage, SupportPage } from './workspacePages';

function NotFound() { return <section className="state"><h1>Page not found</h1><p>This portal page does not exist.</p><a className="button primary" href="/">Return to overview</a></section>; }

function PortalRoutes() {
  const { status } = useAuth();
  if (location.pathname === '/verify-email') return <VerifyEmailPage />;
  if (location.pathname === '/reset-password') return <ResetPasswordPage />;
  if (status === 'loading') return <main className="standalone"><LoadingState label="Restoring your secure account session…" /></main>;
  if (status !== 'authenticated') return <AuthPage />;
  return <OrganizationProvider><Shell><Switch>
    <Route path="/" component={OverviewPage} />
    <Route path="/profile" component={ProfilePage} />
    <Route path="/account" component={ProfilePage} />
    <Route path="/remote-sessions"><OrganizationPage><RemoteSessionsPage /></OrganizationPage></Route>
    <Route path="/downloads" component={DownloadsPage} />
    <Route path="/support" component={SupportPage} />
    <Route path="/sessions" component={SessionsPage} />
    <Route path="/trusted-devices" component={TrustedDevicesPage} />
    <Route path="/organizations" component={OrganizationsPage} />
    <Route path="/members"><OrganizationPage><MembersPage /></OrganizationPage></Route>
    <Route path="/devices"><OrganizationPage><DevicesPage /></OrganizationPage></Route>
    <Route path="/teams"><OrganizationPage><TeamsPage /></OrganizationPage></Route>
    <Route path="/invitations"><OrganizationPage><InvitationsPage /></OrganizationPage></Route>
    <Route path="/invitations/accept" component={AcceptInvitationPage} />
    <Route path="/policy"><OrganizationPage><PolicyPage /></OrganizationPage></Route>
    <Route path="/security" component={SecurityPage} />
    <Route path="/audit"><OrganizationPage><AuditPage /></OrganizationPage></Route>
    <Route path="/privacy" component={PrivacyPage} />
    <Route><NotFound /></Route>
  </Switch></Shell></OrganizationProvider>;
}

export function App() { return <PortalThemeProvider><PortalRoutes /></PortalThemeProvider>; }
