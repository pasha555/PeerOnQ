import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { AuthProvider } from '../auth';
import type { AuthCapabilities } from '../types';
import { capabilities } from './authCapabilities';

const routes = [
  ['Create account', '/register', 'Create account', 'Display name'],
  ['Forgot password?', '/forgot-password', 'Forgot password', 'Email'],
  ['Resend verification', '/resend-verification', 'Resend verification', 'Email'],
] as const;
const recovery = [
  ['/forgot-password', '/auth/password-reset/request', 'Send reset message', 'If an eligible account exists, a password reset email has been sent.'],
  ['/resend-verification', '/auth/verify-email/resend', 'Send verification message', 'If the account is eligible, a new verification email has been sent.'],
] as const;

function setup(path = '/', policy: Partial<AuthCapabilities> = {}, response?: (path: string) => Response | Promise<Response> | undefined) {
  const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
    const url = String(input);
    const custom = response?.(url); if (custom) return custom;
    if (url.endsWith('/capabilities')) return Response.json({ ...capabilities, mfaAvailable: false, ...policy });
    return Response.json({ code: 'session_invalid', title: 'Sign in required.' }, { status: 401 });
  });
  window.history.replaceState({}, '', path);
  render(<AuthProvider><App /></AuthProvider>);
  return fetchMock;
}

async function register(user: ReturnType<typeof userEvent.setup>) {
  await user.type(await screen.findByLabelText('Display name'), 'Avery');
  await user.type(screen.getByLabelText('Email'), 'avery@example.test');
  await user.type(screen.getByLabelText('Password'), 'Example-Registration-2026!');
  await user.type(screen.getByLabelText('Confirm password'), 'Example-Registration-2026!');
  await user.click(screen.getByRole('button', { name: 'Create account' }));
}

