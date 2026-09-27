import { AlertTriangle, Ban, CloudOff, Inbox, LoaderCircle, RefreshCw, ShieldX } from 'lucide-react';
import { ApiError } from '../services/apiClient';

interface Props {
  state: 'loading' | 'empty' | 'offline' | 'error';
  error?: ApiError | null;
  onRetry?: () => void;
  title?: string;
  description?: string;
}

function RetryButton({ onRetry }: { onRetry?: () => void }) {
  if (!onRetry) return null;
  return <button className="button secondary" type="button" onClick={onRetry}>
    <RefreshCw size={16} aria-hidden="true" /> Retry
  </button>;
}

function LoadingPanel({ title, description }: Pick<Props, 'title' | 'description'>) {
  return <section className="state-panel" aria-live="polite" aria-busy="true">
    <LoaderCircle className="spin" size={28} aria-hidden="true" />
    <h2>{title ?? 'Loading authoritative data'}</h2>
    <p>{description ?? 'PeerOnQ is requesting the latest operational state.'}</p>
  </section>;
}

function EmptyPanel({ title, description }: Pick<Props, 'title' | 'description'>) {
  return <section className="state-panel">
    <Inbox size={30} aria-hidden="true" />
    <h2>{title ?? 'No records found'}</h2>
    <p>{description ?? 'No records match the current filters.'}</p>
  </section>;
}

function OfflinePanel({ title, description, onRetry }: Pick<Props, 'title' | 'description' | 'onRetry'>) {
  return <section className="state-panel" role="status">
    <CloudOff size={30} aria-hidden="true" />
    <h2>{title ?? 'This browser is offline'}</h2>
    <p>{description ?? 'Reconnect to the network before requesting administrative data.'}</p>
    <RetryButton onRetry={onRetry} />
  </section>;
}

function errorPresentation(error?: ApiError | null) {
  if (error?.status === 403) return { Icon: ShieldX, title: 'Permission denied', canRetry: false };
  if (error?.status === 409) return { Icon: Ban, title: 'The resource changed', canRetry: true };
  if (error?.status === 429) return { Icon: AlertTriangle, title: 'Request rate limited', canRetry: true };
  return { Icon: AlertTriangle, title: 'Operational data unavailable', canRetry: true };
}

function errorDescription(description: string | undefined, error: ApiError | null | undefined, retryText: string | undefined): string {
  return description ?? error?.message ?? retryText ?? 'The service returned an unexpected response.';
}

function ErrorReference({ error }: { error?: ApiError | null }) {
  return error?.errorId ? <p className="error-reference">Error reference: {error.errorId}</p> : null;
}

function ErrorRetry({ canRetry, onRetry }: { canRetry: boolean; onRetry?: () => void }) {
  return canRetry ? <RetryButton onRetry={onRetry} /> : null;
}

function ErrorPanel({ error, onRetry, title, description }: Omit<Props, 'state'>) {
  const presentation = errorPresentation(error);
  const retryText = error?.status === 429 && error.retryAfterSeconds
    ? `Try again after ${error.retryAfterSeconds} seconds.`
    : undefined;
  return <section className="state-panel error" role="alert">
    <presentation.Icon size={30} aria-hidden="true" />
    <h2>{title ?? presentation.title}</h2>
    <p>{errorDescription(description, error, retryText)}</p>
    <ErrorReference error={error} />
    <ErrorRetry canRetry={presentation.canRetry} onRetry={onRetry} />
  </section>;
}

export function StatePanel(props: Props) {
  if (props.state === 'loading') return <LoadingPanel {...props} />;
  if (props.state === 'empty') return <EmptyPanel {...props} />;
  if (props.state === 'offline') return <OfflinePanel {...props} />;
  return <ErrorPanel {...props} />;
}
