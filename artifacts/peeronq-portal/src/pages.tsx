import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError, post, put, remove } from './api';
import { useAuth } from './auth';
import { EmptyState, ErrorState, LoadingState, Notice, Page } from './components';
import { useOrganization } from './shell';
import type { Device, Invitation, Member, Organization, Policy, Profile, SecurityEvent, Session, Team, TrustedDevice } from './types';

function message(cause: unknown) { return cause instanceof ApiError ? cause.message : 'The request could not be completed.'; }
function formatDate(value: string | null) { return value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—'; }

type AuthMode = 'login' | 'register' | 'forgot';
type AuthActions = Pick<ReturnType<typeof useAuth>, 'login' | 'register'>;

async function submitAuthMode(mode: AuthMode, data: FormData, actions: AuthActions): Promise<string | null> {
  if (mode === 'login') {
    await actions.login(String(data.get('email')), String(data.get('password')), String(data.get('mfaCode') ?? ''));
    return null;
  }
  if (mode === 'register') {
    const token = new URLSearchParams(location.search).get('token') ?? undefined;
    const result = await actions.register(String(data.get('email')), String(data.get('displayName')), String(data.get('password')), token);
    return result.emailVerificationRequired
      ? 'Check the configured mail inbox to verify your account.'
      : 'Account created. You can sign in now.';
  }
  await post('/auth/password-reset/request', { email: String(data.get('email')) });
  return 'If the account exists, a reset message was sent.';
}

function authTitle(mode: AuthMode): string {
  return { login: 'Sign in', register: 'Create account', forgot: 'Reset password' }[mode];
}

function authButtonLabel(mode: AuthMode, busy: boolean): string {
  if (busy) return 'Please wait…';
  return { login: 'Sign in', register: 'Create account', forgot: 'Send reset message' }[mode];
}

function AuthForm({ mode, mfa, busy, onSubmit }: {
  mode: AuthMode; mfa: boolean; busy: boolean;
  onSubmit(event: FormEvent<HTMLFormElement>): void;
}) {
  return <form onSubmit={onSubmit}>
    {mode === 'register' ? <label>Display name<input name="displayName" required maxLength={128} autoComplete="name" /></label> : null}
    <label>Email<input name="email" type="email" required maxLength={320} autoComplete="email" /></label>
    {mode !== 'forgot' ? <label>Password<input name="password" type="password" required minLength={12} maxLength={128}
      autoComplete={mode === 'login' ? 'current-password' : 'new-password'} /></label> : null}
    {mode === 'login' && mfa ? <label>MFA or recovery code<input name="mfaCode" required autoComplete="one-time-code" /></label> : null}
    <button className="button primary full" disabled={busy}>{authButtonLabel(mode, busy)}</button>
  </form>;
}

function AuthLinks({ mode, onMode }: { mode: AuthMode; onMode(mode: AuthMode): void }) {
  if (mode !== 'login') {
    return <div className="auth-links"><button className="link-button" onClick={() => onMode('login')}>Back to sign in</button></div>;
  }
  return <div className="auth-links">
    <button className="link-button" onClick={() => onMode('register')}>Create account</button>
    <button className="link-button" onClick={() => onMode('forgot')}>Forgot password?</button>
  </div>;
}

export function AuthPage() {
  const { login, register, error: serviceError, reload } = useAuth();
  const [mode, setMode] = useState<AuthMode>('login');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [mfa, setMfa] = useState(false);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); setBusy(true); setError(null); setNotice(null);
    try {
      const nextNotice = await submitAuthMode(mode, new FormData(event.currentTarget), { login, register });
      setNotice(nextNotice);
      if (mode !== 'login') setMode('login');
    } catch (cause) {
      if (cause instanceof ApiError && cause.code === 'mfa_required') {
        setMfa(true); setError('Enter your authenticator or recovery code.');
      } else setError(message(cause));
    } finally { setBusy(false); }
  };
  return <main className="auth-screen"><section className="auth-layout">
    <div className="auth-intro"><div className="brand large"><span className="brand-mark">P</span><span><strong>PeerOnQ</strong><small>Account Portal</small></span></div><div><h1>Manage people and shared devices without a commercial license.</h1><p>Self-hosted identity, organizations, teams and transparent security controls. Local LAN access still works without an account.</p></div></div>
    <div className="auth-card"><h2>{authTitle(mode)}</h2><p>Customer accounts are isolated from server administrator accounts.</p>
      {serviceError ? <Notice tone="danger">{serviceError} <button className="link-button" onClick={() => void reload()}>Retry</button></Notice> : null}
      {notice ? <Notice tone="success">{notice}</Notice> : null}
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <AuthForm mode={mode} mfa={mfa} busy={busy} onSubmit={(event) => void submit(event)} />
      <AuthLinks mode={mode} onMode={setMode} />
    </div>
  </section></main>;
}

