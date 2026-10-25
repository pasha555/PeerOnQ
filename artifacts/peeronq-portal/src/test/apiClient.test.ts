import { api } from '../api';

describe('portal API client', () => {
  it('shares a single refresh across concurrent expired requests and retries with rotated CSRF', async () => {
    let csrf = 'before'; let authenticated = false;
    Object.defineProperty(document, 'cookie', { configurable: true, get: () => `__Host-peeronq_customer_csrf=${csrf}` });
    let finish!: () => void;
    const pending = new Promise<void>((resolve) => { finish = resolve; });
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      if (String(input).endsWith('/auth/refresh')) { await pending; csrf = 'after'; authenticated = true; return Response.json({ sessionId: 'rotated' }); }
      return authenticated ? new Response(null, { status: 204 }) : Response.json({}, { status: 401 });
    });
    const first = api('/account/profile', { method: 'PUT', body: '{}' });
    const second = api('/account/sessions');
    await vi.waitFor(() => expect(fetchMock.mock.calls.filter(([path]) => String(path).endsWith('/auth/refresh'))).toHaveLength(1));
    finish(); await Promise.all([first, second]);
    const mutations = fetchMock.mock.calls.filter(([path]) => String(path).endsWith('/account/profile'));
    expect(new Headers(mutations.at(-1)?.[1]?.headers).get('X-CSRF-Token')).toBe('after');
  });

  it('does not refresh invalid login credentials or store returned credentials', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(Response.json({ code: 'invalid_credentials' }, { status: 401 }));
    await expect(api('/auth/login', { method: 'POST', body: '{}' })).rejects.toMatchObject({ status: 401 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0);
  });

  it('sends strict same-origin credentials and CSRF on mutations', async () => {
    Object.defineProperty(document, 'cookie', { configurable: true, value: '__Host-peeronq_customer_csrf=abc123' });
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));

    await api('/account/profile', { method: 'PUT', body: '{}' });

    expect(fetchMock).toHaveBeenCalledWith('/portal/v1/account/profile', expect.objectContaining({ credentials: 'include' }));
    const init = fetchMock.mock.calls[0]?.[1];
    expect(new Headers(init?.headers).get('X-CSRF-Token')).toBe('abc123');
  });
});
