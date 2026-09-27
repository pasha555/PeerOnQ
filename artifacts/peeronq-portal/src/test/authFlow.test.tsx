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
    await user.type(screen.getByLabelText('Password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText('The credentials are invalid.')).toBeVisible();
    await waitFor(() => expect(screen.queryByText('The account service is unavailable.')).not.toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
