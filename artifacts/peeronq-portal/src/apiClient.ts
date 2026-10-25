export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, message: string) { super(message); }
}

function csrfToken(): string | undefined {
  const prefix = '__Host-peeronq_customer_csrf=';
  return document.cookie.split(';').map((value) => value.trim()).find((value) => value.startsWith(prefix))?.slice(prefix.length);
}

let refreshing: Promise<void> | null = null;

export async function api<T>(path: string, init: RequestInit = {}, retrySession = true): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.body) headers.set('Content-Type', 'application/json');
  if (init.method && !['GET', 'HEAD'].includes(init.method.toUpperCase())) {
    const csrf = csrfToken();
    if (csrf) headers.set('X-CSRF-Token', csrf);
  }
  const response = await fetch(`/portal/v1${path}`, { ...init, headers, credentials: 'include' });
  if (response.status === 401 && !path.startsWith('/auth/') && retrySession && csrfToken()) {
    try {
      refreshing ??= api<void>('/auth/refresh', { method: 'POST', body: '{}' }, false).then(() => undefined).finally(() => { refreshing = null; });
      await refreshing;
      return await api<T>(path, init, false);
    } catch (cause) {
      if (cause instanceof ApiError && [401, 403].includes(cause.status)) window.dispatchEvent(new Event('peeronq:session-expired'));
      throw cause;
    }
  }
  if (!response.ok) {
    if (response.status === 401 && !path.startsWith('/auth/')) window.dispatchEvent(new Event('peeronq:session-expired'));
    const body = await response.json().catch(() => ({})) as { code?: string; title?: string };
    throw new ApiError(response.status, body.code ?? 'request_failed', body.title ?? 'The request could not be completed.');
  }
  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T;
  return response.json() as Promise<T>;
}

export const post = <T>(path: string, body?: unknown) => api<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
export const put = <T>(path: string, body: unknown) => api<T>(path, { method: 'PUT', body: JSON.stringify(body) });
export const remove = (path: string) => api<void>(path, { method: 'DELETE' });