export function VerifyEmailPage() {
  const token = new URLSearchParams(location.search).get('token') ?? '';
  const started = useRef(false); const [state, setState] = useState<'loading' | 'done' | 'error'>('loading');
  useEffect(() => { if (started.current) return; started.current = true; void post('/auth/verify-email', { token }).then(() => setState('done')).catch(() => setState('error')); }, [token]);
  return <main className="standalone">{state === 'loading' ? <LoadingState label="Verifying your account…" /> : state === 'done' ? <Notice tone="success">Email verified. <a href="/">Sign in</a>.</Notice> : <ErrorState message="This verification link is invalid, expired, or already used." />}</main>;
}

export function ResetPasswordPage() {
  const token = new URLSearchParams(location.search).get('token') ?? ''; const [state, setState] = useState<'form' | 'done'>('form'); const [error, setError] = useState<string | null>(null);
  const submit = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const data = new FormData(event.currentTarget); try { await post('/auth/password-reset/complete', { token, newPassword: String(data.get('password')) }); setState('done'); } catch (cause) { setError(message(cause)); } };
  return <main className="standalone">{state === 'done' ? <Notice tone="success">Password changed and existing sessions revoked. <a href="/">Sign in</a>.</Notice> : <form className="panel compact-form" onSubmit={(event) => void submit(event)}><h1>Choose a new password</h1>{error ? <Notice tone="danger">{error}</Notice> : null}<label>New password<input name="password" type="password" minLength={12} maxLength={128} required autoComplete="new-password" /></label><button className="button primary">Reset password</button></form>}</main>;
}

export function ProfilePage() {
  const { profile, reload } = useAuth(); const [error, setError] = useState<string | null>(null); const [saved, setSaved] = useState(false);
  const submit = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); try { await put('/account/profile', { displayName: String(new FormData(event.currentTarget).get('displayName')) }); await reload(); setSaved(true); } catch (cause) { setError(message(cause)); } };
  return <Page title="Account profile" description="Your customer identity. Server administrator identities are managed separately."><form className="panel form-grid" onSubmit={(event) => void submit(event)}>{saved ? <Notice tone="success">Profile saved.</Notice> : null}{error ? <Notice tone="danger">{error}</Notice> : null}<label>Display name<input name="displayName" defaultValue={profile?.displayName} required maxLength={128} /></label><label>Email<input value={profile?.email ?? ''} readOnly /></label><div className="facts"><span>Email verification<strong>{profile?.emailVerified ? 'Verified' : 'Pending'}</strong></span><span>MFA<strong>{profile?.mfaEnabled ? 'Enabled' : 'Not enabled'}</strong></span><span>Created<strong>{formatDate(profile?.createdAtUtc ?? null)}</strong></span></div><button className="button primary">Save profile</button></form></Page>;
}

function useLoad<T>(loader: () => Promise<T>, dependencies: unknown[]) {
  const [data, setData] = useState<T | null>(null); const [error, setError] = useState<string | null>(null); const [version, setVersion] = useState(0);
  const reload = useCallback(() => setVersion((value) => value + 1), []);
  useEffect(() => { let active = true; setData(null); setError(null); void loader().then((value) => { if (active) setData(value); }).catch((cause) => { if (active) setError(message(cause)); }); return () => { active = false; }; }, [...dependencies, version]);
  return { data, error, reload };
}

