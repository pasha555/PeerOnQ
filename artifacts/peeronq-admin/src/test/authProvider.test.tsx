import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthProvider, useAuth } from '../auth/AuthProvider';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function SessionExpiryProbe() {
  const { status, api } = useAuth();
  return (
    <div>
      <span data-testid="auth-status">{status}</span>
      {status === 'authenticated' ? (
        <button type="button" onClick={() => void api.overview().catch(() => undefined)}>
          Load protected resource
        </button>
      ) : null}
    </div>
  );
}

describe('AuthProvider', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('returns to sign-in when an expired access token cannot refresh', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({
        accessToken: 'initial-token',
        expiresAtUtc: '2026-08-24T16:05:00Z',
      }))
      .mockResolvedValueOnce(jsonResponse({
        userId: 'owner-1',
        roles: ['Owner'],
        mfaEnabled: true,
        expiresAtUtc: '2026-08-24T20:00:00Z',
        releasePublicationEnabled: true,
        websitePublicationEnabled: true,
      }))
      .mockResolvedValueOnce(jsonResponse({
        title: 'Admin authentication is required.',
        code: 'admin_authentication_required',
      }, 401))
      .mockResolvedValueOnce(jsonResponse({
        title: 'Access is denied.',
        code: 'forbidden',
      }, 403));
    vi.stubGlobal('fetch', fetcher);

    render(<AuthProvider><SessionExpiryProbe /></AuthProvider>);

    await waitFor(() => expect(screen.getByTestId('auth-status')).toHaveTextContent('authenticated'));
    fireEvent.click(screen.getByRole('button', { name: 'Load protected resource' }));

    await waitFor(() => expect(screen.getByTestId('auth-status')).toHaveTextContent('anonymous'));
    expect(fetcher).toHaveBeenCalledTimes(4);
  });
});
