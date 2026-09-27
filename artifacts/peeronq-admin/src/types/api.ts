export type AdminRole =
  | 'Owner'
  | 'SecurityAdministrator'
  | 'OperationsAdministrator'
  | 'SupportAgent'
  | 'ReleaseManager'
  | 'ReadOnlyAnalyst';

export interface AdminSession {
  userId: string;
  roles: AdminRole[];
  mfaEnabled: boolean;
  expiresAtUtc: string;
  releasePublicationEnabled: boolean;
  websitePublicationEnabled: boolean;
}

export interface OperatorSessionRow extends AdminRow {
  sessionId: string;
  adminUserId: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  revokedAtUtc: string | null;
  userAgentFamily: string;
}

export interface AccessResult {
  status: 'authenticated';
  mfaChallengeId: null;
  accessToken: string;
  expiresAtUtc: string;
}

export interface MfaRequiredResult {
  status: 'mfa_required';
  mfaChallengeId: string;
  accessToken: null;
  expiresAtUtc: null;
}

export interface RefreshResult {
  accessToken: string;
  expiresAtUtc: string;
}

export type LoginResult = AccessResult | MfaRequiredResult;

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  errorId?: string;
  traceId?: string;
  extensions?: {
    code?: string;
    errorId?: string;
    traceId?: string;
  };
}

export interface OverviewResponse {
  totalDownloads: number;
  completedDownloads: number;
  uniqueDownloadEstimate: number;
  totalInstallations: number;
  activeInstallations: number;
  onlineDevices: number;
  activeDevicesToday: number;
  activeDevicesThisMonth: number;
  activeSessions: number;
  failedConnectionAttempts: number;
  turnSessions: number;
  crashRate: number | null;
  sessionSuccessRate: number | null;
  updateFailures: number;
  currentStableVersion: string | null;
  generatedAtUtc: string;
}

export interface DistributionBucket {
  label: string;
  count: number;
  percentage: number;
}

export interface VersionDistributionsResponse {
  clientVersions: DistributionBucket[];
  windowsVersions: DistributionBucket[];
  activeInstallations: number;
  activeWindowsInstallations: number;
  activeSinceUtc: string;
  generatedAtUtc: string;
}

export interface InfrastructureMetric {
  state: 'available' | 'unavailable' | string;
  value: number | null;
  unit: string;
}

export interface InfrastructureMetricsResponse {
  state: 'available' | 'partial' | 'unavailable' | string;
  observedAtUtc: string;
  apiLatencyP95Milliseconds: InfrastructureMetric;
  webSocketConnections: InfrastructureMetric;
  turnAllocationsPerSecond: InfrastructureMetric;
  turnBandwidthBytesPerSecond: InfrastructureMetric;
  databasePoolUsage: InfrastructureMetric;
  queueDepth: InfrastructureMetric;
}

export interface DiagnosticDetailResponse {
  diagnosticId: string;
  installationId: string;
  status: string;
  consentGranted: boolean;
  consentGrantedAtUtc: string;
  appVersion: string;
  osVersion: string;
  architecture: string;
  errorId: string | null;
  issueCategory: string | null;
  referenceCode: string | null;
  sanitizedArchiveSizeBytes: number | null;
  createdAtUtc: string;
  expiresAtUtc: string;
}

export type TableValue = string | number | boolean | null | undefined;

export interface AdminRow {
  [key: string]: TableValue;
}

export interface DeviceRow extends AdminRow {
  deviceId: string;
  maskedPublicDeviceId: string;
  displayName: string;
  identityFingerprintPrefix: string;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  isRevoked: boolean;
}

export interface InstallationRow extends AdminRow {
  installationId: string;
  deviceId: string | null;
  platform: string;
  architecture: string;
  appVersion: string;
  osVersion: string;
  channel: string;
  firstSeenAtUtc: string;
  lastSeenAtUtc: string;
  lastOnlineAtUtc: string | null;
  region: string;
  isBlocked: boolean;
}

export interface PresenceRow extends AdminRow {
  installationId: string;
  deviceId: string;
  state: string;
  region: string;
  appVersion: string;
  leaseExpiresAtUtc: string;
}

