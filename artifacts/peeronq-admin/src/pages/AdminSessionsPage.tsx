import { KeyRound, RefreshCw, Search, ShieldOff } from 'lucide-react';
import { useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { ActionDialog } from '../components/ActionDialog';
import { StatePanel } from '../components/StatePanel';
import { StatusPill } from '../components/StatusPill';
import { useOnlineStatus } from '../hooks/useOnlineStatus';
import { beginOnlineLoad } from '../hooks/loadLifecycle';
import { ApiError } from '../services/apiClient';
import type { OperatorSessionRow, PagedResponse } from '../types/api';

const PAGE_SIZE = 25;
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function formatDate(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return '—';
  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    timeZoneName: 'short',
  }).format(parsed);
}

function sessionState(row: OperatorSessionRow): string {
  if (row.revokedAtUtc) return 'Revoked';
  if (new Date(row.expiresAtUtc).getTime() <= Date.now()) return 'Expired';
  return 'Active';
}

function MissingSessionResults({ online, loading, error, onRetry }: {
  online: boolean; loading: boolean; error: ApiError | null; onRetry(): void;
}) {
  if (!online) return <StatePanel state="offline" onRetry={onRetry} />;
  if (loading) return <StatePanel state="loading" title="Loading administrator sessions" />;
  if (error) return <StatePanel state="error" error={error} onRetry={onRetry} />;
  return null;
}

function SessionsTable({ rows, onRevoke }: { rows: OperatorSessionRow[]; onRevoke(row: OperatorSessionRow): void }) {
  return <div className="table-scroll" tabIndex={0} aria-label="Scrollable administrator sessions table">
    <table>
      <caption className="sr-only">Administrator refresh sessions and revocation controls</caption>
      <thead><tr><th scope="col">Session</th><th scope="col">Administrator</th><th scope="col">Client</th>
        <th scope="col">Created</th><th scope="col">Expires</th><th scope="col">State</th><th scope="col">Action</th></tr></thead>
      <tbody>{rows.map((row) => {
        const state = sessionState(row);
        return <tr key={row.sessionId}>
          <td><code>{row.sessionId}</code></td><td><code>{row.adminUserId}</code></td><td>{row.userAgentFamily}</td>
          <td>{formatDate(row.createdAtUtc)}</td><td>{formatDate(row.expiresAtUtc)}</td><td><StatusPill value={state} /></td>
          <td><button className="button compact danger-outline" type="button" disabled={state !== 'Active'} onClick={() => onRevoke(row)}>
            <ShieldOff size={15} aria-hidden="true" /> {state === 'Active' ? 'Revoke' : state}
          </button></td>
        </tr>;
      })}</tbody>
    </table>
  </div>;
}

function SessionResults({ online, loading, error, data, filtersActive, onRetry, onRevoke }: {
  online: boolean; loading: boolean; error: ApiError | null; data: PagedResponse<OperatorSessionRow> | null;
  filtersActive: boolean; onRetry(): void; onRevoke(row: OperatorSessionRow): void;
}) {
  if (!data) return <MissingSessionResults online={online} loading={loading} error={error} onRetry={onRetry} />;
  if (!data.items.length) {
    return <StatePanel state="empty" title="No administrator sessions found"
      description={filtersActive ? 'No sessions match the current filters.' : 'No active administrator sessions exist.'} />;
  }
  return <SessionsTable rows={data.items} onRevoke={onRevoke} />;
}

function RevokeSessionDialog({ target, reason, busy, error, onReason, onClose, onConfirm }: {
  target: OperatorSessionRow | null; reason: string; busy: boolean; error: ApiError | null;
  onReason(value: string): void; onClose(): void; onConfirm(): void;
}) {
  if (!target) return null;
  return <ActionDialog title="Revoke administrator session"
    description="This immediately invalidates the session and every access token bound to it."
    confirmLabel="Revoke session" destructive busy={busy} error={error}
    confirmDisabled={reason.trim().length < 3} onClose={onClose} onConfirm={onConfirm}>
    <div className="dialog-callout"><KeyRound size={18} aria-hidden="true" /><span>Session <code>{target.sessionId}</code></span></div>
    <label htmlFor="session-revoke-reason">Audit reason</label>
    <textarea id="session-revoke-reason" value={reason} minLength={3} maxLength={512} required autoFocus
      onChange={(event) => onReason(event.target.value)} />
    <small>Required. Stored in the immutable security audit trail.</small>
  </ActionDialog>;
}

