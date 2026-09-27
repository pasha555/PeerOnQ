import type {
  AccessResult,
  AdminSession,
  DiagnosticDetailResponse,
  InfrastructureMetricsResponse,
  ListQuery,
  LoginResult,
  OverviewResponse,
  OperatorSessionListQuery,
  OperatorSessionRow,
  PagedResponse,
  PlatformUpgradeAcceptedV1,
  PlatformUpgradeStatus,
  ProblemDetails,
  RefreshResult,
  ReleasePublicationResponse,
  ResourceName,
  ResourceRow,
  VersionDistributionsResponse,
  WebsiteRelease,
} from '../types/api';

type RequestOptions = RequestInit & {
  authenticate?: boolean;
  retryAfterRefresh?: boolean;
};

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly errorId?: string,
    public readonly retryAfterSeconds?: number,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

function normalizeBaseUrl(value: string): string {
  const trimmed = value.trim().replace(/\/$/, '');
  if (!trimmed) return '';

  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    throw new ApiError(0, 'configuration_error', 'The Admin API endpoint is invalid.');
  }

  const isLoopback = parsed.hostname === 'localhost' || parsed.hostname === '127.0.0.1';
  if (parsed.protocol !== 'https:' && !(import.meta.env.DEV && isLoopback)) {
    throw new ApiError(0, 'configuration_error', 'The Admin API endpoint must use HTTPS.');
  }

  return trimmed;
}

function readCookie(name: string): string | undefined {
  const prefix = `${encodeURIComponent(name)}=`;
  return document.cookie
    .split(';')
    .map((part) => part.trim())
    .find((part) => part.startsWith(prefix))
    ?.slice(prefix.length);
}

function csrfFromCookie(): string | undefined {
  const encoded = readCookie('__Host-peeronq_csrf') ?? readCookie('peeronq_csrf');
  return encoded ? decodeURIComponent(encoded) : undefined;
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('application/json') && !contentType.includes('application/problem+json')) {
    return {};
  }

  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return {};
  }
}

function addContentHeaders(headers: Headers, options: RequestOptions): void {
  headers.set('Accept', 'application/json');
  const bodyIsFormData = typeof FormData !== 'undefined' && options.body instanceof FormData;
  if (options.body && !bodyIsFormData && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
}

function addAuthorizationHeader(headers: Headers, options: RequestOptions, accessToken: string | null): void {
  if (options.authenticate !== false && accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
}

function addCsrfHeader(headers: Headers, options: RequestOptions, csrfToken: string | null): void {
  const method = (options.method ?? 'GET').toUpperCase();
  if (['GET', 'HEAD', 'OPTIONS'].includes(method)) return;
  const csrf = csrfToken ?? csrfFromCookie();
  if (csrf) headers.set('X-CSRF-Token', csrf);
}

function requestHeaders(options: RequestOptions, accessToken: string | null, csrfToken: string | null): Headers {
  const headers = new Headers(options.headers);
  addContentHeaders(headers, options);
  addAuthorizationHeader(headers, options, accessToken);
  addCsrfHeader(headers, options, csrfToken);
  return headers;
}

async function fetchResponse(fetcher: typeof fetch, url: string, options: RequestOptions, headers: Headers): Promise<Response> {
  try {
    return await fetcher(url, { ...options, headers, credentials: 'include' });
  } catch {
    throw new ApiError(0, 'network_unavailable', 'The PeerOnQ service could not be reached.');
  }
}

async function responseError(response: Response): Promise<ApiError> {
  const problem = await readProblem(response);
  const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10);
  return new ApiError(
    response.status,
    problemCode(problem, response.status),
    problemMessage(problem),
    problemErrorId(problem),
    Number.isFinite(retryAfter) ? retryAfter : undefined,
  );
}

function problemCode(problem: ProblemDetails, status: number): string {
  return problem.code ?? problem.extensions?.code ?? `http_${status}`;
}

function problemMessage(problem: ProblemDetails): string {
  return problem.detail ?? problem.title ?? 'The request could not be completed.';
}

function problemErrorId(problem: ProblemDetails): string | undefined {
  return problem.errorId ?? problem.extensions?.errorId;
}

async function parseSuccess<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('application/json')) {
    throw new ApiError(response.status, 'invalid_response', 'The PeerOnQ service returned an invalid response.');
  }
  try {
    return (await response.json()) as T;
  } catch {
    throw new ApiError(response.status, 'invalid_response', 'The PeerOnQ service returned malformed JSON.');
  }
}

export class AdminApiClient {
  private accessToken: string | null = null;
  private csrfToken: string | null = null;
  private refreshInFlight: Promise<RefreshResult> | null = null;