export interface SessionRow extends AdminRow {
  sessionId: string;
  maskedViewer: string;
  maskedHost: string;
  permissionMode: string;
  connectionPath: string;
  region: string;
  startedAtUtc: string;
  endedAtUtc: string | null;
  result: string;
  failureStage: string | null;
  reconnectCount: number;
  usedTurn: boolean;
}

export interface DownloadRow extends AdminRow {
  downloadId: string;
  platform: string;
  architecture: string;
  version: string;
  channel: string;
  source: string;
  campaign: string | null;
  countryCode: string | null;
  startedAtUtc: string;
  completedAtUtc: string | null;
  result: string;
}

export interface ReleaseRow extends AdminRow {
  releaseId: string;
  version: string;
  channel: string;
  architecture: string;
  publishedAtUtc: string;
  rolloutPercentage: number;
  isActive: boolean;
  offered: number;
  installed: number;
  failed: number;
  rolledBack: number;
}

export interface ReleasePublicationResponse {
  releaseId: string;
  version: string;
  channel: string;
  architecture: string;
  rolloutPercentage: number;
  publishedAtUtc: string;
}

export interface WebsiteRelease {
  version: string;
  publishedAtUtc: string;
  archiveSha256: string;
  archiveSizeBytes: number;
  isActive: boolean;
  isRollbackCandidate: boolean;
}

export type PlatformUpgradeState =
  | 'idle'
  | 'queued'
  | 'verifying'
  | 'preflight'
  | 'ready'
  | 'applying'
  | 'verifying_deployment'
  | 'succeeded'
  | 'failed'
  | 'rolling_back'
  | 'rolled_back';

export interface PlatformUpgradeCheck {
  code: string;
  label: string;
  state: 'pending' | 'passed' | 'warning' | 'failed';
  message: string;
}

export interface PlatformUpgradeStatus {
  enabled: boolean;
  environment: string;
  schemaVersion: number;
  state: PlatformUpgradeState;
  operationId: string | null;
  currentVersion: string | null;
  targetVersion: string | null;
  rollbackVersion: string | null;
  progressPercent: number;
  canApply: boolean;
  canRollback: boolean;
  blockingReason: string | null;
  message: string;
  logReference: string | null;
  updatedAtUtc: string;
  checks: PlatformUpgradeCheck[];
}

export interface PlatformUpgradeAcceptedV1 {
  requestId: string;
  action: 'stage' | 'apply' | 'rollback';
  targetVersion: string;
  state: 'queued';
  acceptedAtUtc: string;
}

export interface DiagnosticRow extends AdminRow {
  diagnosticId: string;
  installationId: string;
  status: string;
  appVersion: string;
  errorId: string | null;
  issueCategory: string | null;
  consentGranted: boolean;
  createdAtUtc: string;
  expiresAtUtc: string;
}

export interface InfrastructureRow extends AdminRow {
  region: string;
  service: string;
  state: string;
  latencyMilliseconds: number;
  observedAtUtc: string;
}

export interface AuditRow extends AdminRow {
  auditEventId: string;
  actorId: string;
  actorType: string;
  action: string;
  targetType: string;
  targetId: string | null;
  result: string;
  timestampUtc: string;
  correlationId: string;
  reason: string | null;
}

export interface AlertRow extends AdminRow {
  alertId: string;
  ruleName: string;
  severity: string;
  region: string;
  summary: string;
  runbookUrl: string;
  startedAtUtc: string;
  resolvedAtUtc: string | null;
}

export type ResourceName =
  | 'devices'
  | 'installations'
  | 'presence'
  | 'sessions'
  | 'downloads'
  | 'releases'
  | 'diagnostics'
  | 'infrastructure'
  | 'audit'
  | 'alerts';

export type ResourceRow =
  | DeviceRow
  | InstallationRow
  | PresenceRow
  | SessionRow
  | DownloadRow
  | ReleaseRow
  | DiagnosticRow
  | InfrastructureRow
  | AuditRow
  | AlertRow;

export interface PagedResponse<T extends AdminRow> {
  items: T[];
  total: number;
  offset: number;
  limit: number;
}

export interface ListQuery {
  offset: number;
  limit: number;
  search?: string;
  sortBy?: string;
  descending?: boolean;
  fromUtc?: string;
  toUtc?: string;
}

export interface OperatorSessionListQuery {
  offset: number;
  limit: number;
  userId?: string;
  includeRevoked?: boolean;
}
