import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { AuthProvider } from '../auth';
import { capabilities } from './authCapabilities';
import type { AuthCapabilities } from '../types';

const profile = { id: 'customer', displayName: 'Avery', email: 'avery@example.test', emailVerified: true, mfaEnabled: true, createdAtUtc: '2026-01-01T00:00:00Z' };
function mockApi(options: Partial<AuthCapabilities> = {}, authenticated = false, handler?: (path: string, init?: RequestInit) => Response | undefined) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const path = String(input);
    const custom = handler?.(path, init); if (custom) return custom;
    if (path.endsWith('/capabilities')) return Response.json({ ...capabilities, mfaAvailable: false, ...options });
    if (path.endsWith('/account/profile') && authenticated) return Response.json(profile);
    if (path.includes('/auth/') || !authenticated) return Response.json({ code: 'invalid_credentials', title: 'The credentials are invalid.' }, { status: 401 });
    return Response.json([]);
  });
}
function open(path = '/') { window.history.replaceState({}, '', path); render(<AuthProvider><App /></AuthProvider>); }

describe('customer authentication completion', () => {
  it.each(['Closed', 'InvitationOnly'] as const)('does not offer registration without an invitation in %s mode', async (mode) => {
    mockApi({ registrationMode: mode, registrationAvailable: mode !== 'Closed' }); open();
    await screen.findByLabelText('Email');
    expect(screen.queryByRole('link', { name: 'Create account' })).not.toBeInTheDocument();
    expect(await screen.findByText(mode === 'Closed' ? /New account registration is currently closed/ : /An invitation is required/)).toBeVisible();
  });

  it('shows a recoverable error when capabilities cannot be loaded', async () => {
    let available = false;
    mockApi({}, false, (path) => path.endsWith('/capabilities') && !available ? Response.json({}, { status: 503 }) : undefined); open();
    expect(await screen.findByText(/Sign-in options are unavailable/)).toBeVisible();
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    available = true; await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByLabelText('Password')).toBeVisible();
  });

  it('does not advertise email recovery when mail is disabled', async () => {
    mockApi({ passwordResetAvailable: false, registrationAvailable: false, registrationMode: 'Closed' }); open();
    await screen.findByLabelText('Email');
    expect(screen.queryByRole('link', { name: 'Forgot password?' })).not.toBeInTheDocument();
    expect(screen.getByText(/Email recovery is unavailable/)).toBeVisible();
  });

  it('validates password confirmation and complexity before requesting registration', async () => {
    const fetchMock = mockApi(); const user = userEvent.setup(); open();
    await user.click(await screen.findByRole('link', { name: 'Create account' }));
    await user.type(screen.getByLabelText('Display name'), 'Avery'); await user.type(screen.getByLabelText('Email'), profile.email);
    await user.type(screen.getByLabelText('Password'), 'alllowercasepassword'); await user.type(screen.getByLabelText('Confirm password'), 'different');
    await user.click(screen.getByRole('button', { name: 'Create account' }));
    expect(await screen.findByText('Passwords do not match.')).toBeVisible();
    await user.clear(screen.getByLabelText('Confirm password')); await user.type(screen.getByLabelText('Confirm password'), 'alllowercasepassword');
    await user.click(screen.getByRole('button', { name: 'Create account' }));
    expect(await screen.findByText(/Use the required password length/)).toBeVisible();
    expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith('/auth/register'))).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Show password' })); expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'text');
    await user.click(screen.getByRole('button', { name: 'Hide password' })); expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password');
  });

  it('resends verification with a generic response and no token storage', async () => {
    const fetchMock = mockApi({}, false, (path) => path.endsWith('/verify-email/resend') ? Response.json({ message: 'Accepted' }, { status: 202 }) : undefined);
    const user = userEvent.setup(); open(); await user.click(await screen.findByRole('link', { name: 'Resend verification' }));
    await user.type(screen.getByLabelText('Email'), profile.email); await user.click(screen.getByRole('button', { name: 'Send verification message' }));
    expect(await screen.findByText(/If the account is eligible, a new verification email has been sent/)).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/verify-email/resend', expect.objectContaining({ body: JSON.stringify({ email: profile.email }) }));
    expect(sessionStorage.length).toBe(0);
  });

  it('hides customer MFA setup even for an account with stored MFA and changes its password', async () => {
    const fetchMock = mockApi({}, true, (path) => path.endsWith('/password/change') ? new Response(null, { status: 204 }) : undefined);
    const user = userEvent.setup(); open('/security');
    await screen.findByRole('heading', { name: 'Change password' });
    expect(screen.queryByRole('button', { name: 'Set up MFA' })).not.toBeInTheDocument();
    expect(screen.queryByText('Multi-factor authentication')).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('Current password'), 'OldPassword-2026');
    await user.type(screen.getByLabelText('New password'), 'NewPassword-2026');
    await user.type(screen.getByLabelText('Confirm password'), 'NewPassword-2026');
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText('Password changed. Other sign-in sessions have been revoked.')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/password/change', expect.objectContaining({ body: JSON.stringify({ currentPassword: 'OldPassword-2026', newPassword: 'NewPassword-2026' }) }));
    expect(screen.getByLabelText('Current password')).toHaveValue('');
  });

  it('returns an expired account session to sign-in without a stale private screen', async () => {
    mockApi({}, true, (path) => path.endsWith('/account/sessions') ? Response.json({ code: 'session_invalid' }, { status: 401 }) : undefined);
    open('/sessions'); await waitFor(() => expect(screen.getByRole('heading', { name: 'Sign in' })).toBeVisible());
    expect(screen.queryByRole('heading', { name: 'Sign-in sessions' })).not.toBeInTheDocument();
  });

  it('hides the unavailable MFA policy control while preserving other organization controls', async () => {
    mockApi({}, true, (path) => {
      if (path.endsWith('/organizations/')) return Response.json([{ id: 'org', name: 'Workspace', role: 'Owner', ownerAccountId: profile.id }]);
      if (path.endsWith('/policy')) return Response.json({ organizationId: 'org', mfaRequired: false, viewOnlyAllowed: true, trustedDeviceLifetimeDays: 30, auditRetentionDays: 90, approvedRelayRegionsCsv: '', minimumClientVersion: '' });
    });
    open('/policy'); await screen.findByRole('checkbox', { name: 'View Only allowed' });
    expect(screen.queryByRole('checkbox', { name: 'MFA required' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save policy' })).toBeEnabled();
  });
});
