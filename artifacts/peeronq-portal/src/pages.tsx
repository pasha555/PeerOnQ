import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Laptop, ShieldCheck, UsersRound } from 'lucide-react';
import { Link } from 'wouter';
import { api, ApiError, post, put, remove } from './api';
import { useAuth } from './auth';
import { AuthFrame } from './brand';
import { NewPasswordFields, PasswordField, passwordError } from './passwordFields';
import { ConfirmAction, EmptyState, ErrorState, LoadingState, Notice, Page } from './components';
import { useOrganization } from './shell';
import type { Invitation, Member, Organization, Policy, Profile, SecurityEvent, Session, Team, TrustedDevice } from './types';

function message(cause: unknown) { return cause instanceof ApiError ? cause.message : 'The request could not be completed.'; }
function formatDate(value: string | null) { return value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)) : '—'; }

type AuthMode = 'login' | 'register' | 'forgot' | 'resend';
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
      ? 'Check your inbox to verify your account, then sign in.'
      : 'Account created. You can sign in now.';
  }
  await post(mode === 'resend' ? '/auth/verify-email/resend' : '/auth/password-reset/request', { email: String(data.get('email')) });
  return 'Request accepted. If your account is eligible, check your inbox. If no message arrives, try again later.';
}

function authTitle(mode: AuthMode): string {
  return { login: 'Sign in', register: 'Create account', forgot: 'Reset password', resend: 'Resend verification' }[mode];
}

function authButtonLabel(mode: AuthMode, busy: boolean): string {
  if (busy) return 'Please wait…';
  return { login: 'Sign in', register: 'Create account', forgot: 'Send reset message', resend: 'Send verification message' }[mode];
}

function AuthForm({ mode, mfa, busy, onSubmit }: {
  mode: AuthMode; mfa: boolean; busy: boolean;
  onSubmit(event: FormEvent<HTMLFormElement>): void;
}) {
  const form = useRef<HTMLFormElement>(null);
  useEffect(() => { form.current?.querySelector('input')?.focus(); }, [mode]);
  return <form ref={form} key={mode} onSubmit={onSubmit} aria-busy={busy}>
    {mode === 'register' ? <label>Display name<input name="displayName" required maxLength={128} autoComplete="name" /></label> : null}
    <label>Email<input name="email" type="email" required maxLength={320} autoComplete="email" /></label>
    {mode === 'login' ? <PasswordField name="password" label="Password" autoComplete="current-password" /> : null}
    {mode === 'register' ? <NewPasswordFields label="Password" /> : null}
    {mode === 'login' && mfa ? <label>MFA or recovery code<input name="mfaCode" required autoComplete="one-time-code" autoFocus /></label> : null}
    <button className="button primary full" disabled={busy}>{authButtonLabel(mode, busy)}</button>
  </form>;
}

function AuthLinks({ mode, busy, onMode }: { mode: AuthMode; busy: boolean; onMode(mode: AuthMode): void }) {
  const { capabilities } = useAuth();
  const canRegister = capabilities?.registrationAvailable && (capabilities.registrationMode === 'Open' || Boolean(new URLSearchParams(location.search).get('token')));
  if (mode !== 'login') {
    return <div className="auth-links"><button className="link-button" disabled={busy} onClick={() => onMode('login')}>Back to sign in</button></div>;
  }
  return <div className="auth-links">
    {canRegister ? <button className="link-button" disabled={busy} onClick={() => onMode('register')}>Create account</button> : <p className="field-help">{capabilities?.registrationMode === 'InvitationOnly' ? 'An invitation is required to create an account. Open the link from your organization.' : 'New account registration is currently closed.'}</p>}
    {capabilities?.passwordResetAvailable ? <button className="link-button" disabled={busy} onClick={() => onMode('forgot')}>Forgot password?</button> : <p className="field-help">Email recovery is unavailable. Contact your service operator.</p>}
    {capabilities?.requireEmailVerification && capabilities.passwordResetAvailable ? <button className="link-button" disabled={busy} onClick={() => onMode('resend')}>Resend verification</button> : null}
  </div>;
}

