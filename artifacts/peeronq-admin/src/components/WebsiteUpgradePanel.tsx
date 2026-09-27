import { ArchiveRestore, FileArchive, Globe2, ShieldCheck, UploadCloud } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { useOnlineStatus } from '../hooks/useOnlineStatus';
import { ApiError } from '../services/apiClient';
import type { WebsiteRelease } from '../types/api';
import { ActionDialog } from './ActionDialog';
import { StatePanel } from './StatePanel';
import { StatusPill } from './StatusPill';

const MAX_MANIFEST_BYTES = 128 * 1024;
const MAX_ARCHIVE_BYTES = 96 * 1024 * 1024;

function formatDate(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? 'Unknown' : new Intl.DateTimeFormat(undefined, {
    year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', timeZoneName: 'short',
  }).format(parsed);
}

function formatMiB(bytes: number): string {
  return `${(bytes / 1_048_576).toFixed(bytes >= 1_048_576 ? 1 : 3)} MiB`;
}

function MissingWebsiteReleases({ online, loading, error, onRetry }: {
  online: boolean; loading: boolean; error: ApiError | null; onRetry(): void;
}) {
  if (!online) return <StatePanel state="offline" onRetry={onRetry} />;
  if (loading) return <StatePanel state="loading" title="Loading website releases" />;
  if (error?.code === 'website_publication_disabled') return <StatePanel state="empty"
    title="Website publication is not configured"
    description="The release list remains available, but signed website upload and activation are disabled in this environment." />;
  if (error) return <StatePanel state="error" error={error} onRetry={onRetry} />;
  return null;
}

function ActiveWebsiteCard({ release }: { release: WebsiteRelease | null }) {
  return <article>
    <div className="website-release-icon"><Globe2 size={20} aria-hidden="true" /></div>
    <div><span>Active website</span><strong>{release?.version ?? 'Base image'}</strong>
      <small>{release ? formatDate(release.publishedAtUtc) : 'No uploaded patch is active.'}</small></div>
    <StatusPill value="Active" />
  </article>;
}

function RollbackWebsiteCard({ release, online, busy, onActivate }: {
  release: WebsiteRelease | null; online: boolean; busy: boolean; onActivate(release: WebsiteRelease): void;
}) {
  return <article>
    <div className="website-release-icon"><ArchiveRestore size={20} aria-hidden="true" /></div>
    <div><span>Rollback candidate</span><strong>{release?.version ?? 'None'}</strong>
      <small>{release ? `${formatMiB(release.archiveSizeBytes)} verified archive` : 'Published patches appear here after the next activation.'}</small></div>
    {release ? <button className="button compact secondary" type="button" disabled={!online || busy}
      onClick={() => onActivate(release)}>Activate</button> : null}
  </article>;
}

function WebsiteReleaseState({ online, loading, error, releases, active, rollback, busy, onRetry, onActivate }: {
  online: boolean; loading: boolean; error: ApiError | null; releases: WebsiteRelease[] | null;
  active: WebsiteRelease | null; rollback: WebsiteRelease | null; busy: boolean;
  onRetry(): void; onActivate(release: WebsiteRelease): void;
}) {
  if (!releases) return <MissingWebsiteReleases online={online} loading={loading} error={error} onRetry={onRetry} />;
  return <div className="website-release-grid">
    <ActiveWebsiteCard release={active} />
    <RollbackWebsiteCard release={rollback} online={online} busy={busy} onActivate={onActivate} />
  </div>;
}

function websiteFileError(manifest: File | null, archive: File | null): string | null {
  if (manifest && (manifest.size > MAX_MANIFEST_BYTES || !manifest.name.toLowerCase().endsWith('.json'))) {
    return 'Select a signed manifest.json no larger than 128 KiB.';
  }
  if (archive && (archive.size > MAX_ARCHIVE_BYTES || !archive.name.toLowerCase().endsWith('.zip'))) {
    return 'Select a signed website ZIP no larger than 96 MiB.';
  }
  return null;
}

function fileDescription(file: File | null, fallback: string): string {
  return file ? `${file.name} (${formatMiB(file.size)})` : fallback;
}