function AdminSessionsHeader({ loading, online, onRefresh }: { loading: boolean; online: boolean; onRefresh(): void }) {
  return <header className="page-header">
    <div><span className="eyebrow">Security control</span><h1>Admin sessions</h1>
      <p>Review operator refresh sessions and immediately revoke access. Revoking your current session may sign you out.</p>
    </div>
    <button className="button secondary" type="button" disabled={loading || !online} onClick={onRefresh}>
      <RefreshCw size={16} className={loading ? 'spin' : ''} aria-hidden="true" /> Refresh
    </button>
  </header>;
}

function SessionNotices({ notice, stale }: { notice: string; stale: boolean }) {
  return <>
    {notice ? <div className="global-banner success" role="status">{notice}</div> : null}
    {stale ? <div className="global-banner warning" role="status">The latest refresh failed. Previously loaded sessions remain visible.</div> : null}
  </>;
}

interface FilterBarProps {
  userId: string; includeRevoked: boolean; error: string; clearDisabled: boolean;
  onUserId(value: string): void; onIncludeRevoked(value: boolean): void; onSubmit(event: FormEvent): void; onClear(): void;
}

function AdminSessionFilters({ userId, includeRevoked, error, clearDisabled, onUserId, onIncludeRevoked, onSubmit, onClear }: FilterBarProps) {
  return <form className="filter-bar admin-session-filters" onSubmit={onSubmit}>
    <div className="search-control"><Search size={17} aria-hidden="true" />
      <label className="sr-only" htmlFor="admin-user-id">Administrator user ID</label>
      <input id="admin-user-id" type="text" value={userId} maxLength={36} aria-invalid={Boolean(error)}
        aria-describedby={error ? 'admin-user-id-error' : undefined} onChange={(event) => onUserId(event.target.value)}
        placeholder="Filter by administrator user ID" />
    </div>
    <label className="checkbox-control"><input type="checkbox" checked={includeRevoked}
      onChange={(event) => onIncludeRevoked(event.target.checked)} /> Include revoked sessions</label>
    <button className="button primary" type="submit">Apply filters</button>
    <button className="button quiet" type="button" disabled={clearDisabled} onClick={onClear}>Clear</button>
    {error ? <p className="filter-error" id="admin-user-id-error" role="alert">{error}</p> : null}
  </form>;
}

function SessionPagination({ page, totalPages, loading, itemCount, total, onPage }: {
  page: number; totalPages: number; loading: boolean; itemCount: number; total: number; onPage(page: number): void;
}) {
  return <footer className="pagination">
    <button className="button secondary" type="button" disabled={loading || page <= 1}
      onClick={() => onPage(Math.max(1, page - 1))}>Previous</button>
    <span>Showing {itemCount} of {new Intl.NumberFormat().format(total)}</span>
    <button className="button secondary" type="button" disabled={loading || page >= totalPages}
      onClick={() => onPage(page + 1)}>Next</button>
  </footer>;
}

function SessionsDataCard({ online, loading, error, data, filtersActive, page, totalPages, rangeStart, rangeEnd, onRetry, onRevoke, onPage }: {
  online: boolean; loading: boolean; error: ApiError | null; data: PagedResponse<OperatorSessionRow> | null;
  filtersActive: boolean; page: number; totalPages: number; rangeStart: number; rangeEnd: number;
  onRetry(): void; onRevoke(row: OperatorSessionRow): void; onPage(page: number): void;
}) {
  const summary = sessionSummary(data);
  return <section className="data-card" aria-label="Administrator session results" aria-busy={loading}>
    <div className="data-card-heading">
      <div><strong>{new Intl.NumberFormat().format(summary.total)} sessions</strong><span>Page {page} of {totalPages}</span></div>
      <span>Showing {rangeStart}–{rangeEnd}</span>
    </div>
    <SessionResults online={online} loading={loading} error={error} data={data} filtersActive={filtersActive}
      onRetry={onRetry} onRevoke={onRevoke} />
    <SessionPagination page={page} totalPages={totalPages} loading={loading}
      itemCount={summary.itemCount} total={summary.total} onPage={onPage} />
  </section>;
}

