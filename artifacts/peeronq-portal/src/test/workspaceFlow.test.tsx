import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { AuthProvider } from '../auth';

const profile = { id: 'account-1', displayName: 'Avery', email: 'avery@example.test', emailVerified: true, mfaEnabled: true, createdAtUtc: '2026-01-01T00:00:00Z' };
const organizations = [
  { id: 'org-a', name: 'Design team', role: 'Owner', ownerAccountId: profile.id },
  { id: 'org-b', name: 'Support team', role: 'Member', ownerAccountId: 'another-account' },
];
const device = (name: string) => ({ id: name, displayName: name, maskedPublicDeviceId: '123-***-***-456', lastSeenAtUtc: '2026-09-20T09:00:00Z', isRevoked: false });
const remoteSession = { id: 'remote-1', permissionMode: 'FullControl', connectionPath: 'LanDirect', lifecycle: 'Ended', startedAtUtc: '2026-09-20T09:00:00Z', endedAtUtc: '2026-09-20T09:15:00Z' };

function renderPortal(path = '/') {
  window.history.replaceState({}, '', path);
  window.localStorage.removeItem('peeronq_portal_organization');
  return render(<AuthProvider><App /></AuthProvider>);
}

function mockApi(handler?: (path: string, init?: RequestInit) => Response | Promise<Response> | undefined) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const path = String(input);
    const custom = handler?.(path, init);
    if (custom) return custom;
    if (path === '/portal/v1/account/profile') return Response.json(profile);
    if (path === '/portal/v1/organizations/') return Response.json(organizations);
    return Response.json([]);
  });
}

