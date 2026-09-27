import {
  AlertTriangle,
  CheckCircle2,
  CircleDot,
  History,
  LoaderCircle,
  RefreshCw,
  RotateCcw,
  ServerCog,
  ShieldCheck,
  UploadCloud,
} from 'lucide-react';
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import { useAuth } from '../auth/AuthProvider';
import { ActionDialog } from '../components/ActionDialog';
import { StatePanel } from '../components/StatePanel';
import { StatusPill } from '../components/StatusPill';
import { useOnlineStatus } from '../hooks/useOnlineStatus';
import { ApiError } from '../services/apiClient';
import type {
  PlatformUpgradeAcceptedV1,
  PlatformUpgradeCheck,
  PlatformUpgradeState,
  PlatformUpgradeStatus,
} from '../types/api';

const POLL_INTERVAL_MILLISECONDS = 2_000;
const ACCEPTED_STATUS_DEADLINE_MILLISECONDS = 2 * 60_000;
const SUPERSEDED_REQUEST_WARNING = 'Another platform operation superseded this tab\'s accepted request. The latest authoritative host status is shown.';
const TRACKING_DEADLINE_WARNING = 'The host did not publish this tab\'s accepted request before the tracking deadline. Refresh and check the authoritative status before retrying.';
const MINIMUM_REASON_LENGTH = 3;
const MAXIMUM_REASON_LENGTH = 512;
const CONTROL_CHARACTER = /[\u0000-\u001f\u007f]/;
const TRANSIENT_STATES = new Set<PlatformUpgradeState>([
  'queued',
  'verifying',
  'preflight',
  'applying',
  'verifying_deployment',
  'rolling_back',
]);

export function isTransientUpgradeState(state: PlatformUpgradeState): boolean {
  return TRANSIENT_STATES.has(state);
}

function asApiError(cause: unknown, fallback: string): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', fallback);
}

function formatDate(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? 'Unknown' : new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    timeZoneName: 'short',
  }).format(parsed);
}