describe('explicit customer auth navigation', () => {
  it.each(routes)('clicking %s changes URL and focuses its real form', async (label, path, heading, field) => {
    setup(); const user = userEvent.setup();
    const link = await screen.findByRole('link', { name: label });
    expect(link).toHaveAttribute('href', path);
    await user.click(link);
    expect(window.location.pathname).toBe(path);
    expect(screen.getByRole('heading', { name: heading })).toBeVisible();
    expect(screen.getByLabelText(field)).toHaveFocus();
    expect(screen.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/');
  });

  it.each(routes)('supports direct navigation to %s', async (_label, path, heading, field) => {
    setup(path);
    expect(await screen.findByLabelText(field)).toHaveFocus();
    expect(screen.getByRole('heading', { name: heading })).toBeVisible();
    expect(window.location.pathname).toBe(path);
  });

  it.each(routes)('supports native browser Back and Forward through %s', async (label, path, _heading, field) => {
    setup(); const user = userEvent.setup();
    await user.click(await screen.findByRole('link', { name: label }));
    expect(window.location.pathname).toBe(path);
    act(() => window.history.back());
    await waitFor(() => expect(window.location.pathname).toBe('/'));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
    act(() => window.history.forward());
    await waitFor(() => expect(window.location.pathname).toBe(path));
    expect(await screen.findByLabelText(field)).toHaveFocus();
    await user.click(screen.getByRole('link', { name: 'Back to sign in' }));
    await user.click(screen.getByRole('link', { name: 'Forgot password?' }));
    expect(window.location.pathname).toBe('/forgot-password');
  });

  it('activates auth links using the keyboard without submitting the sign-in form', async () => {
    const fetchMock = setup(); const user = userEvent.setup();
    const link = await screen.findByRole('link', { name: 'Create account' });
    link.focus(); await user.keyboard('{Enter}');
    expect(window.location.pathname).toBe('/register');
    expect(screen.getByLabelText('Display name')).toHaveFocus();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false);
  });

  it.each(recovery)('stays at %s with generic success and an explicit sign-in link', async (path, endpoint, button, notice) => {
    const fetchMock = setup(path, {}, (url) => url.endsWith(endpoint) ? Response.json({ message: 'Request accepted.' }, { status: 202 }) : undefined);
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Email'), 'avery@example.test');
    await user.click(screen.getByRole('button', { name: button }));
    expect(await screen.findByText(notice)).toBeVisible();
    expect(window.location.pathname).toBe(path);
    expect(screen.queryByRole('heading', { name: 'Sign in' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Check your email' })).toHaveFocus();
    expect(fetchMock).toHaveBeenCalledWith(`/portal/v1${endpoint}`, expect.objectContaining({ method: 'POST', credentials: 'include', body: JSON.stringify({ email: 'avery@example.test' }) }));
    await user.click(screen.getByRole('link', { name: 'Back to sign in' }));
    expect(window.location.pathname).toBe('/');
    expect(await screen.findByLabelText('Email')).toHaveFocus();
  });

  it.each([true, false])('keeps registration success separate from login (verification=%s)', async (required) => {
    setup('/register', {}, (url) => url.endsWith('/auth/register') ? Response.json({ emailVerificationRequired: required }) : undefined);
    await register(userEvent.setup());
    expect(await screen.findByRole('heading', { name: required ? 'Check your email' : 'Account created' })).toHaveFocus();
    expect(screen.getByText(required ? 'Check your inbox to verify your account, then sign in.' : 'Account created. You can sign in now.')).toBeVisible();
    expect(window.location.pathname).toBe('/register');
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/');
  });

  it.each([
    ['/register', { registrationMode: 'Closed', registrationAvailable: false }, /registration is currently closed/],
    ['/register', { registrationMode: 'InvitationOnly' }, /An invitation is required/],
    ['/forgot-password', { passwordResetAvailable: false }, /Email recovery is unavailable/],
    ['/resend-verification', { passwordResetAvailable: false }, /Verification email is unavailable/],
    ['/resend-verification', { requireEmailVerification: false }, /Verification email is unavailable/],
  ] as const)('does not expose a disabled form on direct %s navigation', async (path, policy, explanation) => {
    setup(path, policy);
    expect(await screen.findByText(explanation)).toBeVisible();
    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to sign in' })).toBeVisible();
  });

  it('hides all unavailable navigation links on sign-in', async () => {
    setup('/', { registrationMode: 'Closed', registrationAvailable: false, passwordResetAvailable: false });
    await screen.findByLabelText('Email');
    for (const [label] of routes) expect(screen.queryByRole('link', { name: label })).not.toBeInTheDocument();
  });

  it('preserves an invitation in the create-account URL and registration request', async () => {
    const fetchMock = setup('/invitations/accept?token=example-invitation', { registrationMode: 'InvitationOnly' }, (url) => url.endsWith('/auth/register') ? Response.json({ emailVerificationRequired: true }) : undefined);
    const user = userEvent.setup(); await user.click(await screen.findByRole('link', { name: 'Create account' }));
    expect(window.location.pathname + window.location.search).toBe('/register?token=example-invitation');
    await register(user);
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/auth/register', expect.objectContaining({ body: expect.stringContaining('"invitationToken":"example-invitation"') }));
    expect(document.body).not.toHaveTextContent('example-invitation');
  });

  it.each([
    ['account_exists', 409, /An account already exists/],
    ['customer_mail_unavailable', 503, /Email delivery is temporarily unavailable/],
  ] as const)('keeps the registration form with a safe %s error', async (code, status, text) => {
    setup('/register', {}, (url) => url.endsWith('/auth/register') ? Response.json({ code, title: 'Unavailable' }, { status }) : undefined);
    await register(userEvent.setup());
    expect(await screen.findByRole('alert')).toHaveTextContent(text);
    expect(screen.getByLabelText('Display name')).toBeVisible();
    expect(window.location.pathname).toBe('/register');
  });

  it('discards a pending response after leaving its auth route', async () => {
    let complete!: (response: Response) => void;
    setup('/forgot-password', {}, (url) => url.endsWith('/password-reset/request') ? new Promise<Response>((resolve) => { complete = resolve; }) : undefined);
    const user = userEvent.setup(); await user.type(await screen.findByLabelText('Email'), 'avery@example.test');
    await user.click(screen.getByRole('button', { name: 'Send reset message' }));
    await user.click(screen.getByRole('link', { name: 'Back to sign in' }));
    await user.click(screen.getByRole('link', { name: 'Resend verification' }));
    await act(async () => { complete(Response.json({ message: 'Request accepted.' }, { status: 202 })); });
    expect(window.location.pathname).toBe('/resend-verification');
    expect(screen.getByRole('heading', { name: 'Resend verification' })).toBeVisible();
    expect(screen.queryByText(/password reset email has been sent/)).not.toBeInTheDocument();
  });
});