describe('portal workspace', () => {
  it('releases the mobile focus trap when the viewport grows to desktop width', async () => {
    mockApi();
    let mobile = true;
    let notifyViewportChange: (() => void) | undefined;
    const removeListener = vi.fn();
    const matchMedia = vi.fn().mockReturnValue({
      get matches() { return mobile; },
      addEventListener: (_name: string, listener: () => void) => { notifyViewportChange = listener; },
      removeEventListener: removeListener,
    });
    vi.stubGlobal('matchMedia', matchMedia);
    try {
      const user = userEvent.setup();
      const view = renderPortal();
      await screen.findByRole('heading', { name: 'Overview' });
      const openNavigation = screen.getByRole('button', { name: 'Open navigation' });
      await user.click(openNavigation);
      expect(openNavigation).toHaveAttribute('aria-expanded', 'true');
      const lastNavigationLink = screen.getByRole('link', { name: 'Public website' });
      lastNavigationLink.focus();
      const trappedTab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
      act(() => { lastNavigationLink.dispatchEvent(trappedTab); });
      expect(trappedTab.defaultPrevented).toBe(true);

      act(() => { mobile = false; notifyViewportChange?.(); });
      expect(openNavigation).toHaveAttribute('aria-expanded', 'false');
      expect(screen.getByRole('link', { name: 'Overview' })).toHaveFocus();
      lastNavigationLink.focus();
      const desktopTab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
      act(() => { lastNavigationLink.dispatchEvent(desktopTab); });
      expect(desktopTab.defaultPrevented).toBe(false);
      view.unmount();
      expect(removeListener).toHaveBeenCalledWith('change', expect.any(Function));
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('shows actual organization records without inventing online status or device identity', async () => {
    const fetchMock = mockApi((path) => {
      if (path === '/portal/v1/organizations/org-a/devices') return Response.json([device('Design laptop')]);
      if (path === '/portal/v1/organizations/org-a/sessions') return Response.json([remoteSession]);
    });
    const user = userEvent.setup();
    renderPortal();
    expect(await screen.findByText('Full Control')).toBeVisible();
    expect(screen.getByText('LAN direct')).toBeVisible();
    expect(screen.getByText('1', { selector: '.summary-value' })).toBeVisible();
    await user.click(screen.getByRole('link', { name: 'Manage devices' }));
    expect(await screen.findByRole('heading', { name: 'Design laptop' })).toBeVisible();
    expect(screen.getByText('123-***-***-456')).toBeVisible();
    expect(screen.getByText('Assigned')).toBeVisible();
    expect(screen.queryByText(/online/i)).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.every(([url]) => String(url).startsWith('/portal/v1/'))).toBe(true);
  });

  it('keeps remote history distinct from browser sessions and preserves real revocation', async () => {
    let revoked = false;
    const fetchMock = mockApi((path, init) => {
      if (path === '/portal/v1/organizations/org-a/sessions') return Response.json([remoteSession]);
      if (path === '/portal/v1/account/sessions/browser-1' && init?.method === 'DELETE') { revoked = true; return new Response(null, { status: 204 }); }
      if (path === '/portal/v1/account/sessions') return Response.json([{ id: 'browser-1', userAgentSummary: 'Browser on Windows', createdAtUtc: '2026-09-20T09:00:00Z', expiresAtUtc: '2099-09-20T09:00:00Z', revokedAtUtc: revoked ? '2026-09-20T10:00:00Z' : null }]);
    });
    const user = userEvent.setup();
    renderPortal('/remote-sessions');
    expect(await screen.findByText('Full Control')).toBeVisible();
    expect(fetchMock.mock.calls.some(([url]) => url === '/portal/v1/account/sessions')).toBe(false);
    await user.click(screen.getAllByRole('link', { name: 'Browser sessions' })[0]!);
    expect(await screen.findByRole('heading', { name: 'Browser on Windows' })).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Revoke session' }));
    expect(await screen.findByText('Revoked')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/sessions/browser-1', expect.objectContaining({ method: 'DELETE', credentials: 'include' }));
  });

  it('reports a rejected browser-session revocation instead of implying success', async () => {
    mockApi((path, init) => {
      if (init?.method === 'DELETE') return Response.json({ code: 'request_failed', title: 'Session could not be revoked.' }, { status: 503 });
      if (path === '/portal/v1/account/sessions') return Response.json([{ id: 'browser-1', userAgentSummary: 'Browser on Windows', createdAtUtc: '2026-09-20T09:00:00Z', expiresAtUtc: '2099-09-20T09:00:00Z', revokedAtUtc: null }]);
    });
    const user = userEvent.setup();
    renderPortal('/sessions');
    await user.click(await screen.findByRole('button', { name: 'Revoke session' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Session could not be revoked.');
    expect(screen.getByText('Active')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Revoke session' })).toBeEnabled();
  });

  it('does not show a late response from the previously selected organization', async () => {
    let finishFirst: (response: Response) => void = () => undefined;
    const first = new Promise<Response>((resolve) => { finishFirst = resolve; });
    const fetchMock = mockApi((path) => {
      if (path === '/portal/v1/organizations/org-a/devices') return first;
      if (path === '/portal/v1/organizations/org-b/devices') return Response.json([device('Support laptop')]);
    });
    const user = userEvent.setup();
    renderPortal('/devices');
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/portal/v1/organizations/org-a/devices', expect.anything()));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Active organization' }), 'org-b');
    expect(await screen.findByRole('heading', { name: 'Support laptop' })).toBeVisible();
    await act(async () => { finishFirst(Response.json([device('Old organization laptop')])); });
    expect(screen.queryByText('Old organization laptop')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Support laptop' })).toBeVisible();
  });

  it('shows an actionable organization-load error and can retry', async () => {
    let available = false;
    mockApi((path) => {
      if (path === '/portal/v1/organizations/') return available ? Response.json([]) : Response.json({ title: 'Unavailable' }, { status: 503 });
    });
    const user = userEvent.setup();
    renderPortal('/devices');
    expect(await screen.findByRole('alert')).toHaveTextContent('Your organizations could not be loaded.');
    expect(screen.queryByText('No assigned devices')).not.toBeInTheDocument();
    available = true;
    await user.click(screen.getByRole('button', { name: 'Retry organizations' }));
    expect(await screen.findByRole('heading', { name: 'Your shared workspace starts here' })).toBeVisible();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('displays device API errors instead of remaining in loading state', async () => {
    mockApi((path) => path.endsWith('/devices') ? Response.json({ title: 'Device service unavailable' }, { status: 503 }) : undefined);
    renderPortal('/devices');
    expect(await screen.findByRole('alert')).toHaveTextContent('Device service unavailable');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(screen.queryByText('Loading organization devices…')).not.toBeInTheDocument();
  });

  it('links downloads to the authoritative public page without fabricated packages', async () => {
    mockApi();
    renderPortal('/downloads');
    expect(await screen.findByRole('link', { name: 'View available downloads' })).toHaveAttribute('href', 'https://peeronq.com/#download');
    expect(screen.getByRole('link', { name: 'View GitHub' })).toHaveAttribute('href', 'https://github.com/pasha555/PeerOnQ');
    expect(screen.queryByRole('link', { name: /admin|grafana|prometheus/i })).not.toBeInTheDocument();
  });
});