function stateLabel(value: string): string {
  return value.replaceAll('_', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function displayVersion(value: string | null): string {
  return value?.trim() || 'Not available';
}

function progressValue(value: number): number | null {
  if (!Number.isFinite(value)) return null;
  return Math.min(100, Math.max(0, value));
}

function fileMatches(file: File | null, extension: string): boolean {
  return Boolean(file && file.size > 0 && file.name.toLowerCase().endsWith(extension));
}

export function platformStageValidationError(
  bundle: File | null,
  checksum: File | null,
  signature: File | null,
): string | null {
  if (bundle && !fileMatches(bundle, '.run')) return 'Select a non-empty PeerOnQ platform bundle ending in .run.';
  if (checksum && !fileMatches(checksum, '.sha256')) return 'Select the matching non-empty .sha256 checksum file.';
  if (signature && !fileMatches(signature, '.asc')) return 'Select the matching non-empty detached .asc signature.';
  return null;
}

export function platformAuditReasonValidationError(value: string): string | null {
  const trimmed = value.trim();
  if (trimmed.length < MINIMUM_REASON_LENGTH) return 'Enter an audit reason of at least 3 characters.';
  if (trimmed.length > MAXIMUM_REASON_LENGTH) return 'The audit reason cannot exceed 512 characters.';
  if (CONTROL_CHARACTER.test(trimmed)) return 'The audit reason must be one line and cannot contain control characters.';
  return null;
}

type UpgradeApi = ReturnType<typeof useAuth>['api'];
type PendingUpgradeRequest = {
  requestId: string;
  acceptedAtMilliseconds: number;
  deadlineMilliseconds: number;
};

function usePlatformUpgradeStatus(api: UpgradeApi, online: boolean) {
  const [status, setStatus] = useState<PlatformUpgradeStatus | null>(null);
  const statusRef = useRef<PlatformUpgradeStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<ApiError | null>(null);
  const [refreshError, setRefreshError] = useState<ApiError | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [trackingWarning, setTrackingWarning] = useState('');
  const [pendingRequest, setPendingRequest] = useState<PendingUpgradeRequest | null>(null);
  const pendingRequestRef = useRef<PendingUpgradeRequest | null>(null);
  const requestSequenceRef = useRef(0);
  const latestAcceptedSequenceRef = useRef(0);

  const acceptStatus = useCallback((next: PlatformUpgradeStatus, sequence: number) => {
    if (sequence < latestAcceptedSequenceRef.current) return false;
    const currentUpdatedAt = statusRef.current ? Date.parse(statusRef.current.updatedAtUtc) : Number.NaN;
    const nextUpdatedAt = Date.parse(next.updatedAtUtc);
    if (Number.isFinite(currentUpdatedAt) && Number.isFinite(nextUpdatedAt) && nextUpdatedAt < currentUpdatedAt) {
      return false;
    }
    latestAcceptedSequenceRef.current = sequence;
    statusRef.current = next;
    setStatus(next);
    setError(null);
    setRefreshError(null);
    return true;
  }, []);

  const load = useCallback(async (signal: AbortSignal, initial: boolean) => {
    const sequence = ++requestSequenceRef.current;
    try {
      const next = await api.getPlatformUpgradeStatus(signal);
      if (signal.aborted || !acceptStatus(next, sequence)) return;
      const pending = pendingRequestRef.current;
      if (pending) {
        const updatedAtMilliseconds = Date.parse(next.updatedAtUtc);
        const matching = next.operationId === pending.requestId;
        const superseded = !matching
          && Number.isFinite(updatedAtMilliseconds)
          && Number.isFinite(pending.acceptedAtMilliseconds)
          && updatedAtMilliseconds > pending.acceptedAtMilliseconds;
        const expired = Date.now() >= pending.deadlineMilliseconds;
        if (matching || superseded || expired) {
          pendingRequestRef.current = null;
          setPendingRequest(null);
          if (superseded) {
            setTrackingWarning(SUPERSEDED_REQUEST_WARNING);
          } else if (expired) {
            setTrackingWarning(TRACKING_DEADLINE_WARNING);
          }
        }
      }
    } catch (cause) {
      if (signal.aborted || sequence < latestAcceptedSequenceRef.current) return;
      const pending = pendingRequestRef.current;
      if (pending && Date.now() >= pending.deadlineMilliseconds) {
        pendingRequestRef.current = null;
        setPendingRequest(null);
        setTrackingWarning(TRACKING_DEADLINE_WARNING);
      }
      const nextError = asApiError(cause, 'The platform upgrade status could not be loaded.');
      if (statusRef.current) setRefreshError(nextError);
      else setError(nextError);
    } finally {
      if (initial && !signal.aborted) setLoading(false);
    }
  }, [acceptStatus, api]);

  useEffect(() => {
    if (!online) {
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    if (!statusRef.current) setLoading(true);
    void load(controller.signal, true);
    return () => controller.abort();
  }, [load, online, refreshKey]);

  const transient = status ? isTransientUpgradeState(status.state) : false;
  const polling = transient || pendingRequest !== null;
  useEffect(() => {
    if (!online || !polling) return;
    const controller = new AbortController();
    let timer = 0;
    let stopped = false;
    const poll = async () => {
      await load(controller.signal, false);
      if (!stopped) timer = window.setTimeout(() => void poll(), POLL_INTERVAL_MILLISECONDS);
    };
    timer = window.setTimeout(() => void poll(), POLL_INTERVAL_MILLISECONDS);
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [load, online, pendingRequest, polling, status?.operationId]);

  const trackAcceptedRequest = useCallback((accepted: PlatformUpgradeAcceptedV1) => {
    const tracked = {
      requestId: accepted.requestId,
      acceptedAtMilliseconds: Date.parse(accepted.acceptedAtUtc),
      deadlineMilliseconds: Date.now() + ACCEPTED_STATUS_DEADLINE_MILLISECONDS,
    };
    pendingRequestRef.current = tracked;
    setPendingRequest(tracked);
    setTrackingWarning('');
  }, []);

  return {
    status,
    loading,
    error,
    refreshError,
    trackingWarning,
    awaitingAcceptedStatus: pendingRequest !== null,
    trackAcceptedRequest,
    refresh: () => setRefreshKey((value) => value + 1),
  };
}

function VersionCard({ label, version, detail, Icon }: {
  label: string;
  version: string | null;
  detail: string;
  Icon: typeof ServerCog;
}) {
  return <article className="upgrade-version-card">
    <div className="upgrade-card-icon"><Icon size={19} aria-hidden="true" /></div>
    <div><span>{label}</span><strong>{displayVersion(version)}</strong><small>{detail}</small></div>
  </article>;
}

function UpgradeSummary({ status }: { status: PlatformUpgradeStatus }) {
  return <section className="upgrade-summary-grid" aria-label="Platform release versions">
    <VersionCard label="Current version" version={status.currentVersion} detail="Running production platform" Icon={ServerCog} />
    <VersionCard label="Target version" version={status.targetVersion} detail="Verified staged release" Icon={UploadCloud} />
    <VersionCard label="Rollback version" version={status.rollbackVersion} detail="Retained recovery candidate" Icon={History} />
  </section>;
}

function UpgradeProgress({ status }: { status: PlatformUpgradeStatus }) {
  const progress = progressValue(status.progressPercent);
  return <section className="upgrade-panel" aria-labelledby="upgrade-progress-title">
    <div className="section-heading upgrade-section-heading">
      <div><span className="eyebrow">Authoritative state</span><h2 id="upgrade-progress-title">Deployment status</h2></div>
      <StatusPill value={stateLabel(status.state)} />
    </div>
    {progress == null ? null : <div className="upgrade-progress-row">
      <progress aria-label="Platform upgrade progress" max={100} value={progress}>{progress}%</progress>
      <strong>{Math.round(progress)}%</strong>
    </div>}
    <div className="upgrade-status-message" role="status" aria-live="polite">
      <UpgradeStateIcon state={status.state} />
      <span>{status.message}</span>
    </div>
    <dl className="upgrade-metadata">
      <div><dt>Environment</dt><dd>{status.environment}</dd></div>
      <div><dt>Updated</dt><dd>{formatDate(status.updatedAtUtc)}</dd></div>
      <div><dt>Operation</dt><dd><code>{status.operationId ?? 'None'}</code></dd></div>
      <div><dt>Log reference</dt><dd><code>{status.logReference ?? 'None'}</code></dd></div>
    </dl>
  </section>;
}

function UpgradeStateIcon({ state }: { state: PlatformUpgradeState }) {
  if (isTransientUpgradeState(state)) return <LoaderCircle className="spin warning" size={18} aria-hidden="true" />;
  if (state === 'failed') return <AlertTriangle className="danger" size={18} aria-hidden="true" />;
  if (state === 'succeeded') return <CheckCircle2 className="success" size={18} aria-hidden="true" />;
  if (state === 'rolled_back') return <RotateCcw className="success" size={18} aria-hidden="true" />;
  if (state === 'ready') return <ShieldCheck className="success" size={18} aria-hidden="true" />;
  return <CircleDot className="neutral" size={18} aria-hidden="true" />;
}

function CheckRow({ check }: { check: PlatformUpgradeCheck }) {
  return <li>
    <StatusPill value={stateLabel(check.state)} />
    <div><strong>{check.label}</strong><span>{check.message}</span><code>{check.code}</code></div>
  </li>;
}

function UpgradeChecks({ checks }: { checks: PlatformUpgradeCheck[] }) {
  return <section className="upgrade-panel" aria-labelledby="upgrade-checks-title">
    <div className="section-heading upgrade-section-heading">
      <div><span className="eyebrow">Verification</span><h2 id="upgrade-checks-title">Host checks</h2>
        <p>The host verifies every gate before it changes production.</p></div>
    </div>
    {checks.length ? <ul className="upgrade-check-list">{checks.map((check) => <CheckRow key={check.code} check={check} />)}</ul>
      : <div className="upgrade-empty-checks"><ShieldCheck size={22} aria-hidden="true" />
        <span>Stage a signed platform bundle to run static integrity checks.</span></div>}
  </section>;
}

function selectedFile(file: File | null, fallback: string): string {
  return file ? `${file.name} (${new Intl.NumberFormat().format(file.size)} bytes)` : fallback;
}

function StagePanel({ status, online, busy, requestPending, error, onStage }: {
  status: PlatformUpgradeStatus;
  online: boolean;
  busy: boolean;
  requestPending: boolean;
  error: ApiError | null;
  onStage(bundle: File, checksum: File, signature: File, reason: string): Promise<void>;
}) {
  const [bundle, setBundle] = useState<File | null>(null);
  const [checksum, setChecksum] = useState<File | null>(null);
  const [signature, setSignature] = useState<File | null>(null);
  const [reason, setReason] = useState('');
  const [generation, setGeneration] = useState(0);
  const localError = platformStageValidationError(bundle, checksum, signature);
  const reasonError = reason ? platformAuditReasonValidationError(reason) : null;
  const complete = Boolean(bundle && checksum && signature && !reasonError && !localError
    && reason.trim().length >= MINIMUM_REASON_LENGTH);
  const formLocked = requestPending || isTransientUpgradeState(status.state) || status.state === 'ready';
  const stagingUnavailable = formLocked || !status.enabled || !status.currentVersion;
  const stagingHelp = !status.enabled
    ? 'Select the signed release files now. Staging becomes available after the host updater is configured.'
    : !status.currentVersion
      ? 'Select the signed release files now. Staging becomes available after the controller reports the current version.'
      : 'Upload progress is not estimated. The authoritative status appears after the server accepts and verifies the bundle.';

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!bundle || !checksum || !signature || !complete || busy || stagingUnavailable || !online) return;
    try {
      await onStage(bundle, checksum, signature, reason.trim());
    } catch {
      return;
    }
    setBundle(null); setChecksum(null); setSignature(null); setReason('');
    setGeneration((value) => value + 1);
  };

  return <section className="upgrade-panel" aria-labelledby="upgrade-stage-title" aria-busy={busy}>
    <div className="section-heading upgrade-section-heading">
      <div><span className="eyebrow">Signed release input</span><h2 id="upgrade-stage-title">Stage and verify</h2>
        <p>Unsigned development packages are rejected. Use the protected whole-platform release output.</p></div>
    </div>
    <form className="upgrade-stage-form" onSubmit={(event) => void submit(event)}>
      <label htmlFor="platform-upgrade-bundle">Platform bundle (.run)
        <input key={`bundle-${generation}`} id="platform-upgrade-bundle" type="file" accept=".run,application/octet-stream"
          disabled={busy || formLocked} aria-required="true" onChange={(event) => setBundle(event.currentTarget.files?.[0] ?? null)} />
        <small>{selectedFile(bundle, 'Complete PeerOnQ platform deployment bundle')}</small>
      </label>
      <label htmlFor="platform-upgrade-checksum">Checksum (.sha256)
        <input key={`checksum-${generation}`} id="platform-upgrade-checksum" type="file" accept=".sha256,text/plain"
          disabled={busy || formLocked} aria-required="true" onChange={(event) => setChecksum(event.currentTarget.files?.[0] ?? null)} />
        <small>{selectedFile(checksum, 'Checksum produced beside the bundle')}</small>
      </label>
      <label htmlFor="platform-upgrade-signature">Detached signature (.asc)
        <input key={`signature-${generation}`} id="platform-upgrade-signature" type="file" accept=".asc,application/pgp-signature"
          disabled={busy || formLocked} aria-required="true" onChange={(event) => setSignature(event.currentTarget.files?.[0] ?? null)} />
        <small>{selectedFile(signature, 'Offline release signature')}</small>
      </label>
      <label className="upgrade-reason-field" htmlFor="platform-upgrade-stage-reason">Audit reason
        <textarea id="platform-upgrade-stage-reason" minLength={MINIMUM_REASON_LENGTH} maxLength={MAXIMUM_REASON_LENGTH} rows={3} value={reason}
          disabled={busy || formLocked} aria-required="true" placeholder="Explain why this complete platform release is being staged"
          onChange={(event) => setReason(event.target.value)} />
      </label>
      {localError || reasonError || error ? <div className="form-error upgrade-form-error" role="alert">
        {localError ?? reasonError ?? error?.message}{error?.errorId ? ` Error reference: ${error.errorId}.` : ''}
      </div> : null}
      <div className="upgrade-stage-actions">
        <small role="status">{stagingHelp}</small>
        <button className="button primary" type="submit" disabled={!online || busy || stagingUnavailable || !complete}>
          {busy ? <LoaderCircle className="spin" size={17} aria-hidden="true" /> : <UploadCloud size={17} aria-hidden="true" />}
          {busy ? 'Uploading and verifying' : 'Stage signed release'}
        </button>
      </div>
    </form>
  </section>;
}

type UpgradeAction = 'apply' | 'rollback';

function UpgradeConfirmationDialog({ action, status, confirmation, reason, busy, error, onConfirmation, onReason, onClose, onConfirm }: {
  action: UpgradeAction | null;
  status: PlatformUpgradeStatus;
  confirmation: string;
  reason: string;
  busy: boolean;
  error: ApiError | null;
  onConfirmation(value: string): void;
  onReason(value: string): void;
  onClose(): void;
  onConfirm(): void;
}) {
  if (!action) return null;
  const rollback = action === 'rollback';
  const target = rollback ? status.rollbackVersion : status.targetVersion;
  if (!target || !status.currentVersion) return null;
  const reasonError = reason ? platformAuditReasonValidationError(reason) : null;
  const valid = confirmation === target && reason.trim().length >= MINIMUM_REASON_LENGTH && !reasonError;
  const verb = rollback ? 'Rollback' : 'Apply';
  return <ActionDialog
    title={`${verb} platform ${target}`}
    description={`${verb} the complete PeerOnQ platform in ${status.environment}. The server rechecks the current version before starting.`}
    confirmLabel={rollback ? 'Rollback platform' : 'Apply to production'}
    destructive={rollback}
    busy={busy}
    error={error}
    confirmDisabled={!valid}
    onClose={onClose}
    onConfirm={onConfirm}
  >
    <div className="dialog-callout"><strong>Expected current</strong><span>{status.currentVersion}</span></div>
    <label htmlFor="upgrade-version-confirmation">Type {target} to confirm
      <input id="upgrade-version-confirmation" value={confirmation} autoFocus autoComplete="off" spellCheck={false}
        onChange={(event) => onConfirmation(event.target.value)} />
    </label>
    <label htmlFor="upgrade-action-reason">Audit reason
      <textarea id="upgrade-action-reason" minLength={MINIMUM_REASON_LENGTH} maxLength={MAXIMUM_REASON_LENGTH} rows={3} value={reason}
        placeholder={`Explain why this platform ${action} is required`} onChange={(event) => onReason(event.target.value)} />
    </label>
    {reasonError ? <div className="form-error" role="alert">{reasonError}</div> : null}
  </ActionDialog>;
}

function PlatformActions({ status, owner, online, busy, onApply, onRollback }: {
  status: PlatformUpgradeStatus;
  owner: boolean;
  online: boolean;
  busy: boolean;
  onApply(): void;
  onRollback(): void;
}) {
  return <section className="upgrade-panel upgrade-actions-panel" aria-labelledby="upgrade-actions-title">
    <div><span className="eyebrow">Production control</span><h2 id="upgrade-actions-title">Apply or recover</h2>
      <p>Only an MFA-verified Owner can change the running platform. Release Managers may stage and inspect a release.</p></div>
    {owner ? <div className="upgrade-action-buttons">
      <button className="button primary" type="button" disabled={!online || busy || !status.enabled || !status.currentVersion || !status.canApply || !status.targetVersion}
        onClick={onApply}><CheckCircle2 size={17} aria-hidden="true" /> Apply to production</button>
      <button className="button danger-outline" type="button" disabled={!online || busy || !status.enabled || !status.currentVersion || !status.canRollback || !status.rollbackVersion}
        onClick={onRollback}><RotateCcw size={17} aria-hidden="true" /> Rollback platform</button>
    </div> : <StatusPill value="Owner approval required" />}
  </section>;
}

function AuthorizedUpgradePage() {
  const { api, can } = useAuth();
  const online = useOnlineStatus();
  const { status, loading, error, refreshError, trackingWarning, awaitingAcceptedStatus, trackAcceptedRequest, refresh } = usePlatformUpgradeStatus(api, online);
  const [stageBusy, setStageBusy] = useState(false);
  const [stageError, setStageError] = useState<ApiError | null>(null);
  const [notice, setNotice] = useState('');
  const [action, setAction] = useState<UpgradeAction | null>(null);
  const [confirmation, setConfirmation] = useState('');
  const [reason, setReason] = useState('');
  const [actionBusy, setActionBusy] = useState(false);
  const [actionError, setActionError] = useState<ApiError | null>(null);
  const owner = can('admin.platform-upgrade');

  const openAction = (next: UpgradeAction) => {
    setAction(next); setConfirmation(''); setReason(''); setActionError(null); setNotice('');
  };

  const stage = async (bundle: File, checksum: File, signature: File, auditReason: string) => {
    if (stageBusy) return;
    setStageBusy(true); setStageError(null); setNotice('');
    try {
      const accepted = await api.stagePlatformUpgrade(bundle, checksum, signature, auditReason);
      trackAcceptedRequest(accepted);
      setNotice(`Platform ${accepted.targetVersion} stage request ${accepted.requestId} was accepted. Waiting for authoritative controller status.`);
    } catch (cause) {
      setStageError(asApiError(cause, 'The signed platform release could not be staged.'));
      throw cause;
    } finally {
      setStageBusy(false);
    }
  };

  const confirmAction = async () => {
    if (!status || !action || actionBusy || !owner) return;
    const target = action === 'apply' ? status.targetVersion : status.rollbackVersion;
    if (!target || !status.currentVersion || confirmation !== target || platformAuditReasonValidationError(reason)) return;
    setActionBusy(true); setActionError(null); setNotice('');
    try {
      const accepted = action === 'apply'
        ? await api.applyPlatformUpgrade(target, status.currentVersion, reason.trim())
        : await api.rollbackPlatformUpgrade(target, status.currentVersion, reason.trim());
      trackAcceptedRequest(accepted);
      setNotice(action === 'apply'
        ? `Platform ${target} apply request ${accepted.requestId} was accepted. Waiting for authoritative controller status.`
        : `Rollback to platform ${target} request ${accepted.requestId} was accepted. Waiting for authoritative controller status.`);
      setAction(null); setConfirmation(''); setReason('');
    } catch (cause) {
      setActionError(asApiError(cause, `The platform ${action} request could not be accepted.`));
    } finally {
      setActionBusy(false);
    }
  };

  if (!online && !status) return <div className="page"><StatePanel state="offline" onRetry={refresh} /></div>;
  if (loading && !status) return <div className="page"><StatePanel state="loading" title="Loading platform upgrade state" /></div>;
  if (error && !status) return <div className="page"><StatePanel state="error" error={error} onRetry={refresh} /></div>;
  if (!status) return <div className="page"><StatePanel state="empty" title="Platform upgrade state unavailable" /></div>;

  return <div className="page">
    <header className="page-header">
      <div><span className="eyebrow">Release management</span><h1>Platform upgrade</h1>
        <p>Stage, verify, apply, and roll back one signed release for the complete PeerOnQ platform.</p></div>
      <div className="page-header-actions">
        <StatusPill value={status.environment} />
        <button className="button secondary" type="button" disabled={!online || loading} onClick={refresh}>
          <RefreshCw size={16} aria-hidden="true" /> Refresh
        </button>
      </div>
    </header>

    {notice ? <div className="global-banner success" role="status">{notice}</div> : null}
    {!status.enabled ? <div className="global-banner warning" role="status">
      Platform upgrade is not configured in this environment. Status remains read-only.
    </div> : null}
    {refreshError ? <div className="global-banner warning upgrade-refresh-warning" role="status">
      <span>The controller is temporarily unavailable. The last authoritative status remains visible.</span>
      <button className="button compact secondary" type="button" disabled={!online} onClick={refresh}>Retry now</button>
    </div> : null}
    {trackingWarning ? <div className="global-banner warning" role="status">{trackingWarning}</div> : null}
    {status.blockingReason ? <div className="global-banner warning" role="status">{status.blockingReason}</div> : null}

    <UpgradeSummary status={status} />
    <div className="upgrade-workspace">
      <UpgradeProgress status={status} />
      <UpgradeChecks checks={status.checks} />
    </div>
    <StagePanel status={status} online={online} busy={stageBusy} requestPending={awaitingAcceptedStatus} error={stageError}
      onStage={stage} />
    <PlatformActions status={status} owner={owner} online={online} busy={stageBusy || actionBusy || awaitingAcceptedStatus}
      onApply={() => openAction('apply')} onRollback={() => openAction('rollback')} />
    <UpgradeConfirmationDialog action={action} status={status} confirmation={confirmation} reason={reason}
      busy={actionBusy} error={actionError} onConfirmation={setConfirmation} onReason={setReason}
      onClose={() => { if (!actionBusy) setAction(null); }} onConfirm={() => void confirmAction()} />
  </div>;
}

export function UpgradePage() {
  const { can } = useAuth();
  if (!can('admin.release')) return <div className="page"><StatePanel state="error"
    error={new ApiError(403, 'admin_forbidden', 'An MFA-verified release role is required to view platform upgrades.')} /></div>;
  return <AuthorizedUpgradePage />;
}