function sessionSummary(data: PagedResponse<OperatorSessionRow> | null): { total: number; itemCount: number } {
  return { total: data?.total ?? 0, itemCount: data?.items.length ?? 0 };
}

function sessionTotalPages(data: PagedResponse<OperatorSessionRow> | null): number {
  return Math.max(1, Math.ceil(sessionTotal(data) / sessionLimit(data)));
}

function sessionTotal(data: PagedResponse<OperatorSessionRow> | null): number {
  return data?.total ?? 0;
}

function sessionLimit(data: PagedResponse<OperatorSessionRow> | null): number {
  return data?.limit ?? PAGE_SIZE;
}

function sessionRange(data: PagedResponse<OperatorSessionRow> | null): { rangeStart: number; rangeEnd: number } {
  if (!data?.items.length) return { rangeStart: 0, rangeEnd: 0 };
  return { rangeStart: data.offset + 1, rangeEnd: data.offset + data.items.length };
}

function sessionPagination(data: PagedResponse<OperatorSessionRow> | null) {
  return { totalPages: sessionTotalPages(data), ...sessionRange(data) };
}

function usePageClamp(data: PagedResponse<OperatorSessionRow> | null, page: number, totalPages: number, onPage: (page: number) => void): void {
  useEffect(() => {
    if (data && page > totalPages) onPage(totalPages);
  }, [data, onPage, page, totalPages]);
}

function canRevoke(target: OperatorSessionRow | null, reason: string, busy: boolean): target is OperatorSessionRow {
  return Boolean(target) && reason.trim().length >= 3 && !busy;
}

type AdminApi = ReturnType<typeof useAuth>['api'];
type OperatorSessionQuery = Parameters<AdminApi['listOperatorSessions']>[0];

function unlessAborted(signal: AbortSignal, callback: () => void): void {
  if (!signal.aborted) callback();
}

function sessionLoadError(cause: unknown): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'Administrator sessions could not be loaded.');
}

async function fetchOperatorSessions(api: AdminApi, query: OperatorSessionQuery, signal: AbortSignal,
  onData: (data: PagedResponse<OperatorSessionRow>) => void, onError: (error: ApiError) => void, onDone: () => void): Promise<void> {
  try {
    const data = await api.listOperatorSessions(query, signal);
    unlessAborted(signal, () => onData(data));
  } catch (cause) {
    unlessAborted(signal, () => onError(sessionLoadError(cause)));
  } finally {
    unlessAborted(signal, onDone);
  }
}

function useOperatorSessionData(api: AdminApi, online: boolean, page: number, userId: string,
  includeRevoked: boolean, refreshKey: number) {
  const [data, setData] = useState<PagedResponse<OperatorSessionRow> | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    const controller = beginOnlineLoad(online, setLoading, () => setError(null));
    if (!controller) return;
    const query = { offset: (page - 1) * PAGE_SIZE, limit: PAGE_SIZE, userId: userId || undefined, includeRevoked };
    void fetchOperatorSessions(api, query, controller.signal, setData, setError, () => setLoading(false));
    return () => controller.abort();
  }, [api, includeRevoked, online, page, refreshKey, userId]);
  return { data, error, loading };
}

function revokeError(cause: unknown): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'The administrator session could not be revoked.');
}

