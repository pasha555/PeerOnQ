import { api } from '../api';

describe('portal API client', () => {
  it('sends strict same-origin credentials and CSRF on mutations', async () => {
    Object.defineProperty(document, 'cookie', { configurable: true, value: '__Host-peeronq_customer_csrf=abc123' });
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));

    await api('/account/profile', { method: 'PUT', body: '{}' });

    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/profile', expect.objectContaining({ credentials: 'include' }));
    const init = fetchMock.mock.calls[0]?.[1];
    expect(new Headers(init?.headers).get('X-CSRF-Token')).toBe('abc123');
  });
});