export function SessionsPage() {
  const state = useLoad(() => api<Session[]>('/account/sessions'), []);
  return <Page title="Account sessions" description="Review and revoke browser sessions. Refresh-token families are rotated and replay-protected.">{state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No sessions">No account sessions were found.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.id}><h2>{item.userAgentSummary}</h2><p>Created {formatDate(item.createdAtUtc)} · expires {formatDate(item.expiresAtUtc)}</p><span className={item.revokedAtUtc ? 'badge muted' : 'badge success'}>{item.revokedAtUtc ? 'Revoked' : 'Active'}</span>{!item.revokedAtUtc ? <button className="button danger-outline" onClick={() => void remove(`/account/sessions/${item.id}`).then(state.reload)}>Revoke</button> : null}</article>)}</div>}</Page>;
}

export function TrustedDevicesPage() {
  const state = useLoad(() => api<TrustedDevice[]>('/account/trusted-devices'), []);
  return <Page title="Trusted devices" description="Account sign-in trust is explicit, expiring and revocable.">{state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No trusted devices">No browser or account device has persistent trust.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.id}><h2>{item.name}</h2><p>Expires {formatDate(item.expiresAtUtc)}</p>{!item.revokedAtUtc ? <button className="button danger-outline" onClick={() => void remove(`/account/trusted-devices/${item.id}`).then(state.reload)}>Revoke</button> : <span className="badge muted">Revoked</span>}</article>)}</div>}</Page>;
}

export function OrganizationsPage() {
  const { organizations, reload, loading } = useOrganization(); const [error, setError] = useState<string | null>(null);
  const create = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); try { await post('/organizations/', { name: String(new FormData(event.currentTarget).get('name')) }); event.currentTarget.reset(); await reload(); } catch (cause) { setError(message(cause)); } };
  return <Page title="Organizations" description="Tenant boundaries for shared devices, teams, support metadata and policy." actions={<a className="button secondary" href="/policy">Security policy</a>}><form className="inline-form panel" onSubmit={(event) => void create(event)}><label>New organization name<input name="name" required maxLength={128} /></label><button className="button primary">Create</button></form>{error ? <Notice tone="danger">{error}</Notice> : null}{loading ? <LoadingState /> : !organizations.length ? <EmptyState title="No organizations">Create an organization to share resources.</EmptyState> : <div className="cards">{organizations.map((item) => <article className="card" key={item.id}><h2>{item.name}</h2><p>Role: {item.role}</p><span className="badge">Tenant {item.id.slice(0, 8)}</span></article>)}</div>}</Page>;
}

export function DevicesPage() {
  const { selected, loading } = useOrganization(); const state = useLoad(() => selected ? api<Device[]>(`/organizations/${selected.id}/devices`) : Promise.resolve([]), [selected?.id]);
  return <Page title="Organization devices" description="Only devices assigned to the active tenant are returned by the API.">{loading || !state.data ? <LoadingState /> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data.length ? <EmptyState title="No managed devices">Accountless LAN devices stay local until explicitly assigned to an organization.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.id}><h2>{item.displayName}</h2><p>{item.maskedPublicDeviceId} · last seen {formatDate(item.lastSeenAtUtc)}</p><span className={item.isRevoked ? 'badge muted' : 'badge success'}>{item.isRevoked ? 'Revoked' : 'Active'}</span></article>)}</div>}</Page>;
}

export function TeamsPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Team[]>(`/organizations/${selected.id}/teams`) : Promise.resolve([]), [selected?.id]); const [error, setError] = useState<string | null>(null);
  const create = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); if (!selected) return; try { await post(`/organizations/${selected.id}/teams`, { name: String(new FormData(event.currentTarget).get('name')) }); event.currentTarget.reset(); state.reload(); } catch (cause) { setError(message(cause)); } };
  return <Page title="Teams" description="Group organization members for managed support workflows."><form className="inline-form panel" onSubmit={(event) => void create(event)}><label>Team name<input name="name" required maxLength={128} /></label><button className="button primary" disabled={!selected}>Create team</button></form>{error ? <Notice tone="danger">{error}</Notice> : null}{!state.data ? <LoadingState /> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data.length ? <EmptyState title="No teams">Create the first team in this organization.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.id}><h2>{item.name}</h2><p>Created {formatDate(item.createdAtUtc)}</p></article>)}</div>}</Page>;
}

