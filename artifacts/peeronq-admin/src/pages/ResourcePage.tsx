import {
  Activity,
  ArrowDown,
  ArrowUp,
  CalendarDays,
  Database,
  Eye,
  Gauge,
  Radio,
  RefreshCw,
  Router,
  Search,
  ShieldOff,
  UploadCloud,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useAuth, type AdminPolicy } from '../auth/AuthProvider';
import { ActionDialog, DialogFrame } from '../components/ActionDialog';
import { ReleasePublishDialog } from '../components/ReleasePublishDialog';
import { StatePanel } from '../components/StatePanel';
import { StatusPill } from '../components/StatusPill';
import { WebsiteUpgradePanel } from '../components/WebsiteUpgradePanel';
import { useOnlineStatus } from '../hooks/useOnlineStatus';
import { beginOnlineLoad } from '../hooks/loadLifecycle';
import { ApiError } from '../services/apiClient';
import type {
  DiagnosticDetailResponse,
  InfrastructureMetric,
  InfrastructureMetricsResponse,
  PagedResponse,
  ResourceName,
  ResourceRow,
  TableValue,
} from '../types/api';
import { resourceConfigs, type ResourceColumn } from './resourceConfig';

const PAGE_SIZE = 25;

type RowAction =
  | { kind: 'device-revoke'; id: string; target: string; label: string; disabled: boolean }
  | { kind: 'installation-access'; id: string; target: string; label: string; blocked: boolean; disabled: false }
  | { kind: 'release-rollout'; id: string; target: string; label: string; disabled: false }
  | { kind: 'diagnostic-detail'; id: string; target: string; label: string; disabled: false };

interface DiagnosticDetailState {
  id: string;
  target: string;
  loading: boolean;
  data: DiagnosticDetailResponse | null;
  error: ApiError | null;
}

function asUtc(date: string, endOfDay = false): string | undefined {
  if (!date) return undefined;
  return new Date(`${date}T${endOfDay ? '23:59:59.999' : '00:00:00.000'}Z`).toISOString();
}

function formatDate(value: TableValue): string {
  if (typeof value !== 'string' || !value) return '—';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return '—';
  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', timeZoneName: 'short',
  }).format(parsed);
}

type CellKind = Exclude<ResourceColumn['kind'], undefined>;
type CellRenderer = (value: TableValue, column: ResourceColumn) => ReactNode;

function displayValue(value: TableValue): string {
  return value == null || value === '' ? '—' : String(value);
}

const cellRenderers: Record<CellKind, CellRenderer> = {
  status: (value) => <StatusPill value={typeof value === 'boolean' ? value : String(value ?? 'Unknown')} />,
  date: (value) => formatDate(value),
  percent: (value) => typeof value === 'number' ? `${value.toFixed(1)}%` : '—',
  number: (value) => typeof value === 'number' ? new Intl.NumberFormat().format(value) : '—',
  reference: (value) => <code>{displayValue(value)}</code>,
  boolean: (value, column) => <StatusPill value={value === true ? (column.trueLabel ?? 'Yes') : (column.falseLabel ?? 'No')} />,
  link: (value) => typeof value === 'string' && /^https:\/\//i.test(value)
    ? <a href={value} target="_blank" rel="noreferrer">Open runbook</a>
    : displayValue(value),
  text: (value) => displayValue(value),
};

function CellValue({ value, column }: { value: TableValue; column: ResourceColumn }) {
  return <>{cellRenderers[column.kind ?? 'text'](value, column)}</>;
}

function canUseResourceActions(resource: ResourceName, can: (policy: AdminPolicy) => boolean): boolean {
  if (resource === 'devices') return can('admin.security');
  if (resource === 'installations') return can('admin.security') || can('admin.operations');
  if (resource === 'releases') return can('admin.release');
  return resource === 'diagnostics' && can('admin.diagnostics');
}

function deviceAction(row: ResourceRow): RowAction {
  const revoked = row.isRevoked === true;
  return { kind: 'device-revoke', id: String(row.deviceId), target: String(row.maskedPublicDeviceId ?? 'selected device'),
    label: revoked ? 'Revoked' : 'Revoke', disabled: revoked };
}

function installationAction(row: ResourceRow): RowAction {
  const blocked = row.isBlocked === true;
  return { kind: 'installation-access', id: String(row.installationId),
    target: `${String(row.appVersion ?? 'Unknown version')} · ${String(row.region ?? 'Unknown region')}`,
    label: blocked ? 'Unblock' : 'Block', blocked, disabled: false };
}

function releaseAction(row: ResourceRow): RowAction {
  return { kind: 'release-rollout', id: String(row.releaseId),
    target: `${String(row.version ?? 'Unknown version')} · ${String(row.architecture ?? 'Unknown architecture')}`,
    label: 'Upload signed rollout', disabled: false };
}

function diagnosticAction(row: ResourceRow): RowAction {
  return { kind: 'diagnostic-detail', id: String(row.diagnosticId),
    target: String(row.errorId ?? row.issueCategory ?? 'Diagnostic metadata'), label: 'Open metadata', disabled: false };
}

