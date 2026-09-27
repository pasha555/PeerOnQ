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
  it('opens the real account overview after successful sign-in', async () => {
    window.history.replaceState({}, '', '/');
    const profile = { id: 'account-1', displayName: 'Avery', email: 'avery@example.test', emailVerified: true, mfaEnabled: false, createdAtUtc: '2026-01-01T00:00:00Z' };
    let authenticated = false;
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const path = String(input);
      if (path === '/portal/v1/auth/login') { authenticated = true; return new Response(null, { status: 204 }); }
      if (path === '/portal/v1/account/profile' && authenticated) return Response.json(profile);
      if (path === '/portal/v1/organizations/') return Response.json([]);
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
    expect(screen.getByRole('link', { name: 'Sessions' })).toHaveAttribute('href', '/remote-sessions');
    expect(screen.getByRole('link', { name: 'Browser sessions' })).toHaveAttribute('href', '/sessions');
  });

  it('keeps MFA challenge and credentials in the real sign-in flow', async () => {
    window.history.replaceState({}, '', '/');
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(problem(401, 'session_invalid', 'Sign in required.'));
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
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(problem(401, 'session_invalid', 'The session is invalid or expired.'))
      .mockResolvedValueOnce(problem(401, 'session_invalid', 'The session is invalid or expired.'));

    render(<AuthProvider><App /></AuthProvider>);

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
    expect(screen.queryByText('The account service is unavailable.')).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/portal/v1/auth/refresh',
      expect.objectContaining({ body: '{}' }),
    );
  });

  it('clears a stale outage banner when the service returns an authentication response', async () => {
    window.history.replaceState({}, '', '/');
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockRejectedValueOnce(new TypeError('unavailable'))
      .mockResolvedValueOnce(problem(401, 'invalid_credentials', 'The credentials are invalid.'));
    const user = userEvent.setup();

    render(<AuthProvider><App /></AuthProvider>);

    expect(await screen.findByText('The account service is unavailable.')).toBeVisible();
    await user.type(screen.getByLabelText('Email'), 'person@example.test');
    await user.type(screen.getByLabelText('Password'), 'Example-Login-Password-2026!');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('The credentials are invalid.')).toBeVisible();
    await waitFor(() => expect(screen.queryByText('The account service is unavailable.')).not.toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