export function AuthPage() {
  const { login, register, error: serviceError, reload, capabilities, capabilitiesError, reloadCapabilities } = useAuth();
  const [mode, setMode] = useState<AuthMode>('login');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [mfa, setMfa] = useState(false);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); setBusy(true); setError(null); setNotice(null);
    try {
      const data = new FormData(event.currentTarget);
      if (mode === 'register') { const invalid = passwordError(data, capabilities?.passwordRules); if (invalid) { setError(invalid); return; } }
      const nextNotice = await submitAuthMode(mode, data, { login, register });
      setNotice(nextNotice);
      if (mode !== 'login') setMode('login');
    } catch (cause) {
      if (capabilities?.mfaAvailable && cause instanceof ApiError && cause.code === 'mfa_required') {
        setMfa(true); setError('Enter your authenticator or recovery code.');
      } else setError(message(cause));
    } finally { setBusy(false); }
  };
  const changeMode = (next: AuthMode) => { setMode(next); setMfa(false); setError(null); setNotice(null); };
  return <AuthFrame><section className="auth-layout">
    <div className="auth-intro"><div><span className="eyebrow">Your PeerOnQ workspace</span><h1>A clear view of your devices and access.</h1><p>Manage your account and your organization’s shared resources from one place.</p></div><ul className="auth-benefits"><li><Laptop aria-hidden="true" /><div><strong>Devices & sessions</strong><span>Review managed devices and remote session history.</span></div></li><li><UsersRound aria-hidden="true" /><div><strong>Your organization</strong><span>Keep members, teams and access settings together.</span></div></li><li><ShieldCheck aria-hidden="true" /><div><strong>Account security</strong><span>Manage your password, sign-in sessions and trusted sign-in devices.</span></div></li></ul><p className="auth-note">Remote connections run in the native app. Local LAN access works without an account.</p></div>
    <div className="auth-card"><span className="eyebrow">Account portal</span><h2>{authTitle(mode)}</h2><p>{mode === 'login' ? 'Welcome back. Use your PeerOnQ account to continue.' : mode === 'register' ? 'Create your account to manage a shared workspace.' : mode === 'resend' ? 'Request a new verification link. Previous links will expire.' : 'Request an email link to reset your password.'}</p>
      {serviceError ? <Notice tone="danger">{serviceError} <button className="link-button" onClick={() => void reload()}>Retry</button></Notice> : null}
      {notice ? <Notice tone="success">{notice}</Notice> : null}
      {error ? <Notice tone="danger">{error}</Notice> : null}
      {capabilitiesError ? <ErrorState message={capabilitiesError} retry={() => void reloadCapabilities()} /> : !capabilities ? <LoadingState label="Loading sign-in options..." /> : <>
        <AuthForm mode={mode} mfa={mfa && capabilities.mfaAvailable} busy={busy} onSubmit={(event) => void submit(event)} />
        <AuthLinks mode={mode} busy={busy} onMode={changeMode} />
      </>}
    </div>
  </section></AuthFrame>;
}

export function VerifyEmailPage() {
  const token = new URLSearchParams(location.search).get('token') ?? '';
  const started = useRef(false); const [state, setState] = useState<'loading' | 'done' | 'error'>('loading');
  useEffect(() => { if (started.current) return; started.current = true; void post('/auth/verify-email', { token }).then(() => setState('done')).catch(() => setState('error')); }, [token]);
  return <AuthFrame><section className="auth-card auth-single"><span className="eyebrow">Account security</span><h1>Verify your email</h1>{state === 'loading' ? <LoadingState label="Verifying your account…" /> : state === 'done' ? <Notice tone="success">Email verified. You can now sign in.</Notice> : <ErrorState message="This verification link is invalid, expired, or already used." />}<a className="button secondary" href="/">Back to sign in</a></section></AuthFrame>;
}

