import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { AuthProvider } from '../auth';

const profile = { id: 'account-1', displayName: 'Avery', email: 'avery@example.test', emailVerified: true, mfaEnabled: false, createdAtUtc: '2026-01-01T00:00:00Z' };
const organizations = [{ id: 'org-a', name: 'Design team', role: 'Owner', ownerAccountId: profile.id }, { id: 'org-b', name: 'Support team', role: 'Member', ownerAccountId: 'another-account' }];
const activeSession = { id: 'session-1', userAgentSummary: 'Browser on Windows', createdAtUtc: '2026-01-01T00:00:00Z', expiresAtUtc: '2099-01-01T00:00:00Z', revokedAtUtc: null };
const trust = { ...activeSession, id: 'trust-1', name: 'Work browser' };
const policy = { organizationId: 'org-a', viewOnlyAllowed: true, fullControlAllowed: false, fileTransferAllowed: false, clipboardAllowed: false, unattendedAccessAllowed: false, mfaRequired: true, hybridSecurityRequired: true, trustedDeviceLifetimeDays: 30, auditRetentionDays: 90, approvedRelayRegionsCsv: 'eu-test', minimumClientVersion: '0.9.66' };
const failure = (title: string, status = 503, code = 'request_failed') => Response.json({ title, code }, { status });
function mockApi(custom?: (path: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const path = String(input);
    const response = custom?.(path, init);
    if (response) return response;
    if (path === '/portal/v1/account/profile') return Response.json(profile);
    if (path === '/portal/v1/organizations/') return Response.json(organizations);
    if (path.endsWith('/policy')) return Response.json({ ...policy, organizationId: path.includes('org-b') ? 'org-b' : 'org-a' });
    return Response.json([]);
  });
}
function open(path = '/') { window.history.replaceState({}, '', path); return render(<AuthProvider><App /></AuthProvider>); }