  constructor(
    private readonly baseUrl: string,
    private readonly fetcher: typeof fetch = (...args) => fetch(...args),
    private readonly onSessionInvalidated: () => void = () => undefined,
  ) {}

  clearSession(): void {
    this.accessToken = null;
    this.csrfToken = null;
  }

  private setAccess<T extends { accessToken: string }>(result: T): T {
    this.accessToken = result.accessToken;
    return result;
  }

  private async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const headers = requestHeaders(options, this.accessToken, this.csrfToken);
    const response = await fetchResponse(this.fetcher, `${this.baseUrl}${path}`, options, headers);

    const responseCsrf = response.headers.get('X-CSRF-Token');
    if (responseCsrf) this.csrfToken = responseCsrf;

    if (
      response.status === 401 &&
      options.authenticate !== false &&
      options.retryAfterRefresh !== false
    ) {
      await this.refresh();
      return this.request<T>(path, { ...options, retryAfterRefresh: false });
    }

    if (!response.ok) throw await responseError(response);
    return parseSuccess<T>(response);
  }

  async login(email: string, password: string): Promise<LoginResult> {
    const result = await this.request<LoginResult>('/admin/v1/auth/login', {
      method: 'POST',
      authenticate: false,
      retryAfterRefresh: false,
      body: JSON.stringify({ email, password }),
    });
    return result.status === 'authenticated' ? this.setAccess(result) : result;
  }

  async verifyMfa(challengeId: string, code: string): Promise<AccessResult> {
    const result = await this.request<AccessResult>('/admin/v1/auth/mfa/verify', {
      method: 'POST',
      authenticate: false,
      retryAfterRefresh: false,
      body: JSON.stringify({ mfaChallengeId: challengeId, code }),
    });
    return this.setAccess(result);
  }

  async refresh(): Promise<RefreshResult> {
    if (this.refreshInFlight) return this.refreshInFlight;

    this.refreshInFlight = this.request<RefreshResult>('/admin/v1/auth/refresh', {
      method: 'POST',
      authenticate: false,
      retryAfterRefresh: false,
    })
      .then((result) => this.setAccess(result))
      .catch((error: unknown) => {
        this.clearSession();
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
          this.onSessionInvalidated();
        }
        throw error;
      })
      .finally(() => {
        this.refreshInFlight = null;
      });

    return this.refreshInFlight;
  }

  // Used through createAdminApiClient's inferred return type in AuthProvider.
  // fallow-ignore-next-line unused-class-member
  async logout(): Promise<void> {
    try {
      await this.request<void>('/admin/v1/auth/logout', {
        method: 'POST',
        retryAfterRefresh: false,
      });
    } finally {
      this.clearSession();
    }
  }

  // Used through createAdminApiClient's inferred return type in AuthProvider.
  // fallow-ignore-next-line unused-class-member
  session(): Promise<AdminSession> {
    return this.request<AdminSession>('/admin/v1/auth/session');
  }

  overview(signal?: AbortSignal): Promise<OverviewResponse> {
    return this.request<OverviewResponse>('/admin/v1/overview', { signal });
  }

  overviewDistributions(signal?: AbortSignal): Promise<VersionDistributionsResponse> {
    return this.request<VersionDistributionsResponse>('/admin/v1/overview/distributions?top=10', { signal });
  }

  infrastructureMetrics(signal?: AbortSignal): Promise<InfrastructureMetricsResponse> {
    return this.request<InfrastructureMetricsResponse>('/admin/v1/infrastructure/metrics', { signal });
  }

  list<T extends ResourceRow>(
    resource: ResourceName,
    query: ListQuery,
    signal?: AbortSignal,
  ): Promise<PagedResponse<T>> {
    const search = new URLSearchParams({
      offset: String(query.offset),
      limit: String(query.limit),
    });
    if (query.search) search.set('search', query.search);
    if (query.sortBy) search.set('sortBy', query.sortBy);
    if (query.descending !== undefined) search.set('descending', String(query.descending));
    if (query.fromUtc) search.set('fromUtc', query.fromUtc);
    if (query.toUtc) search.set('toUtc', query.toUtc);
    return this.request<PagedResponse<T>>(`/admin/v1/${resource}?${search}`, { signal });
  }

  blockInstallation(installationId: string, blocked: boolean, reason: string): Promise<void> {
    return this.request<void>(`/admin/v1/installations/${encodeURIComponent(installationId)}/block`, {
      method: 'PUT',
      body: JSON.stringify({ blocked, reason }),
    });
  }

  publishRelease(manifest: File, packageFile: File, reason: string): Promise<ReleasePublicationResponse> {
    const form = new FormData();
    form.append('manifest', manifest);
    form.append('package', packageFile);
    form.append('reason', reason);
    return this.request<ReleasePublicationResponse>('/admin/v1/releases', {
      method: 'POST',
      body: form,
    });
  }

  replaceReleaseManifest(
    releaseId: string,
    signedManifest: string,
    reason: string,
  ): Promise<ReleasePublicationResponse> {
    return this.request<ReleasePublicationResponse>(`/admin/v1/releases/${encodeURIComponent(releaseId)}/manifest`, {
      method: 'PUT',
      body: JSON.stringify({ signedManifest, reason }),
    });
  }

  listWebsiteReleases(signal?: AbortSignal): Promise<WebsiteRelease[]> {
    return this.request<WebsiteRelease[]>('/admin/v1/website-releases', { signal });
  }

  publishWebsiteRelease(manifest: File, archive: File, reason: string): Promise<WebsiteRelease> {
    const form = new FormData();
    form.append('manifest', manifest);
    form.append('archive', archive);
    form.append('reason', reason);
    return this.request<WebsiteRelease>('/admin/v1/website-releases', {
      method: 'POST',
      body: form,
    });
  }

  activateWebsiteRelease(version: string, reason: string): Promise<WebsiteRelease> {
    return this.request<WebsiteRelease>(`/admin/v1/website-releases/${encodeURIComponent(version)}/activate`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }

  getPlatformUpgradeStatus(signal?: AbortSignal): Promise<PlatformUpgradeStatus> {
    return this.request<PlatformUpgradeStatus>('/admin/v1/platform-upgrades/status', { signal });
  }

  stagePlatformUpgrade(
    bundle: File,
    checksum: File,
    signature: File,
    reason: string,
  ): Promise<PlatformUpgradeAcceptedV1> {
    const form = new FormData();
    form.append('bundle', bundle);
    form.append('checksum', checksum);
    form.append('signature', signature);
    form.append('reason', reason);
    return this.request<PlatformUpgradeAcceptedV1>('/admin/v1/platform-upgrades/stage', {
      method: 'POST',
      body: form,
    });
  }

  applyPlatformUpgrade(
    targetVersion: string,
    expectedCurrentVersion: string,
    reason: string,
  ): Promise<PlatformUpgradeAcceptedV1> {
    return this.request<PlatformUpgradeAcceptedV1>('/admin/v1/platform-upgrades/apply', {
      method: 'POST',
      body: JSON.stringify({ targetVersion, expectedCurrentVersion, reason }),
    });
  }

  rollbackPlatformUpgrade(
    targetVersion: string,
    expectedCurrentVersion: string,
    reason: string,
  ): Promise<PlatformUpgradeAcceptedV1> {
    return this.request<PlatformUpgradeAcceptedV1>('/admin/v1/platform-upgrades/rollback', {
      method: 'POST',
      body: JSON.stringify({ targetVersion, expectedCurrentVersion, reason }),
    });
  }

  revokeDevice(deviceId: string, reason: string): Promise<void> {
    return this.request<void>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/revoke`, {
      method: 'POST',
      body: JSON.stringify({ blocked: true, reason }),
    });
  }

  diagnosticDetail(diagnosticId: string, signal?: AbortSignal): Promise<DiagnosticDetailResponse> {
    return this.request<DiagnosticDetailResponse>(
      `/admin/v1/diagnostics/${encodeURIComponent(diagnosticId)}`,
      { signal },
    );
  }

  listOperatorSessions(
    query: OperatorSessionListQuery,
    signal?: AbortSignal,
  ): Promise<PagedResponse<OperatorSessionRow>> {
    const search = new URLSearchParams({
      offset: String(query.offset),
      limit: String(query.limit),
      includeRevoked: String(query.includeRevoked ?? false),
    });
    if (query.userId) search.set('userId', query.userId);
    return this.request<PagedResponse<OperatorSessionRow>>(`/admin/v1/admin-sessions?${search}`, { signal });
  }

  revokeOperatorSession(sessionId: string, reason: string): Promise<void> {
    return this.request<void>(`/admin/v1/admin-sessions/${encodeURIComponent(sessionId)}/revoke`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    });
  }
}

export function createAdminApiClient(onSessionInvalidated: () => void = () => undefined): AdminApiClient {
  return new AdminApiClient(
    normalizeBaseUrl(import.meta.env.VITE_PEERONQ_ADMIN_API_BASE_URL ?? ''),
    (...args) => fetch(...args),
    onSessionInvalidated,
  );
}