export function InvitationsPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Invitation[]>(`/organizations/${selected.id}/invitations`) : Promise.resolve([]), [selected?.id]); const [error, setError] = useState<string | null>(null);
  const invite = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); if (!selected) return; const data = new FormData(event.currentTarget); try { await post(`/organizations/${selected.id}/invitations`, { email: data.get('email'), role: data.get('role') }); event.currentTarget.reset(); state.reload(); } catch (cause) { setError(message(cause)); } };
  return <Page title="Invitations" description="Invitation tokens are random, short-lived, email-bound and stored only as hashes."><form className="inline-form panel" onSubmit={(event) => void invite(event)}><label>Email<input name="email" type="email" required /></label><label>Role<select name="role"><option>Member</option><option>Technician</option><option>Auditor</option><option>Administrator</option></select></label><button className="button primary" disabled={!selected}>Invite</button></form>{error ? <Notice tone="danger">{error}</Notice> : null}{!state.data ? <LoadingState /> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data.length ? <EmptyState title="No invitations">No invitations have been issued.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.id}><h2>{item.email}</h2><p>{item.role} · expires {formatDate(item.expiresAtUtc)}</p><span className="badge">{item.acceptedAtUtc ? 'Accepted' : item.revokedAtUtc ? 'Revoked' : 'Pending'}</span>{!item.acceptedAtUtc && !item.revokedAtUtc ? <button className="button danger-outline" onClick={() => selected && void remove(`/organizations/${selected.id}/invitations/${item.id}`).then(state.reload)}>Revoke</button> : null}</article>)}</div>}</Page>;
}

export function AcceptInvitationPage() {
  const token = new URLSearchParams(location.search).get('token') ?? ''; const started = useRef(false); const [state, setState] = useState<'loading' | 'done' | 'error'>('loading'); const { reload } = useOrganization();
  useEffect(() => { if (started.current) return; started.current = true; void post('/organizations/invitations/accept', { token }).then(async () => { await reload(); setState('done'); }).catch(() => setState('error')); }, [token]);
  return <Page title="Accept invitation" description="Join the organization with the role assigned by its administrator.">{state === 'loading' ? <LoadingState /> : state === 'done' ? <Notice tone="success">Invitation accepted. Your active organizations were refreshed.</Notice> : <ErrorState message="This invitation is invalid, expired, revoked, already used, or belongs to another account." />}</Page>;
}

export function SecurityPage() {
  const { profile, reload } = useAuth(); const [setup, setSetup] = useState<{ secret: string; setupToken: string; otpAuthUri: string } | null>(null); const [codes, setCodes] = useState<string[] | null>(null); const [error, setError] = useState<string | null>(null);
  const begin = async () => { try { setSetup(await post('/account/mfa/setup')); } catch (cause) { setError(message(cause)); } };
  const confirm = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); if (!setup) return; try { const result = await post<{ recoveryCodes: string[] }>('/account/mfa/confirm', { setupToken: setup.setupToken, code: String(new FormData(event.currentTarget).get('code')) }); setCodes(result.recoveryCodes); setSetup(null); await reload(); } catch (cause) { setError(message(cause)); } };
  return <Page title="Security settings" description="MFA secrets are protected at rest; recovery codes are shown once and stored only as hashes.">{error ? <Notice tone="danger">{error}</Notice> : null}<div className="panel"><h2>Multi-factor authentication</h2><p>Status: <strong>{profile?.mfaEnabled ? 'Enabled' : 'Not enabled'}</strong></p>{!profile?.mfaEnabled && !setup ? <button className="button primary" onClick={() => void begin()}>Set up MFA</button> : null}{setup ? <form className="form-grid" onSubmit={(event) => void confirm(event)}><Notice>Enter this secret in an authenticator: <code>{setup.secret}</code></Notice><label>6-digit code<input name="code" inputMode="numeric" pattern="[0-9]{6}" required autoComplete="one-time-code" /></label><button className="button primary">Confirm MFA</button></form> : null}{codes ? <Notice tone="success"><strong>Save these one-time recovery codes now:</strong><pre>{codes.join('\n')}</pre></Notice> : null}</div></Page>;
}