function WebsiteRefreshWarning({ error, releases }: { error: ApiError | null; releases: WebsiteRelease[] | null }) {
  return error && releases ? <div className="global-banner warning" role="status">
    The latest website release refresh failed. Previously loaded values remain visible.
  </div> : null;
}

function PublishWebsiteDialog({ open, onClose, onPublished }: {
  open: boolean; onClose(): void; onPublished(release: WebsiteRelease): void;
}) {
  return open ? <WebsitePatchDialog onClose={onClose} onPublished={onPublished} /> : null;
}

function ActivateWebsiteDialog({ release, reason, busy, error, onReason, onClose, onConfirm }: {
  release: WebsiteRelease | null; reason: string; busy: boolean; error: ApiError | null;
  onReason(value: string): void; onClose(): void; onConfirm(): void;
}) {
  if (!release) return null;
  return <ActionDialog title={`Activate website ${release.version}`}
    description="This atomically switches the public website and retains the current release as the rollback candidate."
    confirmLabel="Activate release" busy={busy} error={error} confirmDisabled={reason.trim().length < 3}
    onClose={onClose} onConfirm={onConfirm}>
    <div className="dialog-callout"><strong>Verified archive</strong>
      <span>{release.archiveSha256.slice(0, 16)}... / {formatMiB(release.archiveSizeBytes)}</span>
    </div>
    <label htmlFor="website-activation-reason">Audit reason
      <textarea id="website-activation-reason" minLength={3} maxLength={512} rows={3} value={reason} autoFocus
        onChange={(event) => onReason(event.target.value)} />
    </label>
  </ActionDialog>;
}

type AdminApi = ReturnType<typeof useAuth>['api'];

function websiteLoadError(cause: unknown): ApiError {
  return cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'Website releases could not be loaded.');
}

async function fetchWebsiteReleases(api: AdminApi, signal: AbortSignal | undefined, onData: (releases: WebsiteRelease[]) => void,
  onError: (error: ApiError) => void, onDone: () => void): Promise<void> {
  try {
    onData(await api.listWebsiteReleases(signal));
  } catch (cause) {
    if (!signal?.aborted) onError(websiteLoadError(cause));
  } finally {
    if (!signal?.aborted) onDone();
  }
}

