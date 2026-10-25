import { afterEach, describe, expect, it, vi } from 'vitest';
import { AdminApiClient, ApiError, createAdminApiClient } from '../services/apiClient';

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

describe('AdminApiClient', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('calls the browser fetch function without rebinding its receiver', async () => {
    const browserFetch = vi.fn(function browserFetch(this: unknown) {
      if (this !== undefined && this !== globalThis) throw new TypeError('Illegal invocation');
      return Promise.resolve(jsonResponse({ title: 'Anonymous', code: 'unauthorized' }, 401));
    });
    vi.stubGlobal('fetch', browserFetch);

    await expect(createAdminApiClient().refresh()).rejects.toMatchObject({
      status: 401,
      code: 'unauthorized',
    } satisfies Partial<ApiError>);
    expect(browserFetch).toHaveBeenCalledWith(
      '/admin/v1/auth/refresh',
      expect.objectContaining({ method: 'POST', credentials: 'include' }),
    );
  });

  it('keeps access tokens in memory and sends authenticated requests with secure cookies', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ status: 'authenticated', mfaChallengeId: null, accessToken: 'memory-token', expiresAtUtc: '2026-08-11T12:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse({ totalDownloads: 0, generatedAtUtc: '2026-08-11T11:00:00Z' }));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    await client.login('owner@example.invalid', 'correct-horse-battery-staple');
    await client.overview();

    const loginInit = fetcher.mock.calls[0][1] as RequestInit;
    const overviewInit = fetcher.mock.calls[1][1] as RequestInit;
    expect(loginInit.credentials).toBe('include');
    expect(new Headers(loginInit.headers).has('Authorization')).toBe(false);
    expect(new Headers(overviewInit.headers).get('Authorization')).toBe('Bearer memory-token');
    expect(window.localStorage.length).toBe(0);
  });

  it('rotates the in-memory token, retries once, and sends the offset paging contract', async () => {
    document.cookie = '__Host-peeronq_csrf=csrf-value; Secure; Path=/';
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ title: 'Expired' }, 401))
      .mockResolvedValueOnce(jsonResponse({ accessToken: 'rotated-token', expiresAtUtc: '2026-08-11T12:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse({ items: [], total: 0, offset: 50, limit: 25 }));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    const response = await client.list('devices', {
      offset: 50,
      limit: 25,
      sortBy: 'lastSeen',
      descending: true,
    });

    expect(response.total).toBe(0);
    expect(fetcher).toHaveBeenCalledTimes(3);
    const refreshHeaders = new Headers((fetcher.mock.calls[1][1] as RequestInit).headers);
    const retryHeaders = new Headers((fetcher.mock.calls[2][1] as RequestInit).headers);
    expect(refreshHeaders.get('X-CSRF-Token')).toBe('csrf-value');
    expect(retryHeaders.get('Authorization')).toBe('Bearer rotated-token');
    expect(fetcher.mock.calls[2][0]).toBe('https://api.peeronq.com/admin/v1/devices?offset=50&limit=25&sortBy=lastSeen&descending=true');
  });

  it('does not invalidate authentication for a genuine role denial', async () => {
    const onSessionInvalidated = vi.fn();
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ status: 'authenticated', mfaChallengeId: null, accessToken: 'admin-token', expiresAtUtc: '2026-08-24T16:05:00Z' }))
      .mockResolvedValueOnce(jsonResponse({ title: 'Role denied.', code: 'admin_forbidden' }, 403));
    const client = new AdminApiClient(
      'https://api.peeronq.com',
      fetcher as unknown as typeof fetch,
      onSessionInvalidated,
    );

    await client.login('owner@example.invalid', 'correct-horse-battery-staple');
    await expect(client.overview()).rejects.toMatchObject({
      status: 403,
      code: 'admin_forbidden',
    } satisfies Partial<ApiError>);

    expect(onSessionInvalidated).not.toHaveBeenCalled();
  });

  it('sends the exact MFA verification payload expected by the Admin API', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ status: 'mfa_required', mfaChallengeId: 'challenge-1', accessToken: null, expiresAtUtc: null }))
      .mockResolvedValueOnce(jsonResponse({ status: 'authenticated', mfaChallengeId: null, accessToken: 'verified-token', expiresAtUtc: '2026-08-11T12:00:00Z' }));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    const login = await client.login('owner@example.invalid', 'correct-horse-battery-staple');
    expect(login.status).toBe('mfa_required');
    await client.verifyMfa('challenge-1', '123456');

    expect(JSON.parse(String((fetcher.mock.calls[1][1] as RequestInit).body))).toEqual({
      mfaChallengeId: 'challenge-1',
      code: '123456',
    });
  });

  it('uses the exact distributions and infrastructure metrics routes', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ clientVersions: [], windowsVersions: [] }))
      .mockResolvedValueOnce(jsonResponse({ state: 'unavailable', observedAtUtc: '2026-08-11T12:00:00Z' }));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    await client.overviewDistributions();
    await client.infrastructureMetrics();

    expect(fetcher.mock.calls[0][0]).toBe('https://api.peeronq.com/admin/v1/overview/distributions?top=10');
    expect(fetcher.mock.calls[1][0]).toBe('https://api.peeronq.com/admin/v1/infrastructure/metrics');
  });

  it('sends the exact privileged administration contracts with authorization and CSRF protection', async () => {
    document.cookie = '__Host-peeronq_csrf=action-csrf; Secure; Path=/';
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ status: 'authenticated', mfaChallengeId: null, accessToken: 'admin-token', expiresAtUtc: '2026-08-11T12:00:00Z' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(jsonResponse({ releaseId: 'release-1', version: '1.2.3', channel: 'stable', architecture: 'x64', rolloutPercentage: 10, publishedAtUtc: '2026-08-11T12:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse({ releaseId: 'release-1', version: '1.2.3', channel: 'stable', architecture: 'x64', rolloutPercentage: 35, publishedAtUtc: '2026-08-11T12:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse([]))
      .mockResolvedValueOnce(jsonResponse({ version: '0.6.7', publishedAtUtc: '2026-08-12T12:00:00Z', archiveSha256: 'a'.repeat(64), archiveSizeBytes: 1024, isActive: true, isRollbackCandidate: false }))
      .mockResolvedValueOnce(jsonResponse({ version: '0.6.6', publishedAtUtc: '2026-08-11T12:00:00Z', archiveSha256: 'b'.repeat(64), archiveSizeBytes: 900, isActive: true, isRollbackCandidate: false }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(jsonResponse({ diagnosticId: 'diagnostic-1', status: 'Available' }))
      .mockResolvedValueOnce(jsonResponse({ items: [], total: 0, offset: 0, limit: 25 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    await client.login('owner@example.invalid', 'correct-horse-battery-staple');
    await client.blockInstallation('installation-1', true, 'Security investigation');
    await client.publishRelease(
      new File(['signed-envelope'], 'manifest.json', { type: 'application/json' }),
      new File(['signed-msi'], 'PeerOnQ-1.2.3-x64.msi', { type: 'application/x-msi' }),
      'Initial controlled release',
    );
    await client.replaceReleaseManifest('release-1', 'signed-rollout-envelope', 'Controlled rollout');
    await client.listWebsiteReleases();
    await client.publishWebsiteRelease(
      new File(['signed-website-envelope'], 'manifest.json', { type: 'application/json' }),
      new File(['signed-site'], 'PeerOnQ-website-0.6.7.zip', { type: 'application/zip' }),
      'Publish refreshed public website',
    );
    await client.activateWebsiteRelease('0.6.6', 'Rollback after smoke test');
    await client.revokeDevice('device-1', 'Device retired');
    await client.diagnosticDetail('diagnostic-1');
    await client.listOperatorSessions({ offset: 0, limit: 25, userId: 'user-1', includeRevoked: true });
    await client.revokeOperatorSession('session-1', 'Operator access removed');

    const calls = fetcher.mock.calls.slice(1).map(([url, init]) => ({
      url,
      method: (init as RequestInit).method ?? 'GET',
      body: (init as RequestInit).body instanceof FormData
        ? '[multipart]'
        : (init as RequestInit).body
          ? JSON.parse(String((init as RequestInit).body))
          : undefined,
      headers: new Headers((init as RequestInit).headers),
    }));
    expect(calls.map(({ url, method, body }) => ({ url, method, body }))).toEqual([
      { url: 'https://api.peeronq.com/admin/v1/installations/installation-1/block', method: 'PUT', body: { blocked: true, reason: 'Security investigation' } },
      { url: 'https://api.peeronq.com/admin/v1/releases', method: 'POST', body: '[multipart]' },
      { url: 'https://api.peeronq.com/admin/v1/releases/release-1/manifest', method: 'PUT', body: { signedManifest: 'signed-rollout-envelope', reason: 'Controlled rollout' } },
      { url: 'https://api.peeronq.com/admin/v1/website-releases', method: 'GET', body: undefined },
      { url: 'https://api.peeronq.com/admin/v1/website-releases', method: 'POST', body: '[multipart]' },
      { url: 'https://api.peeronq.com/admin/v1/website-releases/0.6.6/activate', method: 'POST', body: { reason: 'Rollback after smoke test' } },
      { url: 'https://api.peeronq.com/admin/v1/devices/device-1/revoke', method: 'POST', body: { blocked: true, reason: 'Device retired' } },
      { url: 'https://api.peeronq.com/admin/v1/diagnostics/diagnostic-1', method: 'GET', body: undefined },
      { url: 'https://api.peeronq.com/admin/v1/admin-sessions?offset=0&limit=25&includeRevoked=true&userId=user-1', method: 'GET', body: undefined },
      { url: 'https://api.peeronq.com/admin/v1/admin-sessions/session-1/revoke', method: 'POST', body: { reason: 'Operator access removed' } },
    ]);
    const releaseForm = (fetcher.mock.calls[2][1] as RequestInit).body as FormData;
    expect(releaseForm.get('reason')).toBe('Initial controlled release');
    expect((releaseForm.get('manifest') as File).name).toBe('manifest.json');
    expect((releaseForm.get('package') as File).name).toBe('PeerOnQ-1.2.3-x64.msi');
    const websiteForm = (fetcher.mock.calls[5][1] as RequestInit).body as FormData;
    expect(websiteForm.get('reason')).toBe('Publish refreshed public website');
    expect((websiteForm.get('archive') as File).name).toBe('PeerOnQ-website-0.6.7.zip');
    expect(calls[1].headers.has('Content-Type')).toBe(false);
    expect(calls[4].headers.has('Content-Type')).toBe(false);
    for (const call of calls) expect(call.headers.get('Authorization')).toBe('Bearer admin-token');
    for (const call of calls.filter(({ method }) => method !== 'GET')) {
      expect(call.headers.get('X-CSRF-Token')).toBe('action-csrf');
    }
  });

  it('sends the exact whole-platform stage, apply, and rollback contracts', async () => {
    document.cookie = '__Host-peeronq_csrf=upgrade-csrf; Secure; Path=/';
    const status = {
      enabled: true,
      environment: 'Production',
      schemaVersion: 1,
      state: 'ready',
      operationId: 'a'.repeat(32),
      currentVersion: '0.9.57',
      targetVersion: '0.9.58',
      rollbackVersion: '0.9.56',
      progressPercent: 100,
      canApply: true,
      canRollback: true,
      blockingReason: null,
      message: 'Ready to apply.',
      logReference: 'upgrade-a1b2c3',
      updatedAtUtc: '2026-08-24T17:00:00Z',
      checks: [],
    };
    const accepted = (requestId: string, action: 'stage' | 'apply' | 'rollback', targetVersion: string) => ({
      requestId,
      action,
      targetVersion,
      state: 'queued',
      acceptedAtUtc: '2026-08-24T17:01:00Z',
    });
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ status: 'authenticated', mfaChallengeId: null, accessToken: 'upgrade-token', expiresAtUtc: '2026-08-24T18:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse(status))
      .mockResolvedValueOnce(jsonResponse(accepted('b'.repeat(32), 'stage', '0.9.58')))
      .mockResolvedValueOnce(jsonResponse(accepted('c'.repeat(32), 'apply', '0.9.58')))
      .mockResolvedValueOnce(jsonResponse(accepted('d'.repeat(32), 'rollback', '0.9.56')));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    await client.login('owner@example.invalid', 'correct-horse-battery-staple');
    await client.getPlatformUpgradeStatus();
    const stageAccepted = await client.stagePlatformUpgrade(
      new File(['bundle'], 'PeerOnQ-platform-0.9.58.run', { type: 'application/octet-stream' }),
      new File(['digest'], 'PeerOnQ-platform-0.9.58.run.sha256', { type: 'text/plain' }),
      new File(['signature'], 'PeerOnQ-platform-0.9.58.run.asc', { type: 'application/pgp-signature' }),
      'Stage the complete production release',
    );
    const applyAccepted = await client.applyPlatformUpgrade('0.9.58', '0.9.57', 'Apply verified production release');
    const rollbackAccepted = await client.rollbackPlatformUpgrade('0.9.56', '0.9.58', 'Recover the previous verified release');

    expect(stageAccepted).toEqual(accepted('b'.repeat(32), 'stage', '0.9.58'));
    expect(applyAccepted).toEqual(accepted('c'.repeat(32), 'apply', '0.9.58'));
    expect(rollbackAccepted).toEqual(accepted('d'.repeat(32), 'rollback', '0.9.56'));

    const calls = fetcher.mock.calls.slice(1);
    expect(calls.map(([url, init]) => ({
      url,
      method: (init as RequestInit).method ?? 'GET',
    }))).toEqual([
      { url: 'https://api.peeronq.com/admin/v1/platform-upgrades/status', method: 'GET' },
      { url: 'https://api.peeronq.com/admin/v1/platform-upgrades/stage', method: 'POST' },
      { url: 'https://api.peeronq.com/admin/v1/platform-upgrades/apply', method: 'POST' },
      { url: 'https://api.peeronq.com/admin/v1/platform-upgrades/rollback', method: 'POST' },
    ]);
    const stageForm = (calls[1][1] as RequestInit).body as FormData;
    expect((stageForm.get('bundle') as File).name).toBe('PeerOnQ-platform-0.9.58.run');
    expect((stageForm.get('checksum') as File).name).toBe('PeerOnQ-platform-0.9.58.run.sha256');
    expect((stageForm.get('signature') as File).name).toBe('PeerOnQ-platform-0.9.58.run.asc');
    expect(stageForm.get('reason')).toBe('Stage the complete production release');
    expect(new Headers((calls[1][1] as RequestInit).headers).has('Content-Type')).toBe(false);
    expect(JSON.parse(String((calls[2][1] as RequestInit).body))).toEqual({
      targetVersion: '0.9.58',
      expectedCurrentVersion: '0.9.57',
      reason: 'Apply verified production release',
    });
    expect(JSON.parse(String((calls[3][1] as RequestInit).body))).toEqual({
      targetVersion: '0.9.56',
      expectedCurrentVersion: '0.9.58',
      reason: 'Recover the previous verified release',
    });
    for (const [, init] of calls) {
      expect(new Headers((init as RequestInit).headers).get('Authorization')).toBe('Bearer upgrade-token');
    }
    for (const [, init] of calls.slice(1)) {
      expect(new Headers((init as RequestInit).headers).get('X-CSRF-Token')).toBe('upgrade-csrf');
    }
  });

  it.each([
    [403, 'forbidden', undefined],
    [409, 'stale_write', undefined],
    [429, 'rate_limited', 45],
  ])('returns a typed error for HTTP %s', async (status, code, retryAfter) => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse(
      { title: 'Rejected', detail: 'Request rejected.', code, errorId: 'ERR-REF' },
      status,
      retryAfter ? { 'Retry-After': String(retryAfter) } : {},
    ));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    const request = client.login('owner@example.invalid', 'correct-horse-battery-staple');
    await expect(request).rejects.toMatchObject({ status, code, errorId: 'ERR-REF', retryAfterSeconds: retryAfter } satisfies Partial<ApiError>);
  });

  it('converts transport failures to a non-sensitive network error', async () => {
    const fetcher = vi.fn().mockRejectedValue(new Error('socket contained internal details'));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    await expect(client.login('owner@example.invalid', 'correct-horse-battery-staple')).rejects.toMatchObject({
      status: 0,
      code: 'network_unavailable',
      message: 'The PeerOnQ service could not be reached.',
    } satisfies Partial<ApiError>);
  });

  it('rejects a successful status that does not contain the JSON contract', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response('<html>not the API</html>', {
      status: 200,
      headers: { 'Content-Type': 'text/html' },
    }));
    const client = new AdminApiClient('https://api.peeronq.com', fetcher as unknown as typeof fetch);

    await expect(client.login('owner@example.invalid', 'correct-horse-battery-staple')).rejects.toMatchObject({
      status: 200,
      code: 'invalid_response',
    } satisfies Partial<ApiError>);
  });
});