export function PolicyPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Policy>(`/organizations/${selected.id}/policy`) : Promise.resolve(null as unknown as Policy), [selected?.id]); const [saved, setSaved] = useState(false); const [error, setError] = useState<string | null>(null);
  const submit = async (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); if (!selected || !state.data) return; const data = new FormData(event.currentTarget); const value = (name: string) => data.get(name) === 'on'; try { await put(`/organizations/${selected.id}/policy`, { ...state.data, viewOnlyAllowed: value('viewOnlyAllowed'), fullControlAllowed: value('fullControlAllowed'), fileTransferAllowed: value('fileTransferAllowed'), clipboardAllowed: value('clipboardAllowed'), unattendedAccessAllowed: value('unattendedAccessAllowed'), mfaRequired: value('mfaRequired'), hybridSecurityRequired: value('hybridSecurityRequired'), trustedDeviceLifetimeDays: Number(data.get('trustedDeviceLifetimeDays')), auditRetentionDays: Number(data.get('auditRetentionDays')), approvedRelayRegionsCsv: data.get('approvedRelayRegionsCsv'), minimumClientVersion: data.get('minimumClientVersion') }); setSaved(true); state.reload(); } catch (cause) { setError(message(cause)); } };
  return <Page title="Organization policy" description="Server-enforced operational controls. These settings are not commercial entitlements.">{!state.data ? <LoadingState /> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : <form className="panel form-grid" onSubmit={(event) => void submit(event)}>{saved ? <Notice tone="success">Policy saved.</Notice> : null}{error ? <Notice tone="danger">{error}</Notice> : null}<div className="check-grid">{['viewOnlyAllowed','fullControlAllowed','fileTransferAllowed','clipboardAllowed','unattendedAccessAllowed','mfaRequired','hybridSecurityRequired'].map((name) => <label className="check" key={name}><input name={name} type="checkbox" defaultChecked={Boolean(state.data?.[name as keyof Policy])} />{name.replace(/([A-Z])/g, ' $1')}</label>)}</div><label>Trusted-device lifetime (days)<input name="trustedDeviceLifetimeDays" type="number" min="1" max="3650" defaultValue={state.data.trustedDeviceLifetimeDays} /></label><label>Audit retention (days)<input name="auditRetentionDays" type="number" min="1" max="3650" defaultValue={state.data.auditRetentionDays} /></label><label>Approved relay regions, comma separated<input name="approvedRelayRegionsCsv" defaultValue={state.data.approvedRelayRegionsCsv} /></label><label>Minimum client version<input name="minimumClientVersion" defaultValue={state.data.minimumClientVersion} /></label><button className="button primary">Save policy</button></form>}</Page>;
}

export function AuditPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<SecurityEvent[]>(`/organizations/${selected.id}/audit`) : Promise.resolve([]), [selected?.id]);
  return <Page title="Security and audit events" description="Append-only, tenant-scoped metadata. Passwords, tokens, screen, input, clipboard and file contents are never recorded." actions={selected ? <a className="button secondary" href={`/portal/v1/organizations/${selected.id}/audit/export`}>Export JSON</a> : null}>{!state.data ? <LoadingState /> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data.length ? <EmptyState title="No audit events">No organization security events were recorded.</EmptyState> : <div className="table-wrap" tabIndex={0}><table><thead><tr><th>Time</th><th>Action</th><th>Result</th><th>Correlation</th></tr></thead><tbody>{state.data.map((item) => <tr key={item.id}><td>{formatDate(item.timestampUtc)}</td><td>{item.action}</td><td>{item.result}</td><td><code>{item.correlationId}</code></td></tr>)}</tbody></table></div>}</Page>;
}

export function PrivacyPage() {
  const [notice, setNotice] = useState<string | null>(null); const [error, setError] = useState<string | null>(null);
  const request = async (kind: 'Export' | 'Delete') => { if (kind === 'Delete' && !confirm('Request account deletion? Owned organizations must be transferred first.')) return; try { await post('/account/data-requests', { kind }); setNotice(`${kind} request accepted.`); } catch (cause) { setError(message(cause)); } };
  return <Page title="Privacy and data" description="Telemetry is operationally controlled and account data has transparent retention and lifecycle operations.">{notice ? <Notice tone="success">{notice}</Notice> : null}{error ? <Notice tone="danger">{error}</Notice> : null}<div className="cards"><article className="card"><h2>Data export</h2><p>Request an export of stored account and organization metadata.</p><button className="button secondary" onClick={() => void request('Export')}>Request export</button></article><article className="card"><h2>Delete account</h2><p>Deletion is guarded while you own an organization and revokes active sessions when accepted.</p><button className="button danger-outline" onClick={() => void request('Delete')}>Request deletion</button></article><article className="card"><h2>Data inventory</h2><p>Account identity, session metadata, organization membership, device ownership, security policy and redacted audit metadata. No screen, keystroke, clipboard or transferred-file content.</p></article></div></Page>;
}

export function MembersPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Member[]>(`/organizations/${selected.id}/members`) : Promise.resolve([]), [selected?.id]);
  return <Page title="Members" description="Roles are tenant-scoped and separate from internal server administrator roles.">{!state.data ? <LoadingState /> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : <div className="cards">{state.data.map((item) => <article className="card" key={item.accountId}><h2>{item.displayName}</h2><p>{item.email}</p><span className="badge">{item.role}</span></article>)}</div>}</Page>;
}