function useWebsiteReleaseData(api: AdminApi, online: boolean, refreshKey: number) {
  const [releases, setReleases] = useState<WebsiteRelease[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<ApiError | null>(null);
  useEffect(() => {
    if (!online) { setLoading(false); return; }
    const controller = new AbortController();
    setLoading(true); setError(null);
    void fetchWebsiteReleases(api, controller.signal, setReleases, setError, () => setLoading(false));
    return () => controller.abort();
  }, [api, online, refreshKey]);
  return { releases, loading, error };
}

export function WebsiteUpgradePanel({ onNotice }: { onNotice(message: string): void }) {
  const { api } = useAuth();
  const online = useOnlineStatus();
  const [refreshKey, setRefreshKey] = useState(0);
  const { releases, loading, error } = useWebsiteReleaseData(api, online, refreshKey);
  const [publishOpen, setPublishOpen] = useState(false);
  const [activate, setActivate] = useState<WebsiteRelease | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<ApiError | null>(null);

  const active = useMemo(() => releases?.find((release) => release.isActive) ?? null, [releases]);
  const rollback = useMemo(() => releases?.find((release) => release.isRollbackCandidate) ?? null, [releases]);
  const publicationDisabled = error?.code === 'website_publication_disabled';

  const activateRelease = async () => {
    if (!activate || reason.trim().length < 3 || busy) return;
    setBusy(true);
    setActionError(null);
    try {
      await api.activateWebsiteRelease(activate.version, reason.trim());
      setActivate(null);
      setReason('');
      setRefreshKey((value) => value + 1);
      onNotice(`Website ${activate.version} is now active.`);
    } catch (cause) {
      setActionError(cause instanceof ApiError
        ? cause
        : new ApiError(0, 'unknown_error', 'The website release could not be activated.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="website-upgrade-panel" aria-labelledby="website-upgrades-title" aria-busy={loading}>
      <div className="section-heading">
        <div>
          <span className="eyebrow">Public website</span>
          <h2 id="website-upgrades-title">Website upgrades</h2>
          <p>Publish a complete, offline-signed static website ZIP. Executables and server scripts are never accepted.</p>
        </div>
        <button className="button primary" type="button" hidden={publicationDisabled} disabled={!online || busy} onClick={() => { setActionError(null); setPublishOpen(true); }}>
          <UploadCloud size={17} aria-hidden="true" /> Upload signed patch
        </button>
      </div>

      <WebsiteReleaseState online={online} loading={loading} error={error} releases={releases}
        active={active} rollback={rollback} busy={busy}
        onRetry={() => setRefreshKey((value) => value + 1)}
        onActivate={(release) => { setReason(''); setActionError(null); setActivate(release); }} />
      <WebsiteRefreshWarning error={error} releases={releases} />
      <PublishWebsiteDialog open={publishOpen} onClose={() => setPublishOpen(false)}
        onPublished={(release) => {
          setPublishOpen(false); setRefreshKey((value) => value + 1);
          onNotice(`Signed website patch ${release.version} was verified and activated.`);
        }} />
      <ActivateWebsiteDialog release={activate} reason={reason} busy={busy} error={actionError}
        onReason={setReason} onClose={() => { if (!busy) setActivate(null); }}
        onConfirm={() => void activateRelease()} />
    </section>
  );
}

function WebsitePatchDialog({ onClose, onPublished }: { onClose(): void; onPublished(release: WebsiteRelease): void }) {
  const { api } = useAuth();
  const [manifest, setManifest] = useState<File | null>(null);
  const [archive, setArchive] = useState<File | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const localError = websiteFileError(manifest, archive);
  const valid = Boolean(manifest && archive && reason.trim().length >= 3 && !localError);

  const publish = async () => {
    if (!manifest || !archive || !valid || busy) return;
    setBusy(true);
    setError(null);
    try {
      onPublished(await api.publishWebsiteRelease(manifest, archive, reason.trim()));
    } catch (cause) {
      setError(cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'The signed website patch could not be published.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <ActionDialog
      title="Publish signed website patch"
      description="The server verifies the dedicated website signature, ZIP digest, expiry, file types, paths, and expanded size before an atomic activation."
      confirmLabel="Verify and activate"
      busy={busy}
      error={error ?? (localError ? new ApiError(0, 'invalid_file', localError) : null)}
      confirmDisabled={!valid}
      onClose={onClose}
      onConfirm={() => void publish()}
    >
      <ol className="release-publication-steps" aria-label="Secure website publication steps">
        <li><ShieldCheck size={18} aria-hidden="true" /><span><strong>Dedicated trust root</strong>The private website key stays offline.</span></li>
        <li><FileArchive size={18} aria-hidden="true" /><span><strong>Static files only</strong>No shell scripts, links, or executable file modes.</span></li>
        <li><ArchiveRestore size={18} aria-hidden="true" /><span><strong>Atomic rollback</strong>The previous website remains available for activation.</span></li>
      </ol>
      <label htmlFor="website-signed-manifest">Signed manifest
        <input id="website-signed-manifest" type="file" accept="application/json,.json" required onChange={(event) => setManifest(event.currentTarget.files?.[0] ?? null)} />
        <small>{fileDescription(manifest, 'manifest.json, maximum 128 KiB')}</small>
      </label>
      <label htmlFor="website-patch-archive">Website patch archive
        <input id="website-patch-archive" type="file" accept="application/zip,.zip" required onChange={(event) => setArchive(event.currentTarget.files?.[0] ?? null)} />
        <small>{fileDescription(archive, 'PeerOnQ-website-version.zip, maximum 96 MiB')}</small>
      </label>
      <label htmlFor="website-publication-reason">Audit reason
        <textarea id="website-publication-reason" minLength={3} maxLength={512} rows={3} value={reason} placeholder="Explain why this website version is being activated" onChange={(event) => setReason(event.target.value)} />
      </label>
    </ActionDialog>
  );
}