const actionFactories: Partial<Record<ResourceName, (row: ResourceRow) => RowAction>> = {
  devices: deviceAction,
  installations: installationAction,
  releases: releaseAction,
  diagnostics: diagnosticAction,
};

function actionForRow(resource: ResourceName, row: ResourceRow): RowAction | null {
  return actionFactories[resource]?.(row) ?? null;
}

function formatBytes(value: number | null): string {
  if (value == null) return '—';
  return new Intl.NumberFormat(undefined, { style: 'unit', unit: 'megabyte', maximumFractionDigits: 2 })
    .format(value / 1_048_576);
}

const infrastructureMetricDefinitions = [
  { key: 'apiLatencyP95Milliseconds', label: 'API latency p95', icon: Gauge },
  { key: 'webSocketConnections', label: 'WebSocket connections', icon: Radio },
  { key: 'turnAllocationsPerSecond', label: 'TURN allocations', icon: Router },
  { key: 'turnBandwidthBytesPerSecond', label: 'TURN throughput', icon: Activity },
  { key: 'databasePoolUsage', label: 'Database pool usage', icon: Database },
  { key: 'queueDepth', label: 'Queue depth', icon: ArrowDown },
] as const;

function formatInfrastructureMetric(metric: InfrastructureMetric | undefined): string {
  if (!metric || metric.state !== 'available' || metric.value == null) return '—';
  if (metric.unit === 'bytes_per_second') return `${(metric.value * 8 / 1_000_000).toFixed(1)} Mb/s`;
  if (metric.unit === 'milliseconds') {
    return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(metric.value)} ms`;
  }
  if (metric.unit === 'allocations_per_second') {
    return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(metric.value)}/s`;
  }
  return new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(metric.value);
}

interface InfrastructureMetricsContentProps {
  data: InfrastructureMetricsResponse | null;
  error: ApiError | null;
  loading: boolean;
  online: boolean;
  onRetry(): void;
}

function MissingInfrastructureMetrics({ error, loading, online, onRetry }: Omit<InfrastructureMetricsContentProps, 'data'>) {
  if (!online) return <StatePanel state="offline" onRetry={onRetry} />;
  if (loading) return <StatePanel state="loading" title="Loading infrastructure metrics" />;
  if (error) return <StatePanel state="error" error={error} onRetry={onRetry} />;
  return null;
}

function InfrastructureWarning({ state, error }: { state: InfrastructureMetricsResponse['state']; error: ApiError | null }) {
  if (state === 'partial') return <div className="global-banner warning" role="status">
    Some infrastructure measurements are unavailable. Available values remain authoritative.
  </div>;
  if (error) return <div className="global-banner warning" role="status">
    The latest metrics refresh failed. Previously loaded values remain visible.
  </div>;
  return null;
}

function InfrastructureMetricCard({ definition, metric }: {
  definition: (typeof infrastructureMetricDefinitions)[number]; metric: InfrastructureMetric | undefined;
}) {
  const Icon = definition.icon;
  const source = metric?.state === 'available' ? `Source unit: ${metric.unit}` : 'Metric unavailable';
  return <article className="metric-card">
    <div className="metric-icon"><Icon size={19} aria-hidden="true" /></div>
    <div><span>{definition.label}</span><strong>{formatInfrastructureMetric(metric)}</strong></div>
    <small>{source}</small>
  </article>;
}

function InfrastructureMetricsBody({ data, error, loading, online, onRetry }: InfrastructureMetricsContentProps) {
  if (!data) return <MissingInfrastructureMetrics error={error} loading={loading} online={online} onRetry={onRetry} />;
  if (data.state === 'unavailable') {
    return <StatePanel state="empty" title="Metrics source unavailable"
      description="Prometheus is not configured or none of the bounded measurements are currently available." />;
  }
  return <>
    <InfrastructureWarning state={data.state} error={error} />
    <div className="metric-grid">
      {infrastructureMetricDefinitions.map((definition) => (
        <InfrastructureMetricCard key={definition.key} definition={definition} metric={data[definition.key]} />
      ))}
    </div>
    <p className="metrics-observed">Observed {formatDate(data.observedAtUtc)}</p>
  </>;
}

export function InfrastructureMetricsContent({ data, error, loading, online, onRetry }: InfrastructureMetricsContentProps) {
  return (
    <section className="infrastructure-metrics" aria-labelledby="live-infrastructure-metrics">
      <div className="section-heading">
        <div>
          <h2 id="live-infrastructure-metrics">Live infrastructure metrics</h2>
          <p>Bounded Prometheus queries proxied and authorized by the Admin API.</p>
        </div>
        {data ? <StatusPill value={data.state} /> : null}
      </div>
      <InfrastructureMetricsBody data={data} error={error} loading={loading} online={online} onRetry={onRetry} />
    </section>
  );
}