describe('customer portal product UX', () => {
  it('keeps healthy overview widgets available when one API fails and retries only that widget', async () => {
    let available = false;
    const fetchMock = mockApi((path) => {
      if (path === '/portal/v1/account/sessions') return available ? Response.json([activeSession, { ...activeSession, id: 'expired', expiresAtUtc: '2020-01-01T00:00:00Z' }, { ...activeSession, id: 'revoked', revokedAtUtc: '2026-01-01T00:00:00Z' }]) : failure('Sign-in session service unavailable.');
      if (path === '/portal/v1/account/trusted-devices') return Response.json([trust]);
    });
    const user = userEvent.setup(); open();
    expect(await screen.findByText('Sign-in session service unavailable.')).toBeVisible();
    expect(screen.getByText('Email verified')).toBeVisible();
    expect(screen.getByText('MFA not enabled')).toBeVisible();
    expect(within(screen.getByRole('article', { name: 'Trusted sign-in devices' })).getByText('1')).toBeVisible();
    expect(within(screen.getByRole('article', { name: 'Organizations' })).getByText('2')).toBeVisible();
    const trustRequests = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/account/trusted-devices')).length;
    available = true;
    await user.click(screen.getByRole('button', { name: 'Retry sign-in sessions' }));
    expect(await within(screen.getByRole('article', { name: 'Sign-in sessions' })).findByText('1')).toBeVisible();
    expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/account/trusted-devices'))).toHaveLength(trustRequests);
  });

  it.each(['/profile', '/account'])('keeps the profile available at %s with a canonical Profile navigation link', async (path) => {
    const fetchMock = mockApi(); open(path);
    expect(await screen.findByRole('heading', { name: 'Profile' })).toBeVisible();
    expect(screen.getByLabelText('Display name')).toHaveValue('Avery');
    expect(screen.getByRole('link', { name: 'Profile' })).toHaveAttribute('href', '/profile');
    expect(screen.getByRole('link', { name: 'Profile' })).toHaveAttribute('aria-current', 'page');
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/portal/v1/account/profile'))).toBe(true);
  });

  it('shows a failed profile save, then allows a successful retry', async () => {
    let available = false; let name = profile.displayName;
    mockApi((path, init) => {
      if (path !== '/portal/v1/account/profile') return;
      if (init?.method === 'PUT') { if (!available) return failure('Profile could not be saved.'); name = JSON.parse(String(init.body)).displayName; return new Response(null, { status: 204 }); }
      return Response.json({ ...profile, displayName: name });
    });
    const user = userEvent.setup(); open('/profile');
    const field = await screen.findByLabelText('Display name');
    await user.clear(field); await user.type(field, 'Avery Updated');
    await user.click(screen.getByRole('button', { name: 'Save profile' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Profile could not be saved.');
    available = true; await user.click(screen.getByRole('button', { name: 'Save profile' }));
    expect(await screen.findByText('Profile saved.')).toBeVisible();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('hides organization-only navigation and guards direct organization routes when none exist', async () => {
    const fetchMock = mockApi((path) => path === '/portal/v1/organizations/' ? Response.json([]) : undefined);
    open('/policy');
    expect(await screen.findByRole('heading', { name: 'Your shared workspace starts here' })).toBeVisible();
    for (const name of ['Managed devices', 'Remote sessions', 'Members', 'Teams', 'Invitations', 'Policy', 'Audit history'])
      expect(screen.queryByRole('link', { name })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Organizations' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Save policy' })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => /\/organizations\/[^/]+\//.test(String(url)))).toBe(false);
  });

  it('preserves every server policy default and sends exactly the edited values', async () => {
    const fetchMock = mockApi((path, init) => path.endsWith('/policy') && init?.method === 'PUT' ? new Response(null, { status: 204 }) : undefined);
    const user = userEvent.setup(); open('/policy');
    expect(await screen.findByRole('checkbox', { name: 'View Only allowed' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'MFA required' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Hybrid session security required' })).toBeChecked();
    for (const name of ['Full Control allowed', 'File Transfer allowed', 'Clipboard allowed', 'Unattended Access allowed']) expect(screen.getByRole('checkbox', { name })).not.toBeChecked();
    await user.click(screen.getByRole('checkbox', { name: 'File Transfer allowed' }));
    await user.click(screen.getByRole('button', { name: 'Save policy' }));
    expect(await screen.findByText('Policy saved.')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/organizations/org-a/policy', expect.objectContaining({ method: 'PUT', credentials: 'include', body: JSON.stringify({ ...policy, fileTransferAllowed: true }) }));
  });

  it('does not move an edited policy or stale success message to another organization', async () => {
    const fetchMock = mockApi(); const user = userEvent.setup(); open('/policy');
    await user.click(await screen.findByRole('checkbox', { name: 'Full Control allowed' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Active organization' }), 'org-b');
    expect(await screen.findByText(/You can review this policy/)).toBeVisible();
    expect(screen.getByRole('checkbox', { name: 'Full Control allowed' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'View Only allowed' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save policy' })).not.toBeInTheDocument();
    expect(screen.queryByText('Policy saved.')).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false);
  });

  it('surfaces server policy rejection and retains the edits for review', async () => {
    mockApi((path, init) => path.endsWith('/policy') && init?.method === 'PUT' ? failure('Policy change denied.', 403) : undefined);
    const user = userEvent.setup(); open('/policy');
    await user.click(await screen.findByRole('checkbox', { name: 'Clipboard allowed' }));
    await user.click(screen.getByRole('button', { name: 'Save policy' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Policy change denied.');
    expect(screen.getByRole('checkbox', { name: 'Clipboard allowed' })).toBeChecked();
    expect(screen.queryByText('Policy saved.')).not.toBeInTheDocument();
  });

  it('confirms trust revocation, restores focus on cancel and makes no request before confirmation', async () => {
    let revoked = false;
    const fetchMock = mockApi((path, init) => {
      if (path === '/portal/v1/account/trusted-devices') return Response.json([{ ...trust, revokedAtUtc: revoked ? '2026-09-27T00:00:00Z' : null }]);
      if (init?.method === 'DELETE') { revoked = true; return new Response(null, { status: 204 }); }
    });
    const user = userEvent.setup(); open('/trusted-devices');
    const trigger = await screen.findByRole('button', { name: 'Revoke trust' }); await user.click(trigger);
    const modal = within(screen.getByRole('dialog', { name: 'Remove sign-in trust?' }));
    expect(modal.getByRole('button', { name: 'Cancel' })).toHaveFocus();
    await user.click(modal.getByRole('button', { name: 'Cancel' })); expect(trigger).toHaveFocus();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false);
    await user.click(trigger); await user.click(modal.getByRole('button', { name: 'Confirm revocation' }));
    expect(await screen.findByText('Revoked')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/trusted-devices/trust-1', expect.objectContaining({ method: 'DELETE' }));
  });

  it('reports an invitation revocation failure in the confirmation dialog', async () => {
    mockApi((path, init) => {
      if (init?.method === 'DELETE') return failure('Invitation could not be revoked.');
      if (path.endsWith('/invitations')) return Response.json([{ id: 'invite-1', email: 'member@example.test', role: 'Member', createdAtUtc: '2026-01-01T00:00:00Z', expiresAtUtc: '2099-01-01T00:00:00Z', acceptedAtUtc: null, revokedAtUtc: null }]);
    });
    const user = userEvent.setup(); open('/invitations');
    await user.click(await screen.findByRole('button', { name: 'Revoke invitation' }));
    await user.click(screen.getByRole('button', { name: 'Confirm revocation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Invitation could not be revoked.');
    expect(screen.getByRole('dialog')).toBeVisible();
  });

  it('keeps MFA setup and recovery codes in memory while confirming through the existing API', async () => {
    let enabled = false;
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const fetchMock = mockApi((path) => {
      if (path === '/portal/v1/account/profile') return Response.json({ ...profile, mfaEnabled: enabled });
      if (path.endsWith('/mfa/setup')) return Response.json({ secret: 'ExampleMfaSetupKey', setupToken: 'example-setup-token', otpAuthUri: 'otpauth://totp/example' });
      if (path.endsWith('/mfa/confirm')) { enabled = true; return Response.json({ recoveryCodes: ['example-recovery-code'] }); }
    });
    const user = userEvent.setup(); open('/security');
    await user.click(await screen.findByRole('button', { name: 'Set up MFA' }));
    await user.type(await screen.findByLabelText('6-digit code'), '123456');
    await user.click(screen.getByRole('button', { name: 'Confirm MFA' }));
    expect(await screen.findByText('example-recovery-code')).toBeVisible();
    expect(screen.getByText('Enabled')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/mfa/confirm', expect.objectContaining({ method: 'POST', body: JSON.stringify({ setupToken: 'example-setup-token', code: '123456' }) }));
    expect(storage.mock.calls.every(([key]) => ['peeronq_portal_theme', 'peeronq_portal_organization'].includes(key))).toBe(true);
    expect(JSON.stringify(window.localStorage) + JSON.stringify(window.sessionStorage)).not.toMatch(/example-setup-token|ExampleMfaSetupKey|example-recovery-code/);
  });

  it('requires confirmation before submitting an account deletion request', async () => {
    const fetchMock = mockApi((path) => path.endsWith('/data-requests') ? Response.json({ id: 'request-1' }, { status: 202 }) : undefined);
    const user = userEvent.setup(); open('/privacy');
    await user.click(await screen.findByRole('button', { name: 'Request deletion' }));
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/data-requests'))).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Confirm deletion request' }));
    expect(await screen.findByText('Delete request accepted.')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/data-requests', expect.objectContaining({ method: 'POST', body: JSON.stringify({ kind: 'Delete' }) }));
  });

  it('explains manager-only invitation access without issuing an unauthorized list request', async () => {
    const fetchMock = mockApi((path) => path === '/portal/v1/organizations/' ? Response.json([organizations[1]]) : undefined);
    open('/invitations');
    expect(await screen.findByText(/required to view and manage invitations/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Invite' })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/invitations'))).toBe(false);
  });

  it('continues using cookie authentication when browser preference storage is blocked', async () => {
    mockApi();
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Blocked', 'SecurityError'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Blocked', 'SecurityError'); });
    const user = userEvent.setup(); open('/profile');
    expect(await screen.findByRole('heading', { name: 'Profile' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Use dark theme' }));
    expect(document.documentElement).toHaveClass('dark');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Active organization' }), 'org-b');
    expect(screen.getByRole('combobox', { name: 'Active organization' })).toHaveValue('org-b');
  });

  it('keeps the account visible on failed logout, then signs out only after confirmation from the API', async () => {
    let available = false;
    const fetchMock = mockApi((path) => path.endsWith('/auth/logout') ? available ? new Response(null, { status: 204 }) : failure('Unavailable') : undefined);
    const user = userEvent.setup(); open('/profile');
    await user.click(await screen.findByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign out could not be confirmed.');
    expect(screen.getByRole('heading', { name: 'Profile' })).toBeVisible();
    available = true; await user.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/logout', expect.objectContaining({ method: 'POST', credentials: 'include' }));
  });

  it('closes mobile navigation with Escape or route selection and focuses the destination', async () => {
    mockApi(); const user = userEvent.setup(); open('/profile');
    const toggle = await screen.findByRole('button', { name: 'Open navigation' }); await user.click(toggle);
    expect(screen.getByRole('dialog', { name: 'Portal navigation' })).toBeVisible();
    expect(document.getElementById('main')).toHaveAttribute('inert');
    await user.keyboard('{Escape}'); expect(toggle).toHaveFocus();
    expect(document.getElementById('main')).not.toHaveAttribute('inert');
    await user.click(toggle); await user.click(screen.getByRole('link', { name: 'Security' }));
    expect(await screen.findByRole('heading', { name: 'Security' })).toBeVisible();
    expect(document.getElementById('main')).toHaveFocus(); expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('uses canonical logos and keeps operational hosts out of customer navigation', async () => {
    const fetchMock = mockApi(); open('/profile'); await screen.findByRole('heading', { name: 'Profile' });
    for (const name of ['peeronq-lockup.svg', 'peeronq-lockup-light.svg']) {
      expect(readFileSync(resolve('public/brand', name), 'utf8')).toBe(readFileSync(resolve('../peeronq/public/brand', name), 'utf8'));
    }
    for (const link of screen.getAllByRole('link')) expect(link.getAttribute('href')).not.toMatch(/admin|grafana|prometheus/);
    expect(fetchMock.mock.calls.every(([url]) => String(url).startsWith('/portal/v1/'))).toBe(true);
  });
});