function useSessionFilters() {
  const [userIdInput, setUserIdInput] = useState('');
  const [includeRevokedInput, setIncludeRevokedInput] = useState(false);
  const [userId, setUserId] = useState('');
  const [includeRevoked, setIncludeRevoked] = useState(false);
  const [filterError, setFilterError] = useState('');
  const [page, setPage] = useState(1);
  const applyFilters = (event: FormEvent) => {
    event.preventDefault();
    const normalized = userIdInput.trim();
    if (normalized && !GUID_PATTERN.test(normalized)) {
      setFilterError('Enter a complete administrator user ID in UUID format.'); return;
    }
    setFilterError(''); setPage(1); setUserId(normalized); setIncludeRevoked(includeRevokedInput);
  };
  const clearFilters = () => {
    setUserIdInput(''); setIncludeRevokedInput(false); setUserId(''); setIncludeRevoked(false); setFilterError(''); setPage(1);
  };
  return { userIdInput, setUserIdInput, includeRevokedInput, setIncludeRevokedInput, userId, includeRevoked,
    filterError, page, setPage, applyFilters, clearFilters, filtersActive: Boolean(userId || includeRevoked) };
}

function sessionFiltersClearDisabled(filtersActive: boolean, userId: string, includeRevoked: boolean): boolean {
  return !filtersActive && !userId && !includeRevoked;
}

function sessionsAreStale(error: ApiError | null, data: PagedResponse<OperatorSessionRow> | null): boolean {
  return Boolean(error && data);
}

export function AdminSessionsPage() {
  const { api } = useAuth();
  const online = useOnlineStatus();
  const filters = useSessionFilters();
  const [refreshKey, setRefreshKey] = useState(0);
  const [revokeTarget, setRevokeTarget] = useState<OperatorSessionRow | null>(null);
  const [reason, setReason] = useState('');
  const [actionError, setActionError] = useState<ApiError | null>(null);
  const [actionBusy, setActionBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const { data, error, loading } = useOperatorSessionData(api, online, filters.page, filters.userId, filters.includeRevoked, refreshKey);

  const { totalPages, rangeStart, rangeEnd } = sessionPagination(data);
  usePageClamp(data, filters.page, totalPages, filters.setPage);

  const openRevoke = (row: OperatorSessionRow) => {
    setRevokeTarget(row);
    setReason('');
    setActionError(null);
    setNotice('');
  };

  const revoke = async () => {
    if (!canRevoke(revokeTarget, reason, actionBusy)) return;
    setActionBusy(true);
    setActionError(null);
    try {
      await api.revokeOperatorSession(revokeTarget.sessionId, reason.trim());
      setRevokeTarget(null);
      setNotice('Administrator session revoked. Any bound access token is now invalid.');
      setRefreshKey((value) => value + 1);
    } catch (cause) {
      setActionError(revokeError(cause));
    } finally {
      setActionBusy(false);
    }
  };

  return (
    <div className="page">
      <AdminSessionsHeader loading={loading} online={online} onRefresh={() => setRefreshKey((value) => value + 1)} />
      <SessionNotices notice={notice} stale={sessionsAreStale(error, data)} />
      <AdminSessionFilters userId={filters.userIdInput} includeRevoked={filters.includeRevokedInput} error={filters.filterError}
        clearDisabled={sessionFiltersClearDisabled(filters.filtersActive, filters.userIdInput, filters.includeRevokedInput)}
        onUserId={filters.setUserIdInput} onIncludeRevoked={filters.setIncludeRevokedInput}
        onSubmit={filters.applyFilters} onClear={filters.clearFilters} />
      <SessionsDataCard online={online} loading={loading} error={error} data={data} filtersActive={filters.filtersActive}
        page={filters.page} totalPages={totalPages} rangeStart={rangeStart} rangeEnd={rangeEnd}
        onRetry={() => setRefreshKey((value) => value + 1)} onRevoke={openRevoke} onPage={filters.setPage} />

      <RevokeSessionDialog target={revokeTarget} reason={reason} busy={actionBusy} error={actionError}
        onReason={setReason} onClose={() => setRevokeTarget(null)} onConfirm={() => void revoke()} />
    </div>
  );
}