export function ResetPasswordPage() {
  const { capabilities } = useAuth();
  const token = new URLSearchParams(location.search).get('token') ?? '';
  const [done, setDone] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const data = new FormData(event.currentTarget); setError(null); setBusy(true);
    try { const invalid = passwordError(data, capabilities?.passwordRules); if (invalid) { setError(invalid); return; } await post('/auth/password-reset/complete', { token, newPassword: String(data.get('password')) }); setDone(true); }
    catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <AuthFrame><section className="auth-card auth-single"><span className="eyebrow">Account security</span><h1>Choose a new password</h1>
    {done ? <Notice tone="success">Password changed and existing sessions revoked.</Notice> : <form onSubmit={(event) => void submit(event)} aria-busy={busy}>{error ? <Notice tone="danger">{error}</Notice> : null}<NewPasswordFields /><p className="field-help">Changing your password revokes existing sign-in sessions.</p><button className="button primary full" disabled={busy}>{busy ? 'Resetting…' : 'Reset password'}</button></form>}
    <a className="text-action" href="/">Back to sign in</a>
  </section></AuthFrame>;
}

export function ProfilePage() {
  const { profile, reload, capabilities } = useAuth(); const [error, setError] = useState<string | null>(null); const [saved, setSaved] = useState(false); const [busy, setBusy] = useState(false);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const displayName = String(new FormData(event.currentTarget).get('displayName')); setError(null); setSaved(false); setBusy(true);
    try { await put('/account/profile', { displayName }); await reload(); setSaved(true); } catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <Page title="Profile" description="Your name, email and account status." actions={<Link href="/security" className="button secondary">Security settings</Link>}>
    <form className="panel form-grid" onSubmit={(event) => void submit(event)} aria-busy={busy}>
      {saved ? <Notice tone="success">Profile saved.</Notice> : null}{error ? <Notice tone="danger">{error}</Notice> : null}
      <label>Display name<input name="displayName" defaultValue={profile?.displayName} required maxLength={128} autoComplete="name" aria-describedby="name-help" /></label><p id="name-help" className="field-help">The name shown in your account and organization memberships.</p>
      <label>Email<input value={profile?.email ?? ''} readOnly aria-describedby="email-help" /></label><p id="email-help" className="field-help">Your sign-in email cannot be changed from this page.</p>
      <div className="facts"><span>Email verification<strong>{profile?.emailVerified ? 'Verified' : 'Pending'}</strong></span>{capabilities?.mfaAvailable ? <span>MFA<strong>{profile?.mfaEnabled ? 'Enabled' : 'Not enabled'}</strong></span> : null}<span>Created<strong>{formatDate(profile?.createdAtUtc ?? null)}</strong></span></div><button className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Save profile'}</button>
    </form>
  </Page>;
}

function useLoad<T>(loader: () => Promise<T>, dependencies: unknown[]) {
  const [data, setData] = useState<T | null>(null); const [error, setError] = useState<string | null>(null); const [version, setVersion] = useState(0);
  const reload = useCallback(() => setVersion((value) => value + 1), []);
  useEffect(() => { let active = true; setData(null); setError(null); void loader().then((value) => { if (active) setData(value); }).catch((cause) => { if (active) setError(message(cause)); }); return () => { active = false; }; }, [...dependencies, version]);
  return { data, error, reload };
}

export function SessionsPage() {
  const state = useLoad(() => api<Session[]>('/account/sessions'), []);
  return <Page title="Sign-in sessions" description="Where your account is signed in. These sessions are separate from remote desktop connections.">
    {state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No sign-in sessions">No account sessions were found.</EmptyState> : <div className="cards">{state.data.map((item) => {
      const active = !item.revokedAtUtc && new Date(item.expiresAtUtc).getTime() > Date.now();
      return <article className="card" key={item.id}><h2>{item.userAgentSummary || 'Account session'}</h2><p>Created {formatDate(item.createdAtUtc)} · expires {formatDate(item.expiresAtUtc)}</p><span className={active ? 'badge success' : 'badge muted'}>{item.revokedAtUtc ? 'Revoked' : active ? 'Active' : 'Expired'}</span>{active ? <div><ConfirmAction label="Revoke session" title="Revoke this sign-in session?" description="This ends account access for the selected session. If it is your current session, you will need to sign in again." confirmLabel="Confirm revocation" onConfirm={async () => { await remove(`/account/sessions/${item.id}`); state.reload(); }} /></div> : null}</article>;
    })}</div>}
  </Page>;
}

