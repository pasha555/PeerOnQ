import { ArrowRight, ArrowUpRight, BookOpen, Check, Download, FileUp, Github, Laptop, Monitor, MousePointer2, ShieldCheck, UsersRound } from 'lucide-react';
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Link } from 'wouter';
import { api, ApiError } from './api';
import { useAuth } from './auth';
import { publicWebsite, sourceRepository } from './brand';
import { EmptyState, ErrorState, LoadingState, Page } from './components';
import { useOrganization } from './shell';
import type { Device, RemoteSession, Session, TrustedDevice } from './types';

function date(value: string | null) {
  if (!value) return 'Not recorded';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? 'Not recorded' : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function label(value: string) {
  const labels: Record<string, string> = { LanDirect: 'LAN direct', InternetDirect: 'Internet direct', TurnUdp: 'TURN · UDP', TurnTcp: 'TURN · TCP', TurnTls: 'TURN · TLS', Unknown: 'Not recorded' };
  if (labels[value]) return labels[value];
  return value.replace(/([a-z])([A-Z])/g, '$1 $2');
}

function useResource<T>(path: string | null) {
  const [result, setResult] = useState<{ path: string | null; data: T[] | null; error: string | null }>({ path: null, data: null, error: null });
  const [version, setVersion] = useState(0);
  const reload = useCallback(() => setVersion((value) => value + 1), []);
  useEffect(() => {
    const controller = new AbortController();
    setResult({ path, data: null, error: null });
    if (path) {
      void api<T[]>(path, { signal: controller.signal }).then((data) => {
        if (!controller.signal.aborted) setResult({ path, data, error: null });
      }).catch((cause: unknown) => {
        if (!controller.signal.aborted) setResult({ path, data: null, error: cause instanceof ApiError ? cause.message : 'This information is unavailable. Please try again.' });
      });
    }
    return () => controller.abort();
  }, [path, version]);
  // Never display the previous organization's response while a new scope is loading.
  const current = result.path === path ? result : { data: null, error: null };
  return { ...current, loading: Boolean(path && !current.data && !current.error), reload };
}

function useOrganizationResource<T>(resource: string) {
  const { selected, loading, error: organizationError } = useOrganization();
  const state = useResource<T>(selected ? `/organizations/${encodeURIComponent(selected.id)}/${resource}` : null);
  return { ...state, loading: loading || state.loading, selected, organizationError };
}

function NoOrganization({ children }: { children?: ReactNode }) {
  return <div className="panel onboarding">
    <span className="section-icon"><UsersRound aria-hidden="true" /></span>
    <div><h2>Your shared workspace starts here</h2><p>{children ?? 'Create an organization or accept an invitation to see its assigned devices and remote-session history.'}</p><Link href="/organizations" className="button secondary">Manage organizations<ArrowRight size={16} aria-hidden="true" /></Link></div>
  </div>;
}

function SessionsTable({ sessions }: { sessions: RemoteSession[] }) {
  return <div className="table-wrap" tabIndex={0} aria-label="Remote session history"><table>
    <thead><tr><th scope="col">Started</th><th scope="col">Access mode</th><th scope="col">Status</th><th scope="col">Connection</th><th scope="col">Ended</th></tr></thead>
    <tbody>{sessions.map((item) => <tr key={item.id}><td>{date(item.startedAtUtc)}</td><td>{label(item.permissionMode)}</td><td><span className="badge">{label(item.lifecycle)}</span></td><td>{label(item.connectionPath)}</td><td>{date(item.endedAtUtc)}</td></tr>)}</tbody>
  </table></div>;
}

function AccountCount({ title, href, state }: {
  title: string; href: string;
  state: ReturnType<typeof useResource<Session | TrustedDevice>>;
}) {
  const count = state.data?.filter((item) => !item.revokedAtUtc && new Date(item.expiresAtUtc).getTime() > Date.now()).length;
  return <article className="account-metric" aria-label={title}>
    <h2>{title}</h2>
    {state.loading ? <p role="status">Loading…</p> : state.error ? <div role="alert"><p>{state.error}</p><button className="link-button" onClick={state.reload}>Retry {title.toLowerCase()}</button></div> : <><strong className="summary-value">{count}</strong><p>Active, not expired or revoked</p></>}
    <Link href={href} className="text-action">Review {title.toLowerCase()}<ArrowRight size={16} aria-hidden="true" /></Link>
  </article>;
}

export function OverviewPage() {
  const { profile, capabilities } = useAuth();
  const { organizations, selected, loading, error } = useOrganization();
  const devices = useOrganizationResource<Device>('devices');
  const sessions = useOrganizationResource<RemoteSession>('sessions');
  const accountSessions = useResource<Session>('/account/sessions');
  const trustedDevices = useResource<TrustedDevice>('/account/trusted-devices');
  return <Page title="Overview" description={profile ? `Welcome, ${profile.displayName}. Your account and shared workspace in one place.` : 'Your account and shared workspace in one place.'} actions={<Link href="/downloads" className="button primary"><Download size={17} aria-hidden="true" />Get PeerOnQ</Link>}>
    <section className="account-summary panel" aria-label="Account status">
      <div><span className="eyebrow">Your account</span><h2>{profile?.email}</h2><span className={profile?.emailVerified ? 'badge success' : 'badge muted'}>{profile?.emailVerified ? 'Email verified' : 'Email verification pending'}</span><Link href="/profile" className="text-action">Edit profile<ArrowRight size={16} aria-hidden="true" /></Link></div>
      <div className="security-summary"><ShieldCheck aria-hidden="true" /><div><strong>{capabilities?.mfaAvailable ? (profile?.mfaEnabled ? 'MFA enabled' : 'MFA not enabled') : 'Account security'}</strong><p>{capabilities?.mfaAvailable ? (profile?.mfaEnabled ? 'An additional check protects your sign-in.' : 'Add an authenticator to protect your sign-in.') : 'Manage your password and review active sign-in sessions.'}</p><Link href="/security" className="text-action">Review security<ArrowRight size={16} aria-hidden="true" /></Link></div></div>
    </section>
    <section className="account-metrics" aria-label="Account overview">
      <AccountCount title="Sign-in sessions" href="/sessions" state={accountSessions} />
      <AccountCount title="Trusted sign-in devices" href="/trusted-devices" state={trustedDevices} />
      <article className="account-metric" aria-label="Organizations"><h2>Organizations</h2>{loading ? <p role="status">Loading…</p> : error ? <p>Organization count unavailable.</p> : <><strong className="summary-value">{organizations.length}</strong><p>Your organization memberships</p></>}<Link href="/organizations" className="text-action">Manage organizations<ArrowRight size={16} aria-hidden="true" /></Link></article>
    </section>
    {loading ? <LoadingState label="Loading your workspace…" /> : selected ? <>
      <section className="workspace-banner" aria-label="Current workspace"><div><span className="eyebrow">Active organization</span><h2>{selected.name}</h2><p>Your role: {selected.role}. Devices and history below belong to this organization.</p></div><Link href="/organizations" className="button secondary">Organizations<ArrowRight size={16} aria-hidden="true" /></Link></section>
      <div className="workspace-overview">
        <section aria-labelledby="recent-sessions"><div className="section-heading"><h2 id="recent-sessions">Recent remote sessions</h2><Link href="/remote-sessions" className="text-action">View history<ArrowRight size={16} aria-hidden="true" /></Link></div>
          {sessions.error ? <ErrorState message={sessions.error} retry={sessions.reload} /> : sessions.loading ? <LoadingState label="Loading remote-session history…" /> : !sessions.data?.length ? <div className="panel"><EmptyState title="No remote sessions yet">Sessions hosted by this organization’s managed devices will appear here.</EmptyState></div> : <SessionsTable sessions={sessions.data.slice(0, 5)} />}
        </section>
        <article className="panel overview-card" aria-label="Managed devices"><span className="section-icon"><Laptop aria-hidden="true" /></span><h2>Managed devices</h2>{devices.loading ? <p role="status">Loading assigned devices…</p> : devices.error ? <div role="alert"><p>{devices.error}</p><button className="link-button" onClick={devices.reload}>Retry managed devices</button></div> : <><strong className="summary-value">{devices.data?.length}</strong><p>Assigned device records, including revoked devices. This is not an online-device count.</p></>}<Link href="/devices" className="text-action">Manage devices<ArrowRight size={16} aria-hidden="true" /></Link></article>
      </div>
    </> : !error ? <NoOrganization /> : null}
    <div className="help-strip"><BookOpen aria-hidden="true" /><p>Local LAN connections work without an account. Start remote viewing and control in the PeerOnQ app.</p><Link href="/support">How it works</Link></div>
  </Page>;
}

export function DevicesPage() {
  const state = useOrganizationResource<Device>('devices');
  return <Page title="Managed devices" description="Devices assigned to your selected organization. Personal LAN connections remain in the desktop app." actions={<Link href="/downloads" className="button secondary"><Download size={17} aria-hidden="true" />Get the app</Link>}>
    {state.organizationError ? <p>Restore your organization list to view its devices.</p> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : state.loading ? <LoadingState label="Loading organization devices…" /> : !state.selected ? <NoOrganization /> : !state.data?.length ? <div className="panel"><EmptyState title="No assigned devices">This organization has no assigned devices. Desktop devices do not appear automatically when you sign in to this portal.</EmptyState><p className="empty-guidance">Device assignment requires verified ownership. There is currently no self-service desktop sign-in or device-linking flow. Contact your organization administrator for an existing managed setup.</p></div> : <div className="cards">{state.data.map((item) => <article className="card device-card" key={item.id}><div className="card-heading"><span className="section-icon"><Laptop aria-hidden="true" /></span><span className={item.isRevoked ? 'badge muted' : 'badge success'}>{item.isRevoked ? 'Revoked' : 'Assigned'}</span></div><h2>{item.displayName}</h2><code className="device-id">{item.maskedPublicDeviceId}</code><dl className="detail-list"><div><dt>Last seen</dt><dd>{date(item.lastSeenAtUtc)}</dd></div></dl></article>)}</div>}
  </Page>;
}

export function RemoteSessionsPage() {
  const state = useOrganizationResource<RemoteSession>('sessions');
  return <Page title="Remote sessions" description="Recorded remote sessions hosted by devices assigned to your selected organization. This is connection metadata, not a screen recording." actions={<Link href="/sessions" className="button secondary">Sign-in sessions<ArrowRight size={16} aria-hidden="true" /></Link>}>
    {state.organizationError ? <p>Restore your organization list to view its remote sessions.</p> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : state.loading ? <LoadingState label="Loading remote-session history…" /> : !state.selected ? <NoOrganization /> : !state.data?.length ? <div className="panel"><EmptyState title="No remote sessions yet">Session history will appear after an assigned host device records a remote connection.</EmptyState></div> : <><p className="table-caption">{state.data.length} recorded {state.data.length === 1 ? 'session' : 'sessions'} · up to the 500 most recent</p><SessionsTable sessions={state.data} /></>}
  </Page>;
}

export function DownloadsPage() {
  return <Page title="Downloads" description="Remote connections run in the native PeerOnQ app. The public download page lists the packages currently published for your platform.">
    <section className="panel download-panel"><span className="download-icon"><Monitor aria-hidden="true" /></span><div><span className="eyebrow">PeerOnQ native app</span><h2>Your connection starts on your device.</h2><p>Use the desktop app for View Only, Full Control and File Transfer. Unattended Access requires a separate setup on a supported host.</p><a className="button primary" href={`${publicWebsite}/#download`}><Download size={17} aria-hidden="true" />View available downloads<ArrowUpRight size={16} aria-hidden="true" /></a><p className="download-note">Published packages and known release details are shown on the public download page. Platform limits are documented in the repository.</p></div></section>
    <div className="capability-grid">{([[Monitor, 'View Only', 'See the remote screen with approval.'], [MousePointer2, 'Full Control', 'Use the remote keyboard and pointer within the approved scope.'], [FileUp, 'File Transfer', 'Exchange files in an authorized session.']] as const).map(([Icon, title, description]) => <article className="panel" key={title}><Icon size={22} aria-hidden="true" /><h2>{title}</h2><p>{description}</p></article>)}</div>
    <div className="help-strip"><Github aria-hidden="true" /><p>PeerOnQ source is MIT licensed. Dependencies retain their own licenses; see <a href={`${sourceRepository}/blob/main/THIRD_PARTY_NOTICES.md`}>Third-party notices</a>. Native platform support varies; previews are labeled in the project documentation.</p><a href={sourceRepository}>View GitHub<ArrowUpRight size={15} aria-hidden="true" /></a></div>
  </Page>;
}

export function SupportPage() {
  return <Page title="Help & support" description="Find the source, understand account access, or report a reproducible problem.">
    <section className="panel content-section"><h2>Diagnostics</h2><p>In the Windows app, open Settings → Advanced diagnostics to run Network Doctor or export a sanitized diagnostics ZIP. Review the archive before sharing it with support.</p><h2>Verified Updates</h2><p>In the Windows app, open Settings → Verified Updates. Update checks require configured trust metadata; packages must pass verification before installation.</p></section>
    <div className="overview-grid"><article className="panel"><BookOpen className="section-symbol" aria-hidden="true" /><h2>Documentation</h2><p>Setup instructions, supported platforms and current capability limits live with the source.</p><a href={`${sourceRepository}#readme`} className="text-action">Read the documentation<ArrowUpRight size={16} aria-hidden="true" /></a></article><article className="panel"><Github className="section-symbol" aria-hidden="true" /><h2>Report an issue</h2><p>Check existing issues or describe the steps to reproduce. Remove passwords, tokens and personal information.</p><a href={`${sourceRepository}/issues`} className="text-action">Open GitHub issues<ArrowUpRight size={16} aria-hidden="true" /></a></article><article className="panel"><ShieldCheck className="section-symbol" aria-hidden="true" /><h2>Security concerns</h2><p>Use the repository's security policy for responsible reporting of potential vulnerabilities.</p><a href={`${sourceRepository}/security/policy`} className="text-action">Read security policy<ArrowUpRight size={16} aria-hidden="true" /></a></article></div>
    <section className="panel content-section"><h2>Account and device access</h2><ul className="guidance-list"><li><Check aria-hidden="true" /><span>Your portal account manages profile, organization membership and browser sign-in security.</span></li><li><Check aria-hidden="true" /><span>Desktop installation identity is separate. Signing in here does not automatically add a device or authorize remote access.</span></li><li><Check aria-hidden="true" /><span>Local LAN connections remain accountless. Start and approve remote sessions in the native app.</span></li></ul><Link href="/security" className="button secondary">Review account security<ArrowRight size={16} aria-hidden="true" /></Link></section>
  </Page>;
}