function InfrastructureMetricsPanel({ refreshKey }: { refreshKey: number }) {
  const { api } = useAuth();
  const online = useOnlineStatus();
  const [data, setData] = useState<InfrastructureMetricsResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const [retryKey, setRetryKey] = useState(0);

  useEffect(() => {
    if (!online) {
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    void api.infrastructureMetrics(controller.signal)
      .then(setData)
      .catch((cause: unknown) => {
        if (!controller.signal.aborted) {
          setError(cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'Infrastructure metrics could not be loaded.'));
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [api, online, refreshKey, retryKey]);

  return <InfrastructureMetricsContent
    data={data}
    error={error}
    loading={loading}
    online={online}
    onRetry={() => setRetryKey((value) => value + 1)}
  />;
}

type ResourceConfig = (typeof resourceConfigs)[ResourceName];

function ResourceHeader({ config, canPublish, online, loading, onPublish, onRefresh }: {
  config: ResourceConfig; canPublish: boolean; online: boolean; loading: boolean; onPublish(): void; onRefresh(): void;
}) {
  return <header className="page-header">
    <div><span className="eyebrow">Operations data</span><h1>{config.title}</h1><p>{config.description}</p></div>
    <div className="page-header-actions">
      {canPublish ? <button className="button primary" type="button" disabled={!online} onClick={onPublish}>
        <UploadCloud size={17} aria-hidden="true" /> Publish signed release
      </button> : null}
      <button className="button secondary" type="button" disabled={loading || !online} onClick={onRefresh}>
        <RefreshCw size={16} className={loading ? 'spin' : ''} aria-hidden="true" /> Refresh
      </button>
    </div>
  </header>;
}

function ResourceNotices({ notice, staleRefresh, staleData, resource }: {
  notice: string; staleRefresh: boolean; staleData: boolean; resource: ResourceName;
}) {
  const staleMessage = resource === 'presence'
    ? 'All returned presence leases have expired.'
    : 'The newest returned infrastructure snapshot is older than 180 seconds.';
  return <>
    {notice ? <div className="global-banner success" role="status">{notice}</div> : null}
    {staleRefresh ? <div className="global-banner warning" role="status">The latest refresh failed. Previously loaded records remain visible.</div> : null}
    {staleData ? <div className="global-banner warning" role="status">{staleMessage}</div> : null}
  </>;
}

function PublicationConfigurationNotice({ releaseEnabled, websiteEnabled }: {
  releaseEnabled: boolean; websiteEnabled: boolean;
}) {
  if (releaseEnabled && websiteEnabled) return null;
  const unavailable = [!releaseEnabled ? 'Windows release publication' : null,
    !websiteEnabled ? 'website publication' : null].filter(Boolean).join(' and ');
  return <div className="global-banner warning" role="status">
    {unavailable} {releaseEnabled || websiteEnabled ? 'is' : 'are'} not configured in this environment. Read-only release data remains available.
  </div>;
}

function ResourceFeaturePanels({ resource, canManageReleases, releaseEnabled, websiteEnabled, refreshKey, onNotice }: {
  resource: ResourceName; canManageReleases: boolean; releaseEnabled: boolean; websiteEnabled: boolean;
  refreshKey: number; onNotice(message: string): void;
}) {
  return <>
    {resource === 'infrastructure' ? <InfrastructureMetricsPanel refreshKey={refreshKey} /> : null}
    {resource === 'releases' && canManageReleases
      ? <PublicationConfigurationNotice releaseEnabled={releaseEnabled} websiteEnabled={websiteEnabled} />
      : null}
    {resource === 'releases' && canManageReleases && websiteEnabled ? <WebsiteUpgradePanel onNotice={onNotice} /> : null}
  </>;
}

interface ResourceFiltersProps {
  resource: ResourceName; searchLabel: string; search: string; from: string; to: string; clearDisabled: boolean;
  onSearch(value: string): void; onFrom(value: string): void; onTo(value: string): void;
  onSubmit(event: FormEvent): void; onClear(): void;
}

function ResourceFilters({ resource, searchLabel, search, from, to, clearDisabled, onSearch, onFrom, onTo, onSubmit, onClear }: ResourceFiltersProps) {
  return <form className="filter-bar" onSubmit={onSubmit}>
    <div className="search-control"><Search size={17} aria-hidden="true" />
      <label className="sr-only" htmlFor={`${resource}-search`}>{searchLabel}</label>
      <input id={`${resource}-search`} type="search" maxLength={128} value={search}
        onChange={(event) => onSearch(event.target.value)} placeholder={searchLabel} />
    </div>
    <div className="date-control"><CalendarDays size={16} aria-hidden="true" /><label htmlFor={`${resource}-from`}>From</label>
      <input id={`${resource}-from`} type="date" value={from} max={to || undefined} onChange={(event) => onFrom(event.target.value)} />
    </div>
    <div className="date-control"><label htmlFor={`${resource}-to`}>To</label>
      <input id={`${resource}-to`} type="date" value={to} min={from || undefined} onChange={(event) => onTo(event.target.value)} />
    </div>
    <button className="button primary" type="submit">Apply filters</button>
    <button className="button quiet" type="button" disabled={clearDisabled} onClick={onClear}>Clear</button>
  </form>;
}

function ActionIcon({ action }: { action: RowAction }) {
  if (action.kind === 'diagnostic-detail') return <Eye size={15} aria-hidden="true" />;
  if (action.kind === 'device-revoke') return <ShieldOff size={15} aria-hidden="true" />;
  return null;
}

function isDestructiveAction(action: RowAction): boolean {
  if (action.kind === 'device-revoke') return true;
  return action.kind === 'installation-access' && !action.blocked;
}

function RowActionButton({ resource, row, busy, onAction }: {
  resource: ResourceName; row: ResourceRow; busy: boolean; onAction(action: RowAction): void;
}) {
  const action = actionForRow(resource, row);
  if (!action) return <span aria-hidden="true">—</span>;
  const style = isDestructiveAction(action) ? 'danger-outline' : 'secondary';
  return <button className={`button compact ${style}`} type="button" disabled={action.disabled || busy}
    onClick={() => onAction(action)}><ActionIcon action={action} />{action.label}</button>;
}

function SortIcon({ direction }: { direction: 'asc' | 'desc' }) {
  return direction === 'asc' ? <ArrowUp size={14} /> : <ArrowDown size={14} />;
}

function ResourceColumnHeader({ column, sortBy, direction, onSort }: {
  column: ResourceColumn; sortBy: string; direction: 'asc' | 'desc'; onSort(key: string): void;
}) {
  if (!column.sortKey) return <th scope="col">{column.label}</th>;
  const active = sortBy === column.sortKey;
  return <th scope="col" aria-sort={active ? (direction === 'asc' ? 'ascending' : 'descending') : undefined}>
    <button type="button" onClick={() => onSort(column.sortKey!)}>{column.label}{active ? <SortIcon direction={direction} /> : null}</button>
  </th>;
}

function ResourceTable({ resource, config, rows, sortBy, direction, actionsEnabled, actionBusy, onSort, onAction }: {
  resource: ResourceName; config: ResourceConfig; rows: ResourceRow[]; sortBy: string; direction: 'asc' | 'desc';
  actionsEnabled: boolean; actionBusy: boolean; onSort(key: string): void; onAction(action: RowAction): void;
}) {
  return <div className="table-scroll" tabIndex={0} aria-label={`Scrollable ${config.title} table`}>
    <table><caption className="sr-only">{config.title} records</caption>
      <thead><tr>{config.columns.map((column) => <ResourceColumnHeader key={column.key} column={column}
        sortBy={sortBy} direction={direction} onSort={onSort} />)}
        {actionsEnabled ? <th scope="col">Action</th> : null}</tr></thead>
      <tbody>{rows.map((row, index) => <tr key={config.rowKey(row, index)}>
        {config.columns.map((column) => <td key={column.key}><CellValue value={row[column.key]} column={column} /></td>)}
        {actionsEnabled ? <td><RowActionButton resource={resource} row={row} busy={actionBusy} onAction={onAction} /></td> : null}
      </tr>)}</tbody>
    </table>
  </div>;
}

function MissingResourceResults({ online, loading, error, onRetry }: {
  online: boolean; loading: boolean; error: ApiError | null; onRetry(): void;
}) {
  if (!online) return <StatePanel state="offline" onRetry={onRetry} />;
  if (loading) return <StatePanel state="loading" />;
  if (error) return <StatePanel state="error" error={error} onRetry={onRetry} />;
  return null;
}

function ResourceResults({ resource, config, data, online, loading, error, filtersActive, sortBy, direction,
  actionsEnabled, actionBusy, onRetry, onSort, onAction }: {
  resource: ResourceName; config: ResourceConfig; data: PagedResponse<ResourceRow> | null; online: boolean; loading: boolean;
  error: ApiError | null; filtersActive: boolean; sortBy: string; direction: 'asc' | 'desc'; actionsEnabled: boolean;
  actionBusy: boolean; onRetry(): void; onSort(key: string): void; onAction(action: RowAction): void;
}) {
  if (!data) return <MissingResourceResults online={online} loading={loading} error={error} onRetry={onRetry} />;
  if (!data.items.length) return <StatePanel state="empty" description={filtersActive ? 'No records match the active filters.' : undefined} />;
  return <ResourceTable resource={resource} config={config} rows={data.items} sortBy={sortBy} direction={direction}
    actionsEnabled={actionsEnabled} actionBusy={actionBusy} onSort={onSort} onAction={onAction} />;
}

function ResourcePagination({ page, totalPages, loading, itemCount, total, onPage }: {
  page: number; totalPages: number; loading: boolean; itemCount: number; total: number; onPage(page: number): void;
}) {
  return <footer className="pagination">
    <button className="button secondary" type="button" disabled={page <= 1 || loading}
      onClick={() => onPage(Math.max(1, page - 1))}>Previous</button>
    <span>Showing {itemCount} of {new Intl.NumberFormat().format(total)}</span>
    <button className="button secondary" type="button" disabled={page >= totalPages || loading}
      onClick={() => onPage(page + 1)}>Next</button>
  </footer>;
}

function resourceSummary(data: PagedResponse<ResourceRow> | null): { total: number; itemCount: number } {
  return { total: data?.total ?? 0, itemCount: data?.items.length ?? 0 };
}

function ResourceCardHeading({ total, page, totalPages, rangeStart, rangeEnd }: {
  total: number; page: number; totalPages: number; rangeStart: number; rangeEnd: number;
}) {
  return <div className="data-card-heading"><div><strong>{new Intl.NumberFormat().format(total)} records</strong>
    <span>Page {page} of {totalPages}</span></div><span>Showing {rangeStart}–{rangeEnd}</span></div>;
}

function ResourceDataCard({ resource, config, data, online, loading, error, filtersActive, page, totalPages,
  rangeStart, rangeEnd, sortBy, direction, actionsEnabled, actionBusy, onRetry, onSort, onAction, onPage }: {
  resource: ResourceName; config: ResourceConfig; data: PagedResponse<ResourceRow> | null; online: boolean; loading: boolean;
  error: ApiError | null; filtersActive: boolean; page: number; totalPages: number; rangeStart: number; rangeEnd: number;
  sortBy: string; direction: 'asc' | 'desc'; actionsEnabled: boolean; actionBusy: boolean;
  onRetry(): void; onSort(key: string): void; onAction(action: RowAction): void; onPage(page: number): void;
}) {
  const summary = resourceSummary(data);
  return <section className="data-card" aria-label={`${config.title} results`} aria-busy={loading}>
    <ResourceCardHeading total={summary.total} page={page} totalPages={totalPages} rangeStart={rangeStart} rangeEnd={rangeEnd} />
    <ResourceResults resource={resource} config={config} data={data} online={online} loading={loading} error={error}
      filtersActive={filtersActive} sortBy={sortBy} direction={direction} actionsEnabled={actionsEnabled}
      actionBusy={actionBusy} onRetry={onRetry} onSort={onSort} onAction={onAction} />
    <ResourcePagination page={page} totalPages={totalPages} loading={loading}
      itemCount={summary.itemCount} total={summary.total} onPage={onPage} />
  </section>;
}

type PendingAction = Exclude<RowAction, { kind: 'diagnostic-detail' }>;

function installationDialogCopy(action: Extract<PendingAction, { kind: 'installation-access' }>) {
  const verb = action.blocked ? 'Restore' : 'Block';
  return {
    title: `${verb} installation access`,
    description: 'The access decision is enforced by the server and written to the audit trail.',
    confirmLabel: `${verb} access`,
  };
}

function actionDialogCopy(action: PendingAction) {
  if (action.kind === 'device-revoke') return {
    title: 'Revoke device',
    description: 'This permanently revokes the device identity and prevents future authentication.',
    confirmLabel: 'Revoke device',
  };
  if (action.kind === 'installation-access') return installationDialogCopy(action);
  return {
    title: 'Upload signed rollout manifest',
    description: 'Create this manifest with the offline signing tool. The server rejects changed release identity, package details, bad signatures, and unsafe URLs.',
    confirmLabel: 'Verify and update rollout',
  };
}

function actionIsDestructive(action: PendingAction): boolean {
  return action.kind === 'device-revoke' || (action.kind === 'installation-access' && !action.blocked);
}

function RolloutManifestField({ action, manifest, onManifest }: {
  action: PendingAction; manifest: File | null; onManifest(file: File | null): void;
}) {
  if (action.kind !== 'release-rollout') return null;
  const description = manifest
    ? `${manifest.name} (${new Intl.NumberFormat().format(manifest.size)} bytes)`
    : 'Only the rollout and signed envelope may change; maximum 128 KiB.';
  return <label htmlFor="release-rollout-manifest">Newly signed manifest
    <input id="release-rollout-manifest" type="file" accept="application/json,.json" autoFocus
      onChange={(event) => onManifest(event.currentTarget.files?.[0] ?? null)} />
    <small>{description}</small>
  </label>;
}

function PendingActionDialog({ action, reason, manifest, busy, error, valid, onReason, onManifest, onClose, onConfirm }: {
  action: PendingAction | null; reason: string; manifest: File | null; busy: boolean; error: ApiError | null; valid: boolean;
  onReason(value: string): void; onManifest(file: File | null): void; onClose(): void; onConfirm(): void;
}) {
  if (!action) return null;
  const copy = actionDialogCopy(action);
  const rollout = action.kind === 'release-rollout';
  return <ActionDialog title={copy.title} description={copy.description} confirmLabel={copy.confirmLabel}
    destructive={actionIsDestructive(action)} busy={busy} error={error} confirmDisabled={!valid}
    onClose={onClose} onConfirm={onConfirm}>
    <div className="dialog-callout"><strong>Target</strong><span>{action.target}</span></div>
    <RolloutManifestField action={action} manifest={manifest} onManifest={onManifest} />
    <label htmlFor="administrative-action-reason">Audit reason
      <textarea id="administrative-action-reason" minLength={3} maxLength={rollout ? 512 : 256} rows={3}
        value={reason} autoFocus={!rollout} placeholder="Explain why this change is required"
        onChange={(event) => onReason(event.target.value)} />
    </label>
    <small>This reason is stored in the immutable administration audit trail.</small>
  </ActionDialog>;
}

function ReleasePublishMount({ open, onClose, onPublished }: {
  open: boolean; onClose(): void; onPublished(result: { version: string; architecture: string; rolloutPercentage: number }): void;
}) {
  return open ? <ReleasePublishDialog onClose={onClose} onPublished={onPublished} /> : null;
}

function DiagnosticDetailBody({ detail, onRetry }: { detail: DiagnosticDetailState; onRetry(): void }) {
  if (detail.loading) return <StatePanel state="loading" />;
  if (detail.error) return <StatePanel state="error" error={detail.error} onRetry={onRetry} />;
  if (!detail.data) return null;
  return <dl className="detail-grid">
    <div><dt>Status</dt><dd><StatusPill value={detail.data.status} /></dd></div>
    <div><dt>Consent</dt><dd><StatusPill value={detail.data.consentGranted ? 'Granted' : 'Not granted'} /></dd></div>
    <div><dt>App version</dt><dd>{detail.data.appVersion}</dd></div><div><dt>Operating system</dt><dd>{detail.data.osVersion}</dd></div>
    <div><dt>Architecture</dt><dd>{detail.data.architecture}</dd></div><div><dt>Error ID</dt><dd><code>{detail.data.errorId}</code></dd></div>
    <div><dt>Issue category</dt><dd>{detail.data.issueCategory}</dd></div><div><dt>Reference code</dt><dd><code>{detail.data.referenceCode}</code></dd></div>
    <div><dt>Archive size</dt><dd>{formatBytes(detail.data.sanitizedArchiveSizeBytes)}</dd></div>
    <div><dt>Created</dt><dd>{formatDate(detail.data.createdAtUtc)}</dd></div><div><dt>Expires</dt><dd>{formatDate(detail.data.expiresAtUtc)}</dd></div>
  </dl>;
}

function DiagnosticDetailDialog({ detail, onClose, onRetry }: {
  detail: DiagnosticDetailState | null; onClose(): void; onRetry(detail: DiagnosticDetailState): void;
}) {
  if (!detail) return null;
  return <DialogFrame title="Diagnostic metadata"
    description="Opening diagnostic metadata is authorized and recorded by the server. No archive content is exposed here."
    busy={detail.loading} onClose={onClose}>
    <div className="dialog-body diagnostic-detail">
      <div className="dialog-callout"><strong>Reference</strong><span>{detail.target}</span></div>
      <DiagnosticDetailBody detail={detail} onRetry={() => onRetry(detail)} />
    </div>
    <footer className="dialog-actions"><button className="button secondary" type="button"
      disabled={detail.loading} onClick={onClose}>Close</button></footer>
  </DialogFrame>;
}

type ResourceApi = ReturnType<typeof useAuth>['api'];
type ResourceQuery = Parameters<ResourceApi['list']>[1];
type DiagnosticAction = Extract<RowAction, { kind: 'diagnostic-detail' }>;

function unlessAborted(signal: AbortSignal, callback: () => void): void {
  if (!signal.aborted) callback();
}

function resourceLoadError(cause: unknown): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'The data request failed.');
}

async function fetchResourceRows(api: ResourceApi, resource: ResourceName, query: ResourceQuery, signal: AbortSignal,
  onData: (data: PagedResponse<ResourceRow>) => void, onError: (error: ApiError) => void, onDone: () => void): Promise<void> {
  try {
    const data = await api.list(resource, query, signal);
    unlessAborted(signal, () => onData(data));
  } catch (cause) {
    unlessAborted(signal, () => onError(resourceLoadError(cause)));
  } finally {
    unlessAborted(signal, onDone);
  }
}

function useResourceRows(api: ResourceApi, online: boolean, resource: ResourceName, page: number, search: string,
  sortBy: string, direction: 'asc' | 'desc', from: string, to: string, refreshKey: number) {
  const [data, setData] = useState<PagedResponse<ResourceRow> | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    const controller = beginOnlineLoad(online, setLoading, () => setError(null));
    if (!controller) return;
    const query = { offset: (page - 1) * PAGE_SIZE, limit: PAGE_SIZE, search: search || undefined, sortBy,
      descending: direction === 'desc', fromUtc: asUtc(from), toUtc: asUtc(to, true) };
    void fetchResourceRows(api, resource, query, controller.signal, setData, setError, () => setLoading(false));
    return () => controller.abort();
  }, [api, direction, from, online, page, refreshKey, resource, search, sortBy, to]);
  return { data, error, loading };
}