export function TrustedDevicesPage() {
  const state = useLoad(() => api<TrustedDevice[]>('/account/trusted-devices'), []);
  return <Page title="Trusted sign-in devices" description="Account sign-in trust records. These are separate from managed remote-access devices and do not grant Full Control or Unattended Access." actions={<Link href="/security" className="button secondary">Account security</Link>}>
    {state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No trusted sign-in devices">No browser or account device has persistent sign-in trust.</EmptyState> : <div className="cards">{state.data.map((item) => {
      const active = !item.revokedAtUtc && new Date(item.expiresAtUtc).getTime() > Date.now();
      return <article className="card" key={item.id}><h2>{item.name}</h2><p>Expires {formatDate(item.expiresAtUtc)}</p><span className={active ? 'badge success' : 'badge muted'}>{item.revokedAtUtc ? 'Revoked' : active ? 'Trusted' : 'Expired'}</span>{active ? <div><ConfirmAction label="Revoke trust" title="Remove sign-in trust?" description="The selected device will no longer be trusted for account sign-in. Remote-access permissions are managed separately." confirmLabel="Confirm revocation" onConfirm={async () => { await remove(`/account/trusted-devices/${item.id}`); state.reload(); }} /></div> : null}</article>;
    })}</div>}
  </Page>;
}

export function OrganizationsPage() {
  const { organizations, reload, loading, error: organizationError, selected, select } = useOrganization();
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const create = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const form = event.currentTarget; const name = String(new FormData(form).get('name')); setError(null); setBusy(true);
    try { await post('/organizations/', { name }); form.reset(); await reload(); } catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <Page title="Organizations" description="Workspaces for your shared devices, members and connection policy.">
    <form className="inline-form panel" onSubmit={(event) => void create(event)} aria-busy={busy}><label>New organization name<input name="name" required maxLength={128} /></label><button className="button primary" disabled={busy}>{busy ? 'Creating...' : 'Create organization'}</button></form>
    {error ? <Notice tone="danger">{error}</Notice> : null}
    {loading ? <LoadingState /> : organizationError ? <p>Use Retry organizations above to restore your memberships.</p> : !organizations.length ? <EmptyState title="No organizations">Create an organization or use an invitation from its administrator.</EmptyState> : <div className="cards">{organizations.map((item) => <article className="card" key={item.id}><h2>{item.name}</h2><p>Role: {item.role}</p><span className="badge">{selected?.id === item.id ? 'Active organization' : 'Member organization'}</span>{selected?.id !== item.id ? <div><button className="button secondary" onClick={() => select(item.id)}>Switch to {item.name}</button></div> : null}</article>)}</div>}
  </Page>;
}

export function TeamsPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Team[]>(`/organizations/${selected.id}/teams`) : Promise.resolve([]), [selected?.id]);
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const canManage = selected?.role === 'Owner' || selected?.role === 'Administrator';
  const create = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const form = event.currentTarget; if (!selected || !canManage) return; const name = String(new FormData(form).get('name')); setError(null); setBusy(true);
    try { await post(`/organizations/${selected.id}/teams`, { name }); form.reset(); state.reload(); } catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <Page title="Teams" description="Groups of members within your selected organization.">
    {canManage ? <form className="inline-form panel" onSubmit={(event) => void create(event)} aria-busy={busy}><label>Team name<input name="name" required maxLength={128} /></label><button className="button primary" disabled={busy}>{busy ? 'Creating...' : 'Create team'}</button></form> : <Notice>An organization Owner or Administrator can create teams.</Notice>}
    {error ? <Notice tone="danger">{error}</Notice> : null}
    {state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No teams">No teams have been created in this organization.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.id}><h2>{item.name}</h2><p>Created {formatDate(item.createdAtUtc)}</p></article>)}</div>}
  </Page>;
}

