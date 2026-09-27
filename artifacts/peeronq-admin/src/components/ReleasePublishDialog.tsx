import { FileCheck2, PackageCheck, ShieldCheck } from 'lucide-react';
import { useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { ApiError } from '../services/apiClient';
import type { ReleasePublicationResponse } from '../types/api';
import { ActionDialog } from './ActionDialog';

const MAX_MANIFEST_BYTES = 128 * 1024;
const MAX_PACKAGE_BYTES = 96 * 1024 * 1024;

interface ReleasePublishDialogProps {
  onClose(): void;
  onPublished(result: ReleasePublicationResponse): void;
}

function formatMiB(bytes: number): string {
  return `${(bytes / 1_048_576).toFixed(bytes >= 1_048_576 ? 1 : 3)} MiB`;
}

function fileValidationError(manifest: File | null, packageFile: File | null): string | null {
  if (manifest && manifest.size > MAX_MANIFEST_BYTES) return `The signed manifest exceeds ${formatMiB(MAX_MANIFEST_BYTES)}.`;
  if (packageFile && packageFile.size > MAX_PACKAGE_BYTES) return `The MSI package exceeds ${formatMiB(MAX_PACKAGE_BYTES)}.`;
  if (manifest && !manifest.name.toLowerCase().endsWith('.json')) return 'Select the signed manifest.json file.';
  if (packageFile && !packageFile.name.toLowerCase().endsWith('.msi')) return 'Select the signed Windows MSI package.';
  return null;
}

function fileDescription(file: File | null, fallback: string): string {
  return file ? `${file.name} (${formatMiB(file.size)})` : fallback;
}

export function ReleasePublishDialog({ onClose, onPublished }: ReleasePublishDialogProps) {
  const { api } = useAuth();
  const [manifest, setManifest] = useState<File | null>(null);
  const [packageFile, setPackageFile] = useState<File | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  const localError = fileValidationError(manifest, packageFile);

  const valid = Boolean(manifest && packageFile && reason.trim().length >= 3 && !localError);

  const publish = async () => {
    if (!manifest || !packageFile || !valid || busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await api.publishRelease(manifest, packageFile, reason.trim());
      onPublished(result);
    } catch (cause) {
      setError(cause instanceof ApiError
        ? cause
        : new ApiError(0, 'unknown_error', 'The signed release could not be published.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <ActionDialog
      title="Publish signed Windows release"
      description="Upload artifacts produced by the offline release-signing workflow. The server verifies the signature, package hash, URL, version, and rollout before publication."
      confirmLabel="Verify and publish"
      busy={busy}
      error={error ?? (localError ? new ApiError(0, 'invalid_file', localError) : null)}
      confirmDisabled={!valid}
      onClose={onClose}
      onConfirm={() => void publish()}
    >
      <ol className="release-publication-steps" aria-label="Secure release publication steps">
        <li><ShieldCheck size={18} aria-hidden="true" /><span><strong>Sign offline</strong>The private update key never enters this panel or server.</span></li>
        <li><FileCheck2 size={18} aria-hidden="true" /><span><strong>Verify exactly</strong>Manifest signature and MSI SHA-256 must match.</span></li>
        <li><PackageCheck size={18} aria-hidden="true" /><span><strong>Publish atomically</strong>Clients only see a fully verified release.</span></li>
      </ol>
      <label htmlFor="release-signed-manifest">
        Signed manifest
        <input
          id="release-signed-manifest"
          type="file"
          accept="application/json,.json"
          required
          onChange={(event) => setManifest(event.currentTarget.files?.[0] ?? null)}
        />
        <small>{fileDescription(manifest, 'manifest.json, maximum 128 KiB')}</small>
      </label>
      <label htmlFor="release-package">
        Authenticode-signed package
        <input
          id="release-package"
          type="file"
          accept="application/x-msi,.msi"
          required
          onChange={(event) => setPackageFile(event.currentTarget.files?.[0] ?? null)}
        />
        <small>{fileDescription(packageFile, 'PeerOnQ-version-architecture.msi, maximum 96 MiB')}</small>
      </label>
      <label htmlFor="release-publication-reason">
        Audit reason
        <textarea
          id="release-publication-reason"
          minLength={3}
          maxLength={512}
          rows={3}
          value={reason}
          placeholder="Explain why this release is being published"
          onChange={(event) => setReason(event.target.value)}
        />
      </label>
      <small>The server records the actor, verified release identity, rollout, and reason in the immutable audit trail.</small>
    </ActionDialog>
  );
}