function diagnosticLoadError(cause: unknown): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'Diagnostic metadata could not be opened.');
}

async function fetchDiagnosticDetail(api: ResourceApi, action: DiagnosticAction, signal: AbortSignal,
  onData: (data: DiagnosticDetailResponse) => void, onError: (error: ApiError) => void): Promise<void> {
  try {
    const data = await api.diagnosticDetail(action.id, signal);
    unlessAborted(signal, () => onData(data));
  } catch (cause) {
    unlessAborted(signal, () => onError(diagnosticLoadError(cause)));
  }
}

function useDiagnosticDetail(api: ResourceApi) {
  const [detail, setDetail] = useState<DiagnosticDetailState | null>(null);
  const controller = useRef<AbortController | null>(null);
  useEffect(() => () => controller.current?.abort(), []);
  const open = useCallback((action: DiagnosticAction) => {
    controller.current?.abort();
    controller.current = new AbortController();
    setDetail({ id: action.id, target: action.target, loading: true, data: null, error: null });
    const setData = (data: DiagnosticDetailResponse) => setDetail((current) => current?.id === action.id
      ? { ...current, loading: false, data } : current);
    const setError = (error: ApiError) => setDetail((current) => current?.id === action.id
      ? { ...current, loading: false, error } : current);
    void fetchDiagnosticDetail(api, action, controller.current.signal, setData, setError);
  }, [api]);
  const close = useCallback(() => {
    controller.current?.abort(); controller.current = null; setDetail(null);
  }, []);
  return { diagnosticDetail: detail, openDiagnostic: open, closeDiagnostic: close };
}