export function InvitationsPage() {
  const { selected } = useOrganization(); const canManage = selected?.role === 'Owner' || selected?.role === 'Administrator';
  const state = useLoad(() => selected && canManage ? api<Invitation[]>(`/organizations/${selected.id}/invitations`) : Promise.resolve([]), [selected?.id, canManage]);
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false); const [notice, setNotice] = useState<string | null>(null);
  const invite = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const form = event.currentTarget; if (!selected || !canManage) return; const data = new FormData(form); setError(null); setNotice(null); setBusy(true);
    try { await post(`/organizations/${selected.id}/invitations`, { email: data.get('email'), role: data.get('role') }); form.reset(); setNotice('Invitation issued.'); state.reload(); } catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <Page title="Invitations" description="Invite someone to this organization with a specific role. Invitations are email-bound and expire.">
    {!canManage ? <Notice>An organization Owner or Administrator is required to view and manage invitations.</Notice> : <>
      <form className="inline-form panel" onSubmit={(event) => void invite(event)} aria-busy={busy}><label>Email<input name="email" type="email" required /></label><label>Role<select name="role"><option>Member</option><option>Technician</option><option>Auditor</option><option>Administrator</option></select></label><button className="button primary" disabled={busy}>{busy ? 'Sending...' : 'Invite'}</button></form>
      {error ? <Notice tone="danger">{error}</Notice> : null}{notice ? <Notice tone="success">{notice}</Notice> : null}
      {state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No invitations">No invitations have been issued.</EmptyState> : <div className="cards">{state.data.map((item) => {
        const active = !item.acceptedAtUtc && !item.revokedAtUtc && new Date(item.expiresAtUtc).getTime() > Date.now();
        return <article className="card" key={item.id}><h2>{item.email}</h2><p>{item.role} - expires {formatDate(item.expiresAtUtc)}</p><span className="badge">{item.acceptedAtUtc ? 'Accepted' : item.revokedAtUtc ? 'Revoked' : active ? 'Pending' : 'Expired'}</span>{active ? <ConfirmAction label="Revoke invitation" title="Revoke this invitation?" description={`The invitation for ${item.email} will no longer allow them to join this organization.`} confirmLabel="Confirm revocation" onConfirm={async () => { if (!selected) return; await remove(`/organizations/${selected.id}/invitations/${item.id}`); state.reload(); }} /> : null}</article>;
      })}</div>}
    </>}
  </Page>;
}

export function AcceptInvitationPage() {
  const token = new URLSearchParams(location.search).get('token') ?? ''; const started = useRef(false); const [state, setState] = useState<'loading' | 'done' | 'error'>('loading'); const { reload } = useOrganization();
  useEffect(() => { if (started.current) return; started.current = true; void post('/organizations/invitations/accept', { token }).then(async () => { await reload(); setState('done'); }).catch(() => setState('error')); }, [token]);
  return <Page title="Accept invitation" description="Join the organization with the role assigned by its administrator.">{state === 'loading' ? <LoadingState /> : state === 'done' ? <Notice tone="success">Invitation accepted. Your active organizations were refreshed.</Notice> : <ErrorState message="This invitation is invalid, expired, revoked, already used, or belongs to another account." />}</Page>;
}

