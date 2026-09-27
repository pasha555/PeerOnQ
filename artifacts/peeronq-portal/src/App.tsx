import { Route, Switch } from 'wouter';
import { useAuth } from './auth';
import { LoadingState } from './components';
import {
  AcceptInvitationPage, AuditPage, AuthPage, InvitationsPage, MembersPage, OrganizationsPage,
  PolicyPage, PrivacyPage, ProfilePage, ResetPasswordPage, SecurityPage, SessionsPage, TeamsPage,
  TrustedDevicesPage, VerifyEmailPage,
} from './pages';
import { OrganizationProvider, Shell } from './shell';
import { DevicesPage, DownloadsPage, OverviewPage, RemoteSessionsPage, SupportPage } from './workspacePages';

function NotFound() { return <section className="state"><h1>Page not found</h1><p>This portal page does not exist.</p><a className="button primary" href="/">Return to overview</a></section>; }

export function App() {
  const { status } = useAuth();
  if (location.pathname === '/verify-email') return <VerifyEmailPage />;
  if (location.pathname === '/reset-password') return <ResetPasswordPage />;
  if (status === 'loading') return <main className="standalone"><LoadingState label="Restoring your secure account session…" /></main>;
  if (status !== 'authenticated') return <AuthPage />;
  return <OrganizationProvider><Shell><Switch>
    <Route path="/" component={OverviewPage} />
    <Route path="/account" component={ProfilePage} />
    <Route path="/remote-sessions" component={RemoteSessionsPage} />
    <Route path="/downloads" component={DownloadsPage} />
    <Route path="/support" component={SupportPage} />
    <Route path="/sessions" component={SessionsPage} />
    <Route path="/trusted-devices" component={TrustedDevicesPage} />
    <Route path="/organizations" component={OrganizationsPage} />
    <Route path="/members" component={MembersPage} />
    <Route path="/devices" component={DevicesPage} />
    <Route path="/teams" component={TeamsPage} />
    <Route path="/invitations" component={InvitationsPage} />
    <Route path="/invitations/accept" component={AcceptInvitationPage} />
    <Route path="/policy" component={PolicyPage} />
    <Route path="/security" component={SecurityPage} />
    <Route path="/audit" component={AuditPage} />
    <Route path="/privacy" component={PrivacyPage} />
    <Route><NotFound /></Route>
  </Switch></Shell></OrganizationProvider>;
}
