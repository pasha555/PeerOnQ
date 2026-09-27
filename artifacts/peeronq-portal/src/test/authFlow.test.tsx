import { capabilities } from './authCapabilities';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { AuthProvider } from '../auth';

function problem(status: number, code: string, title: string) {
  return new Response(JSON.stringify({ code, title }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('customer authentication bootstrap', () => {
  it('renders branded anonymous navigation and applies theme preferences before login', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/capabilities') ? Response.json(capabilities) : problem(401, 'session_invalid', 'Sign in required.'));
    window.localStorage.setItem('peeronq_portal_theme', 'dark');
    const user = userEvent.setup(); render(<AuthProvider><App /></AuthProvider>);
    await screen.findByRole('heading', { name: 'Sign in' });
    expect(document.documentElement).toHaveClass('dark');
    expect(screen.getByRole('link', { name: 'PeerOnQ public website' })).toHaveAttribute('href', 'https://peeronq.com');
    expect(screen.getByRole('link', { name: 'Download the app' })).toHaveAttribute('href', 'https://peeronq.com/#download');
    await waitFor(() => expect(screen.getByLabelText('Email')).toHaveFocus());
    await user.click(screen.getByRole('button', { name: 'Use light theme' }));
    expect(document.documentElement).not.toHaveClass('dark');
    expect(window.localStorage.getItem('peeronq_portal_theme')).toBe('light');
  });

  it.each([false, true])('preserves invitation registration and server rejection (closed=%s)', async (closed) => {
    window.history.replaceState({}, '', '/?token=example-invitation-token');
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      if (String(input).endsWith('/capabilities')) return Response.json(capabilities);
      if (String(input).endsWith('/auth/register')) return closed ? problem(403, 'registration_closed', 'Registration is unavailable.') : Response.json({ emailVerificationRequired: true });
      return problem(401, 'session_invalid', 'Sign in required.');
    });
    const user = userEvent.setup(); render(<AuthProvider><App /></AuthProvider>);
    await user.click(await screen.findByRole('link', { name: 'Create account' }));
    expect(screen.getByLabelText('Display name')).toHaveFocus();
    await user.type(screen.getByLabelText('Display name'), 'Avery');
    await user.type(screen.getByLabelText('Email'), 'avery@example.test');
    await user.type(screen.getByLabelText('Password'), 'Example-Registration-2026!');
    await user.type(screen.getByLabelText('Confirm password'), 'Example-Registration-2026!');
    await user.click(screen.getByRole('button', { name: 'Create account' }));
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/register', expect.objectContaining({ credentials: 'include', body: JSON.stringify({ email: 'avery@example.test', displayName: 'Avery', password: 'Example-Registration-2026!', invitationToken: 'example-invitation-token' }) }));
    if (closed) expect(await screen.findByRole('alert')).toHaveTextContent('Registration is unavailable.');
    else expect(await screen.findByText('Check your inbox to verify your account, then sign in.')).toBeVisible();
    expect(storage).not.toHaveBeenCalled();
    expect(window.sessionStorage.length).toBe(0);
  });

  it('uses the generic password-reset request response without revealing whether the account exists', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/capabilities') ? Response.json(capabilities) : String(input).endsWith('/password-reset/request') ? new Response(null, { status: 204 }) : problem(401, 'session_invalid', 'Sign in required.'));
    const user = userEvent.setup(); render(<AuthProvider><App /></AuthProvider>);
    await user.click(await screen.findByRole('link', { name: 'Forgot password?' }));
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('Email'), 'avery@example.test');
    await user.click(screen.getByRole('button', { name: 'Send reset message' }));
    expect(await screen.findByText('If an eligible account exists, a password reset email has been sent.')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/password-reset/request', expect.objectContaining({ method: 'POST', body: JSON.stringify({ email: 'avery@example.test' }) }));
  });

  it('completes a token password reset in the branded account screen', async () => {
    window.history.replaceState({}, '', '/reset-password?token=example-reset-token');
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/capabilities') ? Response.json(capabilities) : String(input).endsWith('/password-reset/complete') ? new Response(null, { status: 204 }) : problem(401, 'session_invalid', 'Sign in required.'));
    const user = userEvent.setup(); render(<AuthProvider><App /></AuthProvider>);
    await user.type(screen.getByLabelText('New password'), 'Example-Reset-Password-2026!');
    await user.type(screen.getByLabelText('Confirm password'), 'Example-Reset-Password-2026!');
    await user.click(screen.getByRole('button', { name: 'Reset password' }));
    expect(await screen.findByText('Password changed and existing sessions revoked.')).toBeVisible();
    expect(screen.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/');
    expect(screen.getByRole('link', { name: 'Back to PeerOnQ' })).toHaveAttribute('href', 'https://peeronq.com');
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/password-reset/complete', expect.objectContaining({ method: 'POST', body: JSON.stringify({ token: 'example-reset-token', newPassword: 'Example-Reset-Password-2026!' }) }));
    expect(storage).not.toHaveBeenCalled();
  });

  it.each([true, false])('handles email verification success=%s without exposing the token', async (valid) => {
    window.history.replaceState({}, '', '/verify-email?token=example-verification-token');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      if (String(input).endsWith('/capabilities')) return Response.json(capabilities);
      if (String(input).endsWith('/verify-email')) return valid ? new Response(null, { status: 204 }) : problem(400, 'invalid_token', 'Invalid token.');
      return problem(401, 'session_invalid', 'Sign in required.');
    });
    render(<AuthProvider><App /></AuthProvider>);
    if (valid) expect(await screen.findByText('Email verified. You can now sign in.')).toBeVisible();
    else expect(await screen.findByRole('alert')).toHaveTextContent('This verification link is invalid, expired, or already used.');
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/verify-email', expect.objectContaining({ body: JSON.stringify({ token: 'example-verification-token' }) }));
    expect(document.body).not.toHaveTextContent('example-verification-token');
  });

  it('opens the real account overview after successful sign-in', async () => {
    window.history.replaceState({}, '', '/');
    const profile = { id: 'account-1', displayName: 'Avery', email: 'avery@example.test', emailVerified: true, mfaEnabled: false, createdAtUtc: '2026-01-01T00:00:00Z' };
    let authenticated = false;
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const path = String(input);
      if (path.endsWith('/capabilities')) return Response.json(capabilities);
      if (path === '/portal/v1/auth/login') { authenticated = true; return Response.json({ accessToken: 'example-token-not-for-the-ui' }); }
      if (path === '/portal/v1/account/profile' && authenticated) return Response.json(profile);
      if (authenticated && !path.includes('/auth/')) return Response.json([]);
      return problem(401, 'session_invalid', 'Sign in required.');
    });
    const user = userEvent.setup();
    render(<AuthProvider><App /></AuthProvider>);

    await screen.findByRole('heading', { name: 'Sign in' });
    await user.type(screen.getByLabelText('Email'), profile.email);
    await user.type(screen.getByLabelText('Password'), 'Example-Login-Password-2026!');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Overview' })).toBeVisible();
    expect(await screen.findByRole('heading', { name: 'Your shared workspace starts here' })).toBeVisible();
    expect(screen.getByText('Welcome, Avery. Your account and shared workspace in one place.')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/login', expect.objectContaining({
      credentials: 'include', method: 'POST', body: JSON.stringify({ email: profile.email, password: 'Example-Login-Password-2026!', mfaCode: null }),
    }));
    expect(screen.queryByRole('link', { name: 'Remote sessions' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign-in sessions' })).toHaveAttribute('href', '/sessions');
    expect(screen.getByRole('link', { name: 'Trusted sign-in devices' })).toHaveAttribute('href', '/trusted-devices');
    expect(storage).not.toHaveBeenCalled();
    expect(window.sessionStorage.length).toBe(0);
    expect(document.body).not.toHaveTextContent('example-token-not-for-the-ui');
  });

  it('keeps MFA challenge and credentials in the real sign-in flow', async () => {
    window.history.replaceState({}, '', '/');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/capabilities') ? Response.json(capabilities) : problem(401, 'session_invalid', 'Sign in required.'));
    const user = userEvent.setup();
    render(<AuthProvider><App /></AuthProvider>);
    await screen.findByRole('heading', { name: 'Sign in' });
    fetchMock.mockResolvedValueOnce(problem(403, 'mfa_required', 'MFA required.'));
    await user.type(screen.getByLabelText('Email'), 'avery@example.test');
    await user.type(screen.getByLabelText('Password'), 'Example-Login-Password-2026!');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await user.type(await screen.findByLabelText('MFA or recovery code'), '123456');
    fetchMock.mockResolvedValueOnce(problem(401, 'invalid_credentials', 'The credentials are invalid.'));
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(fetchMock).toHaveBeenLastCalledWith('/portal/v1/auth/login', expect.objectContaining({
      body: JSON.stringify({ email: 'avery@example.test', password: 'Example-Login-Password-2026!', mfaCode: '123456' }),
    }));
    expect(await screen.findByText('The credentials are invalid.')).toBeVisible();
  });

  it('treats a missing refresh session as anonymous instead of unavailable', async () => {
    window.history.replaceState({}, '', '/');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/capabilities') ? Response.json(capabilities) : problem(401, 'session_invalid', 'The session is invalid or expired.'));

    render(<AuthProvider><App /></AuthProvider>);

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
    expect(screen.queryByText('The account service is unavailable.')).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith('/auth/refresh'))).toBe(false);
  });

  it('clears a stale outage banner when the service returns an authentication response', async () => {
    window.history.replaceState({}, '', '/');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      if (String(input).endsWith('/capabilities')) return Response.json(capabilities);
      if (String(input).endsWith('/account/profile')) throw new TypeError('unavailable');
      return problem(401, 'invalid_credentials', 'The credentials are invalid.');
    });
    const user = userEvent.setup();

    render(<AuthProvider><App /></AuthProvider>);

    expect(await screen.findByText('The account service is unavailable.')).toBeVisible();
    await user.type(screen.getByLabelText('Email'), 'person@example.test');
    await user.type(screen.getByLabelText('Password'), 'Example-Login-Password-2026!');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('The credentials are invalid.')).toBeVisible();
    await waitFor(() => expect(screen.queryByText('The account service is unavailable.')).not.toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });
});
