import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { AuthProvider } from '../auth';
import { capabilities } from './authCapabilities';

const profile = { id: 'owner', displayName: 'Avery', email: 'avery@example.test', emailVerified: true, mfaEnabled: false, createdAtUtc: '2026-01-01T00:00:00Z' };
const organization = { id: 'org', name: 'Support', role: 'Owner', ownerAccountId: 'owner' };
const members = [{ accountId: 'owner', displayName: 'Avery', email: profile.email, role: 'Owner', joinedAtUtc: profile.createdAtUtc }, { accountId: 'member', displayName: 'Morgan', email: 'morgan@example.test', role: 'Member', joinedAtUtc: profile.createdAtUtc }];

function mockApi(custom?: (path: string, init?: RequestInit) => Response | undefined, role = 'Owner') {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const path = String(input); const customResponse = custom?.(path, init); if (customResponse) return customResponse;
    if (path.endsWith('/auth/capabilities')) return Response.json(capabilities);
    if (path.endsWith('/account/profile')) return Response.json(profile);
    if (path === '/portal/v1/organizations/') return Response.json([{ ...organization, role }]);
    if (path === '/portal/v1/organizations/org/members') return Response.json(members);
    if (path.endsWith('/teams')) return Response.json([{ id: 'team', name: 'Operators', createdAtUtc: profile.createdAtUtc }]);
    if (path.endsWith('/teams/team/members')) return Response.json([]);
    return Response.json([]);
  });
}
function open(path: string) { history.replaceState({}, '', path); return render(<AuthProvider><App /></AuthProvider>); }

describe('organization lifecycle', () => {
  it('changes a non-owner role through the selected tenant and cannot directly change the owner', async () => {
    const fetchMock = mockApi((path, init) => init?.method === 'PUT' ? new Response(null, { status: 204 }) : undefined);
    const user = userEvent.setup(); open('/members');
    const role = await screen.findByLabelText('Role for Morgan');
    expect(screen.queryByLabelText('Role for Avery')).not.toBeInTheDocument();
    await user.selectOptions(role, 'Technician'); await user.click(screen.getByRole('button', { name: 'Save role' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/portal/v1/organizations/org/members/member/role', expect.objectContaining({ method: 'PUT', body: JSON.stringify({ role: 'Technician' }) })));
  });

  it('requires confirmation before member removal and shows a server rejection', async () => {
    const fetchMock = mockApi((path, init) => init?.method === 'DELETE' ? Response.json({ title: 'Membership changed. Reload before removing.', code: 'conflict' }, { status: 409 }) : undefined);
    const user = userEvent.setup(); open('/members');
    await user.click(await screen.findByRole('button', { name: 'Remove Morgan' }));
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Confirm member removal' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Membership changed. Reload before removing.');
    expect(screen.getByRole('dialog')).toBeVisible();
  });

  it('does not offer lifecycle mutations to an ordinary organization member', async () => {
    const fetchMock = mockApi(undefined, 'Member'); open('/members');
    expect(await screen.findByRole('heading', { name: 'Morgan' })).toBeVisible();
    expect(screen.queryByLabelText('Role for Morgan')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove Morgan' })).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([, init]) => ['DELETE', 'PUT', 'POST'].includes(init?.method ?? ''))).toBe(false);
  });

  it('transfers ownership only to an existing member after confirmation', async () => {
    let transferred = false;
    const fetchMock = mockApi((path, init) => {
      if (path.endsWith('/transfer-ownership') && init?.method === 'POST') { transferred = true; return new Response(null, { status: 204 }); }
      if (path === '/portal/v1/organizations/' && transferred) return Response.json([{ ...organization, role: 'Administrator', ownerAccountId: 'member' }]);
    });
    const user = userEvent.setup(); open('/organizations');
    await user.selectOptions(await screen.findByLabelText('New organization owner'), 'member');
    await user.click(screen.getByRole('button', { name: 'Transfer ownership' }));
    expect(transferred).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Confirm ownership transfer' }));
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Organization ownership' })).not.toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/organizations/org/transfer-ownership', expect.objectContaining({ method: 'POST', body: JSON.stringify({ newOwnerAccountId: 'member' }) }));
  });

  it('preserves server safeguards when organization deletion is rejected', async () => {
    mockApi((path, init) => path === '/portal/v1/organizations/org' && init?.method === 'DELETE' ? Response.json({ title: 'Reassign or revoke organization devices before deletion.', code: 'organization_devices_present' }, { status: 409 }) : undefined);
    const user = userEvent.setup(); open('/organizations');
    await user.click(await screen.findByRole('button', { name: 'Delete Support' }));
    await user.click(screen.getByRole('button', { name: 'Confirm organization deletion' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Reassign or revoke organization devices before deletion.');
    expect(screen.getByRole('dialog')).toBeVisible();
  });

  it('adds an existing organization member to a team and shows authoritative membership', async () => {
    let added = false;
    const fetchMock = mockApi((path, init) => {
      if (path.endsWith('/teams/team/members')) {
        if (init?.method === 'POST') { added = true; return new Response(null, { status: 204 }); }
        return Response.json(added ? [members[1]] : []);
      }
    });
    const user = userEvent.setup(); open('/teams');
    await user.selectOptions(await screen.findByLabelText('Add member to Operators'), 'member');
    await user.click(screen.getByRole('button', { name: 'Add team member' }));
    expect(await screen.findByRole('button', { name: 'Remove Morgan from Operators' })).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/organizations/org/teams/team/members', expect.objectContaining({ method: 'POST', body: JSON.stringify({ accountId: 'member' }) }));
  });

  it('removes a team member only after confirmation without removing organization membership', async () => {
    let removed = false;
    const fetchMock = mockApi((path, init) => {
      if (path.endsWith('/teams/team/members/member') && init?.method === 'DELETE') { removed = true; return new Response(null, { status: 204 }); }
      if (path.endsWith('/teams/team/members')) return Response.json(removed ? [] : [members[1]]);
    });
    const user = userEvent.setup(); open('/teams');
    await user.click(await screen.findByRole('button', { name: 'Remove Morgan from Operators' }));
    expect(removed).toBe(false);
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Confirm team removal' }));
    expect(await screen.findByText('No members in this team.')).toBeVisible();
    expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/org/members/member') && init?.method === 'DELETE')).toBe(false);
  });
});