function ChangePasswordForm() {
  const { capabilities } = useAuth();
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null); const [saved, setSaved] = useState(false);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const form = event.currentTarget; const data = new FormData(form); setError(null); setSaved(false);
    const invalid = passwordError(data, capabilities?.passwordRules); if (invalid) { setError(invalid); return; }
    setBusy(true);
    try { await post('/account/password/change', { currentPassword: data.get('currentPassword'), newPassword: data.get('password') }); form.reset(); setSaved(true); }
    catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <form className="panel form-grid" onSubmit={(event) => void submit(event)} aria-busy={busy}><h2>Change password</h2>
    <p>Your current sign-in session stays active. Other sign-in sessions and outstanding password-reset links are revoked.</p>
    {error ? <Notice tone="danger">{error}</Notice> : null}{saved ? <Notice tone="success">Password changed. Other sign-in sessions have been revoked.</Notice> : null}
    <PasswordField name="currentPassword" label="Current password" autoComplete="current-password" /><NewPasswordFields />
    <button className="button primary" disabled={busy || !capabilities}>{busy ? 'Changing password...' : 'Change password'}</button>
  </form>;
}

export function SecurityPage() {
  const { profile, reload, capabilities } = useAuth(); const { selected } = useOrganization();
  const [setup, setSetup] = useState<{ secret: string; setupToken: string; otpAuthUri: string } | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const begin = async () => { setError(null); setBusy(true); try { setSetup(await post('/account/mfa/setup')); } catch (cause) { setError(message(cause)); } finally { setBusy(false); } };
  const confirm = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (!setup) return; const code = String(new FormData(event.currentTarget).get('code')); setError(null); setBusy(true);
    try { const result = await post<{ recoveryCodes: string[] }>('/account/mfa/confirm', { setupToken: setup.setupToken, code }); setCodes(result.recoveryCodes); setSetup(null); await reload(); } catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <Page title="Security" description="Protect your account and review who can sign in. Remote access permissions are approved separately in the desktop app.">
    {error ? <Notice tone="danger">{error}</Notice> : null}
    <ChangePasswordForm />
    {capabilities?.mfaAvailable ? <section className="panel security-panel"><div className="section-heading"><h2>Multi-factor authentication</h2><span className={profile?.mfaEnabled ? 'badge success' : 'badge muted'}>{profile?.mfaEnabled ? 'Enabled' : 'Not enabled'}</span></div><p>An authenticator adds a second sign-in check. Recovery codes provide one-time access if your authenticator is unavailable.</p>
      {!profile?.mfaEnabled && !setup ? <button className="button primary" disabled={busy} onClick={() => void begin()}>{busy ? 'Preparing…' : 'Set up MFA'}</button> : null}
      {setup ? <form className="form-grid" onSubmit={(event) => void confirm(event)} aria-busy={busy}><div className="setup-secret"><p>1. Add this setup key to your authenticator. Keep it private.</p><code>{setup.secret}</code></div><label>6-digit code<input name="code" inputMode="numeric" pattern="[0-9]{6}" required autoComplete="one-time-code" aria-describedby="mfa-help" autoFocus /></label><p className="field-help" id="mfa-help">2. Enter the current code from your authenticator to finish setup.</p><div className="form-actions"><button className="button primary" disabled={busy}>{busy ? 'Confirming…' : 'Confirm MFA'}</button><button type="button" className="button secondary" disabled={busy} onClick={() => setSetup(null)}>Cancel setup</button></div></form> : null}
      {codes ? <Notice tone="success"><strong>Save these one-time recovery codes now:</strong><p>Keep them somewhere private. Leaving this page clears this copy.</p><pre>{codes.join('\n')}</pre></Notice> : null}
    </section> : null}
    <div className="overview-grid content-section"><article className="panel"><h2>Sign-in sessions</h2><p>Review and revoke account sessions you no longer recognize.</p><Link href="/sessions" className="text-action">Manage sign-in sessions</Link></article><article className="panel"><h2>Trusted sign-in devices</h2><p>Remove account sign-in trust when a device is no longer yours.</p><Link href="/trusted-devices" className="text-action">Review trusted sign-in devices</Link></article>{selected ? <article className="panel"><h2>Organization policy</h2><p>Review {selected.name}’s connection and security requirements.</p><Link href="/policy" className="text-action">Open policy</Link></article> : null}</div>
  </Page>;
}

