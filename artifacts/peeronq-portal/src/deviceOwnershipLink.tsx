import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, post } from './api';
import { ConfirmAction, Notice } from './components';
import type { Organization } from './types';

interface OwnershipPreview {
  deviceId: string;
  maskedPublicDeviceId: string;
  displayName: string;
  expiresAtUtc: string;
  previewToken: string;
}

export function DeviceOwnershipLink({ organization, onLinked }: { organization: Organization; onLinked(): void }) {
  const [code, setCode] = useState('');
  const [preview, setPreview] = useState<OwnershipPreview | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [linked, setLinked] = useState(false);
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    if (!preview) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [preview]);
  const canManage = organization.role === 'Owner' || organization.role === 'Administrator';
  if (!canManage) return null;
  const expired = preview ? !Number.isFinite(Date.parse(preview.expiresAtUtc)) || Date.parse(preview.expiresAtUtc) <= now : false;
  const base = `/organizations/${encodeURIComponent(organization.id)}/devices/ownership-links`;
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (!code.trim() || busy) return;
    setBusy(true); setError(null); setLinked(false); setPreview(null);
    try {
      const result = await post<OwnershipPreview>(`${base}/preview`, { code: code.trim() });
      setNow(Date.now()); setPreview(result);
    } catch (cause) { setError(cause instanceof ApiError ? cause.message : 'The device could not be previewed. Please try again.'); }
    finally { setBusy(false); }
  };
  return <section className="panel content-section" aria-label="Link an installed device">
    <h2>Link an installed device</h2>
    <p>In the PeerOnQ app on the device you own, choose Link to organization to generate a short-lived code. Enter that code here. Signing in or knowing a device ID does not prove ownership.</p>
    <p><strong>Destination organization: {organization.name}</strong>. Select the correct workspace before previewing; the preview reserves this code for your account and this organization.</p>
    {error ? <Notice tone="danger">{error}</Notice> : null}
    {linked ? <Notice tone="success">Device linked to {organization.name}.</Notice> : null}
    <form className="form-grid" onSubmit={(event) => void submit(event)} aria-busy={busy}>
      <label>Device ownership code<input name="ownershipCode" autoComplete="off" spellCheck={false} required maxLength={128} value={code} onChange={(event) => { setCode(event.target.value); setPreview(null); setError(null); setLinked(false); }} disabled={busy} /></label>
      <button className="button secondary" disabled={busy || !code.trim()}>{busy ? 'Checking device…' : 'Preview device'}</button>
    </form>
    {preview ? <div className="content-section">
      <h3>{preview.displayName}</h3><p><code>{preview.maskedPublicDeviceId}</code></p>
      {expired ? <Notice tone="danger">This ownership code has expired. Generate a new code in the installed app.</Notice> : <>
        <p>Code expires <time dateTime={preview.expiresAtUtc}>{new Date(preview.expiresAtUtc).toLocaleTimeString()}</time>. Linking assigns this device to {organization.name}; session approval and remote-control permissions remain separate.</p>
        <ConfirmAction label={`Link device to ${organization.name}`} title="Link this device to your organization?" description={`Assign ${preview.displayName} (${preview.maskedPublicDeviceId}) to ${organization.name}. Confirm that you own or administer this device.`} confirmLabel="Confirm device linking" onConfirm={async () => {
          if (Date.parse(preview.expiresAtUtc) <= Date.now()) throw new ApiError(400, 'ownership_link_expired', 'This ownership code has expired. Generate a new code in the installed app.');
          await post(`${base}/claim`, { code: code.trim(), previewToken: preview.previewToken });
          setCode(''); setPreview(null); setLinked(true); onLinked();
        }} />
      </>}
    </div> : null}
  </section>;
}
