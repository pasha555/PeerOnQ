import {
  Activity,
  Boxes,
  Bug,
  CircleCheckBig,
  CircleGauge,
  Download,
  Gauge,
  HardDriveDownload,
  Network,
  RefreshCw,
  Router,
  Server,
  ShieldAlert,
  Signal,
  Users,
  type LucideIcon,
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { StatePanel } from '../components/StatePanel';
import { useOnlineStatus } from '../hooks/useOnlineStatus';
import { beginOnlineLoad } from '../hooks/loadLifecycle';
import { ApiError } from '../services/apiClient';
import type { DistributionBucket, OverviewResponse, VersionDistributionsResponse } from '../types/api';

type NumericOverviewKey =
  | 'totalDownloads'
  | 'completedDownloads'
  | 'uniqueDownloadEstimate'
  | 'totalInstallations'
  | 'activeInstallations'
  | 'onlineDevices'
  | 'activeDevicesToday'
  | 'activeDevicesThisMonth'
  | 'activeSessions'
  | 'failedConnectionAttempts'
  | 'turnSessions'
  | 'crashRate'
  | 'sessionSuccessRate'
  | 'updateFailures';

interface MetricDefinition {
  key: NumericOverviewKey;
  label: string;
  icon: LucideIcon;
  format?: 'number' | 'percent';
  note: string;
}

const metrics: MetricDefinition[] = [
  { key: 'onlineDevices', label: 'Online now', icon: Signal, note: 'Live signaling and presence' },
  { key: 'activeSessions', label: 'Open sessions', icon: Activity, note: 'Awaiting, negotiating, or connected' },
  { key: 'activeDevicesToday', label: 'Active devices today', icon: Users, note: 'Distinct devices' },
  { key: 'activeDevicesThisMonth', label: 'Active this month', icon: CircleGauge, note: 'Distinct devices' },
  { key: 'totalDownloads', label: 'Download starts', icon: Download, note: 'Recorded full or range attempts' },
  { key: 'completedDownloads', label: 'Completed downloads', icon: CircleCheckBig, note: 'Finished full-file responses' },
  { key: 'uniqueDownloadEstimate', label: 'Unique estimate', icon: Network, note: 'Privacy-limited estimate' },
  { key: 'totalInstallations', label: 'Total installations', icon: HardDriveDownload, note: 'All historical registrations' },
  { key: 'activeInstallations', label: 'Active installations', icon: Server, note: 'Seen within 30 days' },
  { key: 'sessionSuccessRate', label: 'Session success', icon: CircleCheckBig, format: 'percent', note: 'Completed sessions today' },
  { key: 'turnSessions', label: 'TURN sessions', icon: Router, note: 'Relayed sessions today' },
  { key: 'failedConnectionAttempts', label: 'Failed connections', icon: ShieldAlert, note: 'Failures today' },
  { key: 'crashRate', label: 'Client crash rate', icon: Bug, format: 'percent', note: 'Active installations today' },
  { key: 'updateFailures', label: 'Update failures', icon: Gauge, note: 'Failures today' },
];

function number(value: number): string {
  return new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value);
}

function formatDate(value: string): string {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return 'Unknown';
  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', timeZoneName: 'short',
  }).format(parsed);
}

function MetricCard({ definition, value }: { definition: MetricDefinition; value: number | null }) {
  const Icon = definition.icon;
  const unavailable = value == null;
  return (
    <article className="metric-card">
      <div className="metric-icon"><Icon size={19} aria-hidden="true" /></div>
      <div><span>{definition.label}</span><strong>{unavailable ? '—' : `${number(value)}${definition.format === 'percent' ? '%' : ''}`}</strong></div>
      <small>{unavailable ? 'No observations in the measurement window' : definition.note}</small>
    </article>
  );
}