const policyControls = [
  ['viewOnlyAllowed', 'View Only allowed', 'Allow screen viewing when organization connection policy is evaluated.'],
  ['fullControlAllowed', 'Full Control allowed', 'Allow remote keyboard and pointer control within an approved session.'],
  ['fileTransferAllowed', 'File Transfer allowed', 'Allow file transfer where the client and connection support it.'],
  ['clipboardAllowed', 'Clipboard allowed', 'Allow clipboard sharing within the approved session scope.'],
  ['unattendedAccessAllowed', 'Unattended Access allowed', 'Permit unattended requests. The host still needs separate setup and trust.'],
  ['mfaRequired', 'MFA required', 'Require the account to have MFA enabled for organization policy evaluation.'],
  ['hybridSecurityRequired', 'Hybrid session security required', 'Require hybrid security to be active during organization policy evaluation.'],
] as const;

export function PolicyPage() {
  const { capabilities } = useAuth();
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Policy>(`/organizations/${selected.id}/policy`) : Promise.resolve(null as unknown as Policy), [selected?.id]);
  const [saved, setSaved] = useState(false); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const canEdit = selected?.role === 'Owner' || selected?.role === 'Administrator';
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (!selected || !state.data || !canEdit) return;
    const data = new FormData(event.currentTarget); const value = (name: string) => data.get(name) === 'on'; setSaved(false); setError(null); setBusy(true);
    try { await put(`/organizations/${selected.id}/policy`, { ...state.data, viewOnlyAllowed: value('viewOnlyAllowed'), fullControlAllowed: value('fullControlAllowed'), fileTransferAllowed: value('fileTransferAllowed'), clipboardAllowed: value('clipboardAllowed'), unattendedAccessAllowed: value('unattendedAccessAllowed'), mfaRequired: value('mfaRequired'), hybridSecurityRequired: value('hybridSecurityRequired'), trustedDeviceLifetimeDays: Number(data.get('trustedDeviceLifetimeDays')), auditRetentionDays: Number(data.get('auditRetentionDays')), approvedRelayRegionsCsv: data.get('approvedRelayRegionsCsv'), minimumClientVersion: data.get('minimumClientVersion') }); setSaved(true); state.reload(); }
    catch (cause) { setError(message(cause)); } finally { setBusy(false); }
  };
  return <Page title="Organization policy" description="Rules for your selected organization. Allowing a capability here does not grant remote access or replace host approval.">
    {saved ? <Notice tone="success">Policy saved.</Notice> : null}{error ? <Notice tone="danger">{error}</Notice> : null}
    {!selected ? <EmptyState title="No organization selected">Choose an organization before reviewing its policy.</EmptyState> : state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : <form className="panel policy-form" onSubmit={(event) => void submit(event)} aria-busy={busy}>
      {!canEdit ? <Notice>You can review this policy. An organization Owner or Administrator can edit it.</Notice> : null}
      <fieldset disabled={!canEdit || busy}><legend>Access & security</legend><div className="policy-options">{policyControls.filter(([name]) => name !== 'mfaRequired' || capabilities?.mfaAvailable).map(([name, label, description]) => <div className="policy-option" key={name}><label className="check"><input name={name} type="checkbox" defaultChecked={state.data![name]} aria-describedby={`${name}-help`} />{label}</label><p id={`${name}-help`}>{description}</p></div>)}</div></fieldset>
      <fieldset className="form-grid" disabled={!canEdit || busy}><legend>Trust, retention & connection requirements</legend>
        <label>Trusted-device lifetime (days)<input name="trustedDeviceLifetimeDays" type="number" min="1" max="3650" required defaultValue={state.data.trustedDeviceLifetimeDays} /></label>
        <label>Audit retention (days)<input name="auditRetentionDays" type="number" min="1" max="3650" required defaultValue={state.data.auditRetentionDays} /></label>
        <label>Approved relay regions, comma separated<input name="approvedRelayRegionsCsv" defaultValue={state.data.approvedRelayRegionsCsv} aria-describedby="relay-help" /></label><p id="relay-help" className="field-help">Use region identifiers from your deployment. Leave empty to apply no region restriction from this field.</p>
        <label>Minimum client version<input name="minimumClientVersion" defaultValue={state.data.minimumClientVersion} aria-describedby="version-help" /></label><p id="version-help" className="field-help">Leave empty for no minimum from this policy. The server validates policy changes and enforces authorization.</p>
      </fieldset>
      {canEdit ? <button className="button primary" disabled={busy}>{busy ? 'Saving…' : 'Save policy'}</button> : null}
    </form>}
  </Page>;
}

