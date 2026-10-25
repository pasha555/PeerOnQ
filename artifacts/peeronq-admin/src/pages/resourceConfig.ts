import type { ResourceName, ResourceRow } from '../types/api';

export type ColumnKind = 'text' | 'number' | 'date' | 'status' | 'percent' | 'reference' | 'link' | 'boolean';

export interface ResourceColumn {
  key: string;
  label: string;
  kind?: ColumnKind;
  sortKey?: string;
  trueLabel?: string;
  falseLabel?: string;
}

export interface ResourceConfig {
  name: ResourceName;
  title: string;
  description: string;
  searchLabel: string;
  defaultSort: string;
  supportsFilters?: boolean;
  rowKey(row: ResourceRow, index: number): string;
  columns: ResourceColumn[];
}

const guidKey = (key: string) => (row: ResourceRow, index: number) => String(row[key] ?? index);

export const resourceConfigs: Record<ResourceName, ResourceConfig> = {
  devices: {
    name: 'devices', title: 'Devices', description: 'Device identity and lifecycle. Public device IDs are provided only in their API-masked form.', searchLabel: 'Search masked device ID or display name', defaultSort: 'lastSeen', rowKey: guidKey('deviceId'),
    columns: [
      { key: 'maskedPublicDeviceId', label: 'Device', kind: 'reference' },
      { key: 'displayName', label: 'Display name', sortKey: 'displayName' },
      { key: 'identityFingerprintPrefix', label: 'Identity fingerprint', kind: 'reference' },
      { key: 'createdAtUtc', label: 'Created', kind: 'date', sortKey: 'createdAt' },
      { key: 'lastSeenAtUtc', label: 'Last seen', kind: 'date', sortKey: 'lastSeen' },
      { key: 'isRevoked', label: 'Trust', kind: 'boolean', trueLabel: 'Revoked', falseLabel: 'Active' },
    ],
  },
  installations: {
    name: 'installations', title: 'Installations', description: 'Installed application instances, kept distinct from download events. Internal installation and device GUIDs are not shown.', searchLabel: 'Search version, operating system, channel, or region', defaultSort: 'lastSeen', rowKey: guidKey('installationId'),
    columns: [
      { key: 'platform', label: 'Platform' }, { key: 'architecture', label: 'Architecture' },
      { key: 'appVersion', label: 'Version', sortKey: 'appVersion' }, { key: 'osVersion', label: 'Operating system' },
      { key: 'channel', label: 'Channel', kind: 'status' },
      { key: 'firstSeenAtUtc', label: 'First seen', kind: 'date', sortKey: 'firstSeen' },
      { key: 'lastSeenAtUtc', label: 'Last seen', kind: 'date', sortKey: 'lastSeen' },
      { key: 'lastOnlineAtUtc', label: 'Last online', kind: 'date' }, { key: 'region', label: 'Region', sortKey: 'region' },
      { key: 'isBlocked', label: 'Access', kind: 'boolean', trueLabel: 'Blocked', falseLabel: 'Allowed' },
    ],
  },
  presence: {
    name: 'presence', title: 'Presence', description: 'Live connection leases from the presence service, ordered by lease expiry. Internal device and installation GUIDs are not shown.', searchLabel: '', defaultSort: 'expiresAt', supportsFilters: false, rowKey: guidKey('installationId'),
    columns: [
      { key: 'state', label: 'State', kind: 'status' }, { key: 'region', label: 'Region' },
      { key: 'appVersion', label: 'Version' },
      { key: 'leaseExpiresAtUtc', label: 'Lease expiry', kind: 'date', sortKey: 'expiresAt' },
    ],
  },
  sessions: {
    name: 'sessions', title: 'Sessions', description: 'Operational session metadata only. No screen, input, clipboard, or file content is collected.', searchLabel: 'Search masked peer, result, or region', defaultSort: 'startedAt', rowKey: guidKey('sessionId'),
    columns: [
      { key: 'maskedViewer', label: 'Viewer', kind: 'reference' }, { key: 'maskedHost', label: 'Host', kind: 'reference' },
      { key: 'permissionMode', label: 'Mode' }, { key: 'connectionPath', label: 'Path', kind: 'status' },
      { key: 'region', label: 'Region', sortKey: 'region' }, { key: 'startedAtUtc', label: 'Started', kind: 'date', sortKey: 'startedAt' },
      { key: 'endedAtUtc', label: 'Ended', kind: 'date', sortKey: 'endedAt' }, { key: 'result', label: 'Result', kind: 'status' },
      { key: 'failureStage', label: 'Failure stage' }, { key: 'reconnectCount', label: 'Reconnects', kind: 'number' },
      { key: 'usedTurn', label: 'Transport', kind: 'boolean', trueLabel: 'Relayed', falseLabel: 'Direct' },
    ],
  },
  downloads: {
    name: 'downloads', title: 'Downloads', description: 'Privacy-limited download events. Totals, completions, unique estimates, and installations remain separate metrics.', searchLabel: 'Search version, source, or campaign', defaultSort: 'startedAt', rowKey: guidKey('downloadId'),
    columns: [
      { key: 'version', label: 'Version', kind: 'reference', sortKey: 'version' }, { key: 'channel', label: 'Channel', kind: 'status' },
      { key: 'platform', label: 'Platform' }, { key: 'architecture', label: 'Architecture' }, { key: 'source', label: 'Source' },
      { key: 'campaign', label: 'Campaign' }, { key: 'countryCode', label: 'Country' },
      { key: 'startedAtUtc', label: 'Started', kind: 'date', sortKey: 'startedAt' },
      { key: 'completedAtUtc', label: 'Completed', kind: 'date', sortKey: 'completedAt' }, { key: 'result', label: 'Result', kind: 'status' },
    ],
  },
  releases: {
    name: 'releases', title: 'Releases', description: 'Publish offline-signed MSI packages and manifests, then monitor rollout and adoption. Every release mutation is separately authorized, verified, and audited.', searchLabel: 'Search version or channel', defaultSort: 'publishedAt', rowKey: guidKey('releaseId'),
    columns: [
      { key: 'version', label: 'Version', kind: 'reference', sortKey: 'version' }, { key: 'channel', label: 'Channel', kind: 'status' },
      { key: 'architecture', label: 'Architecture' }, { key: 'publishedAtUtc', label: 'Published', kind: 'date', sortKey: 'publishedAt' },
      { key: 'rolloutPercentage', label: 'Rollout', kind: 'percent', sortKey: 'rollout' },
      { key: 'isActive', label: 'Release', kind: 'boolean', trueLabel: 'Active', falseLabel: 'Inactive' },
      { key: 'offered', label: 'Offered', kind: 'number' }, { key: 'installed', label: 'Installed', kind: 'number' },
      { key: 'failed', label: 'Failed', kind: 'number' }, { key: 'rolledBack', label: 'Rolled back', kind: 'number' },
    ],
  },
  diagnostics: {
    name: 'diagnostics', title: 'Diagnostics', description: 'Consent-based sanitized bundle metadata with explicit expiry. Internal installation GUIDs are not shown and access is audited by the API.', searchLabel: 'Search error, category, or application version', defaultSort: 'createdAt', rowKey: guidKey('diagnosticId'),
    columns: [
      { key: 'createdAtUtc', label: 'Created', kind: 'date', sortKey: 'createdAt' }, { key: 'appVersion', label: 'Version', sortKey: 'appVersion' },
      { key: 'errorId', label: 'Error ID', kind: 'reference' }, { key: 'issueCategory', label: 'Category' },
      { key: 'consentGranted', label: 'Consent', kind: 'boolean', trueLabel: 'Granted', falseLabel: 'Not granted' },
      { key: 'expiresAtUtc', label: 'Expires', kind: 'date', sortKey: 'expiresAt' }, { key: 'status', label: 'Status', kind: 'status' },
    ],
  },
  infrastructure: {
    name: 'infrastructure', title: 'Infrastructure', description: 'Dependency health snapshots by service and region, with live bounded metrics shown separately.', searchLabel: 'Search service or region', defaultSort: 'observedAt',
    rowKey: (row, index) => `${row.region ?? 'unknown'}:${row.service ?? 'unknown'}:${row.observedAtUtc ?? index}`,
    columns: [
      { key: 'service', label: 'Service', sortKey: 'service' }, { key: 'region', label: 'Region', sortKey: 'region' },
      { key: 'state', label: 'State', kind: 'status' }, { key: 'latencyMilliseconds', label: 'Latency ms', kind: 'number' },
      { key: 'observedAtUtc', label: 'Observed', kind: 'date', sortKey: 'observedAt' },
    ],
  },
  audit: {
    name: 'audit', title: 'Audit', description: 'Immutable, paginated security events. Internal target identifiers are retained by the server and intentionally not rendered here.', searchLabel: 'Search actor, action, or correlation ID', defaultSort: 'timestamp', rowKey: guidKey('auditEventId'),
    columns: [
      { key: 'timestampUtc', label: 'Time', kind: 'date', sortKey: 'timestamp' }, { key: 'actorType', label: 'Actor type' },
      { key: 'actorId', label: 'Actor', kind: 'reference' }, { key: 'action', label: 'Action', sortKey: 'action' },
      { key: 'targetType', label: 'Target type' }, { key: 'result', label: 'Result', kind: 'status' },
      { key: 'reason', label: 'Reason' }, { key: 'correlationId', label: 'Correlation', kind: 'reference' },
    ],
  },
  alerts: {
    name: 'alerts', title: 'Alerts', description: 'Current and recent operational alerts with direct runbook access.', searchLabel: 'Search rule, summary, region, or severity', defaultSort: 'startedAt', rowKey: guidKey('alertId'),
    columns: [
      { key: 'startedAtUtc', label: 'Started', kind: 'date', sortKey: 'startedAt' }, { key: 'severity', label: 'Severity', kind: 'status', sortKey: 'severity' },
      { key: 'ruleName', label: 'Rule' }, { key: 'region', label: 'Region', sortKey: 'region' }, { key: 'summary', label: 'Summary' },
      { key: 'runbookUrl', label: 'Runbook', kind: 'link' }, { key: 'resolvedAtUtc', label: 'Resolved', kind: 'date' },
    ],
  },
};