function resourceTotalPages(data: PagedResponse<ResourceRow> | null): number {
  return Math.max(1, Math.ceil((data?.total ?? 0) / (data?.limit ?? PAGE_SIZE)));
}

function resourceRange(data: PagedResponse<ResourceRow> | null): { rangeStart: number; rangeEnd: number } {
  if (!data?.items.length) return { rangeStart: 0, rangeEnd: 0 };
  return { rangeStart: data.offset + 1, rangeEnd: data.offset + data.items.length };
}

function staleField(resource: ResourceName): 'leaseExpiresAtUtc' | 'observedAtUtc' | null {
  if (resource === 'presence') return 'leaseExpiresAtUtc';
  if (resource === 'infrastructure') return 'observedAtUtc';
  return null;
}

function resourceIsStale(data: PagedResponse<ResourceRow> | null, resource: ResourceName): boolean {
  const field = staleField(resource);
  if (!data?.items.length || !field) return false;
  const timestamps = data.items.map((row) => typeof row[field] === 'string'
    ? new Date(row[field] as string).getTime() : Number.NaN).filter(Number.isFinite);
  if (!timestamps.length) return false;
  return resource === 'presence' ? timestamps.every((value) => value < Date.now()) : Math.max(...timestamps) < Date.now() - 180_000;
}