export function AuditPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<SecurityEvent[]>(`/organizations/${selected.id}/audit`) : Promise.resolve([]), [selected?.id]);
  return <Page title="Security and audit events" description="Append-only, tenant-scoped metadata. Passwords, tokens, screen, input, clipboard and file contents are never recorded." actions={selected ? <a className="button secondary" href={`/portal/v1/organizations/${selected.id}/audit/export`}>Export JSON</a> : null}>{state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No audit events">No organization security events were recorded.</EmptyState> : <div className="table-wrap" tabIndex={0} aria-label="Organization audit events"><table><thead><tr><th scope="col">Time</th><th scope="col">Action</th><th scope="col">Result</th><th scope="col">Correlation</th></tr></thead><tbody>{state.data.map((item) => <tr key={item.id}><td>{formatDate(item.timestampUtc)}</td><td>{item.action}</td><td>{item.result}</td><td><code>{item.correlationId}</code></td></tr>)}</tbody></table></div>}</Page>;
}

export function PrivacyPage() {
  const [notice, setNotice] = useState<string | null>(null); const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const requestExport = async () => { setError(null); setNotice(null); setBusy(true); try { await post('/account/data-requests', { kind: 'Export' }); setNotice('Export request accepted.'); } catch (cause) { setError(message(cause)); } finally { setBusy(false); } };
  return <Page title="Privacy and data" description="Manage requests for your stored account data.">{notice ? <Notice tone="success">{notice}</Notice> : null}{error ? <Notice tone="danger">{error}</Notice> : null}<div className="cards"><article className="card"><h2>Data export</h2><p>Request an export of stored account and organization metadata.</p><button className="button secondary" disabled={busy} onClick={() => void requestExport()}>{busy ? 'Requesting…' : 'Request export'}</button></article><article className="card"><h2>Delete account</h2><p>Transfer any organizations you own before requesting deletion. Accepted deletion revokes active sessions.</p><ConfirmAction label="Request deletion" title="Request account deletion?" description="This submits an account deletion request. You must transfer organizations you own first. Accepted deletion revokes your active sign-in sessions." confirmLabel="Confirm deletion request" onConfirm={async () => { await post('/account/data-requests', { kind: 'Delete' }); setError(null); setNotice('Delete request accepted.'); }} /></article><article className="card"><h2>Data inventory</h2><p>Account identity, session metadata, organization membership, device ownership, security policy and redacted audit metadata. No screen, keystroke, clipboard or transferred-file content.</p></article></div></Page>;
}

export function MembersPage() {
  const { selected } = useOrganization(); const state = useLoad(() => selected ? api<Member[]>(`/organizations/${selected.id}/members`) : Promise.resolve([]), [selected?.id]);
  return <Page title="Members" description="Roles are tenant-scoped and separate from internal server administrator roles.">{state.error ? <ErrorState message={state.error} retry={state.reload} /> : !state.data ? <LoadingState /> : !state.data.length ? <EmptyState title="No members">No members were returned for this organization.</EmptyState> : <div className="cards">{state.data.map((item) => <article className="card" key={item.accountId}><h2>{item.displayName}</h2><p>{item.email}</p><span className="badge">{item.role}</span></article>)}</div>}</Page>;
}
