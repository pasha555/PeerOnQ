import { ArrowRight, ArrowUpRight, BookOpen, Check, Download, FileUp, Github, Laptop, Monitor, MousePointer2, ShieldCheck, UsersRound } from 'lucide-react';
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Link } from 'wouter';
import { api, ApiError } from './api';
import { useAuth } from './auth';
import { publicWebsite, sourceRepository } from './brand';
import { EmptyState, ErrorState, LoadingState, Page } from './components';
import { useOrganization } from './shell';
import type { Device, RemoteSession } from './types';

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

function useOrganizationResource<T>(resource: string) {
  const { selected, loading: organizationLoading, error: organizationError } = useOrganization();
  const path = selected ? `/organizations/${encodeURIComponent(selected.id)}/${resource}` : null;
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
  return { ...current, loading: organizationLoading || Boolean(path && !current.data && !current.error), selected, organizationError, reload };
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

export function OverviewPage() {
  const { profile } = useAuth();
  const { selected, loading, error } = useOrganization();
  const devices = useOrganizationResource<Device>('devices');
  const sessions = useOrganizationResource<RemoteSession>('sessions');
  return <Page title="Overview" description={profile ? `Welcome, ${profile.displayName}. Your account and shared workspace in one place.` : 'Your account and shared workspace in one place.'} actions={<Link href="/downloads" className="button primary"><Download size={17} aria-hidden="true" />Get PeerOnQ</Link>}>
    <section className="workspace-banner" aria-label="Current workspace">
      <div><span className="eyebrow">{selected ? 'Organization workspace' : 'Personal account'}</span><h2>{selected?.name ?? profile?.displayName ?? 'Your account'}</h2><p>{selected ? `Your role: ${selected.role}. Devices and history below belong to this organization.` : 'Manage your profile and sign-in security. Shared resources belong to an organization.'}</p></div>
      <Link href="/account" className="button secondary">My account<ArrowRight size={16} aria-hidden="true" /></Link>
    </section>
    <div className="overview-grid">
      <article className="panel overview-card"><span className="section-icon"><Laptop aria-hidden="true" /></span><h2>Devices</h2>
        {devices.loading ? <p role="status">Loading assigned devices…</p> : error || devices.error ? <p>Device information is unavailable.</p> : <><strong className="summary-value">{devices.data?.length ?? 0}</strong><p>{selected ? 'Devices assigned to this organization' : 'No organization selected'}</p></>}
        <Link href="/devices" className="text-action">Manage devices<ArrowRight size={16} aria-hidden="true" /></Link>
      </article>
      <article className="panel overview-card"><span className="section-icon"><ShieldCheck aria-hidden="true" /></span><h2>Account protection</h2>
        <strong className="summary-status">{profile?.mfaEnabled ? 'MFA enabled' : 'Set up MFA'}</strong><p>{profile?.mfaEnabled ? 'An additional check protects your sign-in.' : 'Add an authenticator to protect your sign-in.'}</p><Link href="/security" className="text-action">Review security<ArrowRight size={16} aria-hidden="true" /></Link>
      </article>
      <article className="panel overview-card"><span className="section-icon"><Monitor aria-hidden="true" /></span><h2>Start a connection</h2><strong className="summary-status">Open the desktop app</strong><p>Choose View Only, Full Control or File Transfer. The remote owner approves attended access.</p><Link href="/downloads" className="text-action">View downloads<ArrowRight size={16} aria-hidden="true" /></Link></article>
    </div>
    {!loading && !selected && !error ? <NoOrganization /> : null}
    <section className="content-section" aria-labelledby="recent-sessions"><div className="section-heading"><div><span className="eyebrow">Activity</span><h2 id="recent-sessions">Recent remote sessions</h2></div><Link href="/remote-sessions" className="text-action">View history<ArrowRight size={16} aria-hidden="true" /></Link></div>
      {error ? <p>Remote-session history is unavailable until your organizations can be loaded.</p> : sessions.error ? <ErrorState message={sessions.error} retry={sessions.reload} /> : sessions.loading ? <LoadingState label="Loading remote-session history…" /> : !sessions.data?.length ? <div className="panel"><EmptyState title="No remote sessions yet">{selected ? 'Recorded sessions hosted by devices assigned to this organization will appear here.' : 'Select an organization to view its remote-session history.'}</EmptyState></div> : <SessionsTable sessions={sessions.data.slice(0, 5)} />}
    </section>
    <div className="help-strip"><BookOpen aria-hidden="true" /><p>Local LAN connections work without an account. The portal manages account and organization metadata.</p><Link href="/support">How it works</Link></div>
  </Page>;
}

export function DevicesPage() {
  const state = useOrganizationResource<Device>('devices');
  return <Page title="Devices" description="Devices assigned to your selected organization. Personal LAN connections remain in the desktop app." actions={<Link href="/downloads" className="button secondary"><Download size={17} aria-hidden="true" />Get the app</Link>}>
    {state.organizationError ? <p>Restore your organization list to view its devices.</p> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : state.loading ? <LoadingState label="Loading organization devices…" /> : !state.selected ? <NoOrganization /> : !state.data?.length ? <div className="panel"><EmptyState title="No assigned devices">This organization has no assigned devices. Desktop devices do not appear automatically when you sign in to this portal.</EmptyState><p className="empty-guidance">Device assignment requires verified ownership. There is currently no self-service desktop sign-in or device-linking flow. Contact your organization administrator for an existing managed setup.</p></div> : <div className="cards">{state.data.map((item) => <article className="card device-card" key={item.id}><div className="card-heading"><span className="section-icon"><Laptop aria-hidden="true" /></span><span className={item.isRevoked ? 'badge muted' : 'badge success'}>{item.isRevoked ? 'Revoked' : 'Assigned'}</span></div><h2>{item.displayName}</h2><code className="device-id">{item.maskedPublicDeviceId}</code><dl className="detail-list"><div><dt>Last seen</dt><dd>{date(item.lastSeenAtUtc)}</dd></div></dl></article>)}</div>}
  </Page>;
}

export function RemoteSessionsPage() {
  const state = useOrganizationResource<RemoteSession>('sessions');
  return <Page title="Sessions" description="Recorded remote sessions hosted by devices assigned to your selected organization. This is connection metadata, not a screen recording." actions={<Link href="/sessions" className="button secondary">Browser sessions<ArrowRight size={16} aria-hidden="true" /></Link>}>
    {state.organizationError ? <p>Restore your organization list to view its remote sessions.</p> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : state.loading ? <LoadingState label="Loading remote-session history…" /> : !state.selected ? <NoOrganization /> : !state.data?.length ? <div className="panel"><EmptyState title="No remote sessions yet">Session history will appear after an assigned host device records a remote connection.</EmptyState></div> : <><p className="table-caption">{state.data.length} recorded {state.data.length === 1 ? 'session' : 'sessions'} · up to the 500 most recent</p><SessionsTable sessions={state.data} /></>}
  </Page>;
}

export function DownloadsPage() {
  return <Page title="Downloads" description="Remote connections run in the native PeerOnQ app. The public download page lists the packages currently published for your platform.">
    <section className="panel download-panel"><span className="download-icon"><Monitor aria-hidden="true" /></span><div><span className="eyebrow">PeerOnQ native app</span><h2>Your connection starts on your device.</h2><p>Use the desktop app for View Only, Full Control and File Transfer. Unattended Access requires a separate setup on a supported host.</p><a className="button primary" href={`${publicWebsite}/#download`}><Download size={17} aria-hidden="true" />View available downloads<ArrowUpRight size={16} aria-hidden="true" /></a><p className="download-note">Published packages and known release details are shown on the public download page. Platform limits are documented in the repository.</p></div></section>
    <div className="capability-grid">{([[Monitor, 'View Only', 'See the remote screen with approval.'], [MousePointer2, 'Full Control', 'Use the remote keyboard and pointer within the approved scope.'], [FileUp, 'File Transfer', 'Exchange files in an authorized session.']] as const).map(([Icon, title, description]) => <article className="panel" key={title}><Icon size={22} aria-hidden="true" /><h2>{title}</h2><p>{description}</p></article>)}</div>
    <div className="help-strip"><Github aria-hidden="true" /><p>MIT-licensed source. Native platform support varies; previews are labeled in the project documentation.</p><a href={sourceRepository}>View GitHub<ArrowUpRight size={15} aria-hidden="true" /></a></div>
  </Page>;
}

export function SupportPage() {
  return <Page title="Help & support" description="Find the source, understand account access, or report a reproducible problem.">
    <div className="overview-grid"><article className="panel"><BookOpen className="section-symbol" aria-hidden="true" /><h2>Documentation</h2><p>Setup instructions, supported platforms and current capability limits live with the source.</p><a href={`${sourceRepository}#readme`} className="text-action">Read the documentation<ArrowUpRight size={16} aria-hidden="true" /></a></article><article className="panel"><Github className="section-symbol" aria-hidden="true" /><h2>Report an issue</h2><p>Check existing issues or describe the steps to reproduce. Remove passwords, tokens and personal information.</p><a href={`${sourceRepository}/issues`} className="text-action">Open GitHub issues<ArrowUpRight size={16} aria-hidden="true" /></a></article><article className="panel"><ShieldCheck className="section-symbol" aria-hidden="true" /><h2>Security concerns</h2><p>Use the repository's security policy for responsible reporting of potential vulnerabilities.</p><a href={`${sourceRepository}/security/policy`} className="text-action">Read security policy<ArrowUpRight size={16} aria-hidden="true" /></a></article></div>
    <section className="panel content-section"><h2>Account and device access</h2><ul className="guidance-list"><li><Check aria-hidden="true" /><span>Your portal account manages profile, organization membership and browser sign-in security.</span></li><li><Check aria-hidden="true" /><span>Desktop installation identity is separate. Signing in here does not automatically add a device or authorize remote access.</span></li><li><Check aria-hidden="true" /><span>Local LAN connections remain accountless. Start and approve remote sessions in the native app.</span></li></ul><Link href="/security" className="button secondary">Review account security<ArrowRight size={16} aria-hidden="true" /></Link></section>
  </Page>;
}