function usePageClamp(data: PagedResponse<ResourceRow> | null, page: number, totalPages: number, onPage: (page: number) => void): void {
  useEffect(() => {
    if (data && page > totalPages) onPage(totalPages);
  }, [data, onPage, page, totalPages]);
}

function rolloutManifestValid(action: PendingAction, manifest: File | null): boolean {
  if (action.kind !== 'release-rollout') return true;
  return Boolean(manifest) && manifest!.size <= 128 * 1024 && manifest!.name.toLowerCase().endsWith('.json');
}

function pendingActionValid(action: PendingAction | null, reason: string, manifest: File | null): action is PendingAction {
  return Boolean(action) && reason.trim().length >= 3 && rolloutManifestValid(action!, manifest);
}

async function performPendingAction(api: ResourceApi, action: PendingAction, manifest: File | null, reason: string): Promise<string> {
  if (action.kind === 'device-revoke') {
    await api.revokeDevice(action.id, reason); return `Device ${action.target} was revoked.`;
  }
  if (action.kind === 'installation-access') {
    await api.blockInstallation(action.id, !action.blocked, reason);
    return `Installation access was ${action.blocked ? 'restored' : 'blocked'}.`;
  }
  const result = await api.replaceReleaseManifest(action.id, await manifest!.text(), reason);
  return `Release ${action.target} rollout changed to ${result.rolloutPercentage}% from a verified signed manifest.`;
}

