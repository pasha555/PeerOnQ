import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DeviceOwnershipLink } from '../deviceOwnershipLink';
import type { Organization } from '../types';

const organization: Organization = { id: 'org-a', name: 'Support', role: 'Owner', ownerAccountId: 'owner' };
const preview = { deviceId: 'device-internal', maskedPublicDeviceId: '***-***-***-123', displayName: 'Work laptop', expiresAtUtc: '2099-01-01T00:00:00Z', previewToken: 'one-use-preview-token' };
const base = '/portal/v1/organizations/org-a/devices/ownership-links';

describe('native ownership proof linking', () => {
  it('previews a code and requires explicit organization/device confirmation before claiming', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/preview') ? Response.json(preview) : new Response(null, { status: 204 }));
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const linked = vi.fn(); const user = userEvent.setup();
    render(<DeviceOwnershipLink organization={organization} onLinked={linked} />);
    expect(screen.getByText(/Destination organization: Support/)).toBeVisible();
    await user.type(screen.getByLabelText('Device ownership code'), 'example-ownership-code');
    await user.click(screen.getByRole('button', { name: 'Preview device' }));
    expect(await screen.findByText(preview.maskedPublicDeviceId)).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith(`${base}/preview`, expect.objectContaining({ method: 'POST', body: JSON.stringify({ code: 'example-ownership-code' }) }));
    await user.click(screen.getByRole('button', { name: 'Link device to Support' }));
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/claim'))).toBe(false);
    const dialog = within(screen.getByRole('dialog'));
    expect(dialog.getByText(/Assign Work laptop.*Support/)).toBeVisible();
    expect(dialog.getByRole('button', { name: 'Cancel' })).toHaveFocus();
    await user.click(dialog.getByRole('button', { name: 'Confirm device linking' }));
    expect(await screen.findByText('Device linked to Support.')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith(`${base}/claim`, expect.objectContaining({ method: 'POST', body: JSON.stringify({ code: 'example-ownership-code', previewToken: preview.previewToken }) }));
    expect(linked).toHaveBeenCalledOnce(); expect(storage).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Device ownership code')).toHaveValue('');
  });

  it('hides the operation for an ordinary member without making a request', () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch');
    render(<DeviceOwnershipLink organization={{ ...organization, role: 'Member' }} onLinked={vi.fn()} />);
    expect(screen.queryByLabelText('Device ownership code')).not.toBeInTheDocument(); expect(fetchMock).not.toHaveBeenCalled();
  });

  it('retains the code and displays an expired or invalid preview response', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json({ title: 'Ownership code is invalid or expired.', code: 'ownership_link_invalid' }, { status: 400 }));
    const user = userEvent.setup(); render(<DeviceOwnershipLink organization={organization} onLinked={vi.fn()} />);
    await user.type(screen.getByLabelText('Device ownership code'), 'expired-code'); await user.click(screen.getByRole('button', { name: 'Preview device' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Ownership code is invalid or expired.');
    expect(screen.getByLabelText('Device ownership code')).toHaveValue('expired-code');
    expect(screen.queryByRole('button', { name: 'Link device to Support' })).not.toBeInTheDocument();
  });

  it('does not offer confirmation for an already expired preview', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json({ ...preview, expiresAtUtc: '2000-01-01T00:00:00Z' }));
    const user = userEvent.setup(); render(<DeviceOwnershipLink organization={organization} onLinked={vi.fn()} />);
    await user.type(screen.getByLabelText('Device ownership code'), 'expired-code'); await user.click(screen.getByRole('button', { name: 'Preview device' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('This ownership code has expired.');
    expect(screen.queryByRole('button', { name: 'Link device to Support' })).not.toBeInTheDocument();
  });

  it('shows claim rejection without reporting a successful assignment', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => String(input).endsWith('/preview') ? Response.json(preview) : Response.json({ title: 'Device already belongs to another organization.', code: 'device_already_owned' }, { status: 409 }));
    const linked = vi.fn(); const user = userEvent.setup(); render(<DeviceOwnershipLink organization={organization} onLinked={linked} />);
    await user.type(screen.getByLabelText('Device ownership code'), 'example-code'); await user.click(screen.getByRole('button', { name: 'Preview device' }));
    await user.click(await screen.findByRole('button', { name: 'Link device to Support' })); await user.click(screen.getByRole('button', { name: 'Confirm device linking' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Device already belongs to another organization.');
    expect(screen.getByRole('dialog')).toBeVisible(); expect(linked).not.toHaveBeenCalled();
  });

  it('clears a preview and code when the selected organization changes', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json(preview));
    const user = userEvent.setup(); const { rerender } = render(<DeviceOwnershipLink key="org-a" organization={organization} onLinked={vi.fn()} />);
    await user.type(screen.getByLabelText('Device ownership code'), 'example-code'); await user.click(screen.getByRole('button', { name: 'Preview device' }));
    expect(await screen.findByText('Work laptop')).toBeVisible();
    rerender(<DeviceOwnershipLink key="org-b" organization={{ ...organization, id: 'org-b', name: 'Operations' }} onLinked={vi.fn()} />);
    await waitFor(() => expect(screen.getByLabelText('Device ownership code')).toHaveValue(''));
    expect(screen.queryByText('Work laptop')).not.toBeInTheDocument(); expect(screen.getByText(/Destination organization: Operations/)).toBeVisible();
  });
});