function Distribution({ title, description, items }: { title: string; description: string; items: DistributionBucket[] }) {
  return (
    <section className="distribution-card">
      <div className="section-heading"><div><h2>{title}</h2><p>{description}</p></div></div>
      {!items.length ? <StatePanel state="empty" title="No distribution available" description="No active installation records exist for this period." /> : (
        <div className="bar-list">
          {items.map((item) => (
            <div className="bar-row" key={item.label}>
              <div><strong>{item.label}</strong><span>{number(item.count)} installations · {item.percentage.toFixed(1)}%</span></div>
              <div className="bar-track" role="img" aria-label={`${item.label}: ${item.percentage.toFixed(1)} percent`}><span style={{ width: `${Math.max(0, Math.min(100, item.percentage))}%` }} /></div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function asApiError(cause: unknown, message: string): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', message);
}

function applyOverviewResult(
  result: PromiseSettledResult<OverviewResponse>,
  setData: (value: OverviewResponse) => void,
  setError: (value: ApiError) => void,
) {
  if (result.status === 'fulfilled') setData(result.value);
  else setError(asApiError(result.reason, 'Overview data could not be loaded.'));
}

function applyDistributionResult(
  result: PromiseSettledResult<VersionDistributionsResponse>,
  setData: (value: VersionDistributionsResponse) => void,
  setError: (value: ApiError) => void,
) {
  if (result.status === 'fulfilled') setData(result.value);
  else setError(asApiError(result.reason, 'Version distributions could not be loaded.'));
}

function MissingOverviewPanel({ online, loading, error, onRetry }: {
  online: boolean; loading: boolean; error: ApiError | null; onRetry(): void;
}) {
  if (!online) return <div className="page"><StatePanel state="offline" onRetry={onRetry} /></div>;
  if (loading) return <div className="page"><StatePanel state="loading" title="Loading operational overview" /></div>;
  if (error) return <div className="page"><StatePanel state="error" error={error} onRetry={onRetry} /></div>;
  return null;
}

function optionalText(visible: boolean, value: string): string {
  return visible ? value : '';
}

function OverviewWarning({ stale, error, generatedAtUtc }: { stale: boolean; error: ApiError | null; generatedAtUtc: string }) {
  if (!stale && !error) return null;
  return <div className="global-banner warning" role="status">
    {optionalText(stale, 'Overview data is older than 90 seconds. ')}
    {optionalText(Boolean(error), 'The latest overview refresh failed; previously loaded values remain visible. ')}
    Generated {formatDate(generatedAtUtc)}.
  </div>;
}

function DistributionSection({ distributions, error, onRetry }: {
  distributions: VersionDistributionsResponse | null;
  error: ApiError | null;
  onRetry(): void;
}) {
  if (!distributions) return error
    ? <StatePanel state="error" error={error} onRetry={onRetry} title="Version distributions unavailable" />
    : null;
  return <>
    <DistributionWarning error={error} />
    <div className="distribution-grid">
      <Distribution title="PeerOnQ versions"
        description={`${number(distributions.activeInstallations)} active installations since ${formatDate(distributions.activeSinceUtc)}`}
        items={distributions.clientVersions} />
      <Distribution title="Windows versions"
        description={`${number(distributions.activeWindowsInstallations)} active Windows installations`}
        items={distributions.windowsVersions} />
    </div>
  </>;
}

function DistributionWarning({ error }: { error: ApiError | null }) {
  return error ? <div className="global-banner warning" role="status">
    The latest distribution refresh failed. Previously loaded values remain visible.
  </div> : null;
}

function OverviewRefreshButton({ loading, online, onRefresh }: { loading: boolean; online: boolean; onRefresh(): void }) {
  return <button className="button secondary" type="button" disabled={loading || !online} onClick={onRefresh}>
    <RefreshCw size={16} className={loading ? 'spin' : ''} aria-hidden="true" /> Refresh
  </button>;
}

function GeneratedAt({ overview, distributions }: { overview: string; distributions: string | null }) {
  return <p className="metrics-observed">Overview generated {formatDate(overview)}
    {distributions ? ` · distributions generated ${formatDate(distributions)}` : ''}
  </p>;
}

function stableVersion(value: string | null): string {
  return value ?? 'Not published';
}

function distributionTimestamp(value: VersionDistributionsResponse | null): string | null {
  return value?.generatedAtUtc ?? null;
}

function isOverviewStale(data: OverviewResponse | null): boolean {
  return data ? Date.now() - new Date(data.generatedAtUtc).getTime() > 90_000 : false;
}

function useVisibleRefresh(onRefresh: () => void): void {
  useEffect(() => {
    const interval = window.setInterval(() => {
      if (!document.hidden && navigator.onLine) onRefresh();
    }, 30_000);
    return () => window.clearInterval(interval);
  }, [onRefresh]);
}

type AdminApi = ReturnType<typeof useAuth>['api'];

async function fetchOverviewData(api: AdminApi, signal: AbortSignal,
  onOverview: (value: OverviewResponse) => void, onOverviewError: (error: ApiError) => void,
  onDistributions: (value: VersionDistributionsResponse) => void, onDistributionError: (error: ApiError) => void,
  onDone: () => void): Promise<void> {
  const [overviewResult, distributionsResult] = await Promise.allSettled([
    api.overview(signal), api.overviewDistributions(signal),
  ]);
  if (signal.aborted) return;
  applyOverviewResult(overviewResult, onOverview, onOverviewError);
  applyDistributionResult(distributionsResult, onDistributions, onDistributionError);
  onDone();
}

function useOverviewData(api: AdminApi, online: boolean, refreshKey: number) {
  const [data, setData] = useState<OverviewResponse | null>(null);
  const [distributions, setDistributions] = useState<VersionDistributionsResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [distributionError, setDistributionError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    const controller = beginOnlineLoad(online, setLoading, () => setError(null));
    if (!controller) return;
    setDistributionError(null);
    void fetchOverviewData(api, controller.signal, setData, setError, setDistributions, setDistributionError, () => setLoading(false));
    return () => controller.abort();
  }, [api, online, refreshKey]);
  return { data, distributions, error, distributionError, loading };
}

export function OverviewPage() {
  const { api } = useAuth();
  const online = useOnlineStatus();
  const [refreshKey, setRefreshKey] = useState(0);
  const { data, distributions, error, distributionError, loading } = useOverviewData(api, online, refreshKey);

  const retry = useCallback(() => setRefreshKey((value) => value + 1), []);
  useVisibleRefresh(retry);
  const stale = useMemo(() => isOverviewStale(data), [data]);

  if (!data) return <MissingOverviewPanel online={online} loading={loading} error={error} onRetry={retry} />;

  return (
    <div className="page">
      <header className="page-header overview-heading">
        <div><span className="eyebrow">Live control plane</span><h1>Operational overview</h1><p>Authoritative fleet, acquisition, and reliability measurements. Values refresh every 30 seconds while visible.</p></div>
        <div className="overview-actions">
          <div className="stable-release"><span>Stable release</span><strong>{stableVersion(data.currentStableVersion)}</strong></div>
          <OverviewRefreshButton loading={loading} online={online} onRefresh={retry} />
        </div>
      </header>

      <OverviewWarning stale={stale} error={error} generatedAtUtc={data.generatedAtUtc} />

      <section aria-labelledby="key-metrics">
        <div className="section-heading"><div><h2 id="key-metrics">Key measurements</h2><p>Downloads, completions, unique estimates, and installations are separate values.</p></div></div>
        <div className="metric-grid">{metrics.map((definition) => <MetricCard key={definition.key} definition={definition} value={data[definition.key]} />)}</div>
      </section>

      <DistributionSection distributions={distributions} error={distributionError} onRetry={retry} />

      <GeneratedAt overview={data.generatedAtUtc} distributions={distributionTimestamp(distributions)} />
    </div>
  );
}