function resourceActionError(cause: unknown): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'The administrative action could not be completed.');
}

function filtersClearDisabled(filtersActive: boolean, search: string, from: string, to: string): boolean {
  return !filtersActive && !search && !from && !to;
}

function canPublishResource(resource: ResourceName, can: (policy: AdminPolicy) => boolean): boolean {
  return resource === 'releases' && can('admin.release');
}

function refreshIsStale(error: ApiError | null, data: PagedResponse<ResourceRow> | null): boolean {
  return Boolean(error && data);
}

function resourceFiltersActive(search: string, from: string, to: string): boolean {
  return Boolean(search || from || to);
}

function useResourceFilterState(defaultSort: string) {
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [fromInput, setFromInput] = useState('');
  const [toInput, setToInput] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [sortBy, setSortBy] = useState(defaultSort);
  const [direction, setDirection] = useState<'asc' | 'desc'>('desc');
  const submit = (event: FormEvent) => {
    event.preventDefault(); setPage(1); setSearch(searchInput.trim()); setFrom(fromInput); setTo(toInput);
  };
  const clear = () => {
    setSearchInput(''); setSearch(''); setFromInput(''); setToInput(''); setFrom(''); setTo(''); setPage(1);
  };
  const changeSort = (key: string) => {
    if (sortBy === key) setDirection((value) => value === 'asc' ? 'desc' : 'asc');
    else { setSortBy(key); setDirection('asc'); }
    setPage(1);
  };
  return { searchInput, setSearchInput, search, fromInput, setFromInput, toInput, setToInput, from, to,
    page, setPage, sortBy, direction, submit, clear, changeSort,
    filtersActive: resourceFiltersActive(search, from, to) };
}

function useResourceActionState() {
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null);
  const [reason, setReason] = useState('');
  const [rolloutManifest, setRolloutManifest] = useState<File | null>(null);
  const [publishOpen, setPublishOpen] = useState(false);
  const [actionError, setActionError] = useState<ApiError | null>(null);
  const [actionBusy, setActionBusy] = useState(false);
  const [notice, setNotice] = useState('');
  return { pendingAction, setPendingAction, reason, setReason, rolloutManifest, setRolloutManifest,
    publishOpen, setPublishOpen, actionError, setActionError, actionBusy, setActionBusy, notice, setNotice };
}

export function ResourcePage({ resource }: { resource: ResourceName }) {
  const config = resourceConfigs[resource];
  const { api, can, session } = useAuth();
  const online = useOnlineStatus();
  const filters = useResourceFilterState(config.defaultSort);
  const actions = useResourceActionState();
  const [refreshKey, setRefreshKey] = useState(0);
  const { data, error, loading } = useResourceRows(api, online, resource, filters.page, filters.search,
    filters.sortBy, filters.direction, filters.from, filters.to, refreshKey);
  const { diagnosticDetail, openDiagnostic, closeDiagnostic } = useDiagnosticDetail(api);
  const totalPages = resourceTotalPages(data);
  const releaseEnabled = Boolean(session?.releasePublicationEnabled);
  const websiteEnabled = Boolean(session?.websitePublicationEnabled);
  const actionsEnabled = canUseResourceActions(resource, can) && (resource !== 'releases' || releaseEnabled);
  const { rangeStart, rangeEnd } = resourceRange(data);
  const isStale = useMemo(() => resourceIsStale(data, resource), [data, resource]);
  usePageClamp(data, filters.page, totalPages, filters.setPage);
  const { searchInput, setSearchInput, fromInput, setFromInput, toInput, setToInput, page, setPage,
    sortBy, direction, submit: submitFilters, clear: clearFilters, changeSort, filtersActive } = filters;
  const { pendingAction, setPendingAction, reason, setReason, rolloutManifest, setRolloutManifest,
    publishOpen, setPublishOpen, actionError, setActionError, actionBusy, setActionBusy, notice, setNotice } = actions;

  const openAction = (action: RowAction) => {
    setNotice('');
    if (action.kind === 'diagnostic-detail') {
      openDiagnostic(action);
      return;
    }

    setPendingAction(action);
    setReason('');
    setRolloutManifest(null);
    setActionError(null);
  };

  const executeAction = async () => {
    if (!pendingActionValid(pendingAction, reason, rolloutManifest) || actionBusy) return;
    setActionBusy(true);
    setActionError(null);
    try {
      setNotice(await performPendingAction(api, pendingAction, rolloutManifest, reason.trim()));
      setPendingAction(null);
      setRefreshKey((value) => value + 1);
    } catch (cause) {
      setActionError(resourceActionError(cause));
    } finally {
      setActionBusy(false);
    }
  };

  const actionValid = pendingActionValid(pendingAction, reason, rolloutManifest);
  const canManageReleases = canPublishResource(resource, can);
  const canPublish = canManageReleases && releaseEnabled;
  const clearDisabled = filtersClearDisabled(filtersActive, searchInput, fromInput, toInput);

  return (
    <div className="page">
      <ResourceHeader config={config} canPublish={canPublish}
        online={online} loading={loading} onPublish={() => { setNotice(''); setPublishOpen(true); }}
        onRefresh={() => setRefreshKey((value) => value + 1)} />
      <ResourceNotices notice={notice} staleRefresh={refreshIsStale(error, data)} staleData={isStale} resource={resource} />
      <ResourceFeaturePanels resource={resource} canManageReleases={canManageReleases}
        releaseEnabled={releaseEnabled} websiteEnabled={websiteEnabled} refreshKey={refreshKey} onNotice={setNotice} />
      {config.supportsFilters !== false ? <ResourceFilters resource={resource} searchLabel={config.searchLabel}
        search={searchInput} from={fromInput} to={toInput} clearDisabled={clearDisabled}
        onSearch={setSearchInput} onFrom={setFromInput} onTo={setToInput} onSubmit={submitFilters} onClear={clearFilters} /> : null}
      <ResourceDataCard resource={resource} config={config} data={data} online={online} loading={loading} error={error}
        filtersActive={filtersActive} page={page} totalPages={totalPages} rangeStart={rangeStart} rangeEnd={rangeEnd}
        sortBy={sortBy} direction={direction} actionsEnabled={actionsEnabled} actionBusy={actionBusy}
        onRetry={() => setRefreshKey((value) => value + 1)} onSort={changeSort} onAction={openAction} onPage={setPage} />

      <PendingActionDialog action={pendingAction} reason={reason} manifest={rolloutManifest} busy={actionBusy}
        error={actionError} valid={actionValid} onReason={setReason} onManifest={setRolloutManifest}
        onClose={() => { if (!actionBusy) setPendingAction(null); }} onConfirm={() => void executeAction()} />
      <ReleasePublishMount open={publishOpen} onClose={() => setPublishOpen(false)}
        onPublished={(result) => {
          setPublishOpen(false);
          setNotice(`Release ${result.version} ${result.architecture} published at ${result.rolloutPercentage}% rollout.`);
          setRefreshKey((value) => value + 1);
        }} />
      <DiagnosticDetailDialog detail={diagnosticDetail} onClose={closeDiagnostic}
        onRetry={(detail) => openAction({ kind: 'diagnostic-detail', id: detail.id, target: detail.target, label: 'Open metadata', disabled: false })} />
    </div>
  );
}
