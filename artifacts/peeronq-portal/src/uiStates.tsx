import { AlertTriangle, Inbox, LoaderCircle, RefreshCw } from 'lucide-react';
import { useId, useRef, useState, type ReactNode } from 'react';
import { ApiError } from './api';

export function LoadingState({ label = 'Loading secure data…' }: { label?: string }) {
  return <div className="state" role="status"><LoaderCircle className="spin" aria-hidden="true" /><p>{label}</p></div>;
}
export function ErrorState({ message, retry }: { message: string; retry?: () => void }) {
  return <div className="state error" role="alert"><AlertTriangle aria-hidden="true" /><h2>Could not load this page</h2><p>{message}</p>{retry ? <button className="button secondary" onClick={retry}><RefreshCw size={16} />Retry</button> : null}</div>;
}
export function EmptyState({ title, children }: { title: string; children: ReactNode }) {
  return <div className="state"><Inbox aria-hidden="true" /><h2>{title}</h2><p>{children}</p></div>;
}
export function Page({ title, description, actions, children }: { title: string; description: string; actions?: ReactNode; children: ReactNode }) {
  return <section className="page"><header className="page-header"><div><h1>{title}</h1><p>{description}</p></div>{actions}</header>{children}</section>;
}
export function Notice({ children, tone = 'info' }: { children: ReactNode; tone?: 'info' | 'danger' | 'success' }) {
  return <div className={`notice ${tone}`} role={tone === 'danger' ? 'alert' : 'status'}>{children}</div>;
}

export function ConfirmAction({ label, title, description, confirmLabel, onConfirm }: {
  label: string; title: string; description: string; confirmLabel: string; onConfirm(): Promise<void>;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const id = useId();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const confirm = async () => {
    setBusy(true); setError(null);
    try { await onConfirm(); dialog.current?.close(); document.getElementById('main')?.focus(); }
    catch (cause) { setError(cause instanceof ApiError ? cause.message : 'The action could not be completed. Please try again.'); }
    finally { setBusy(false); }
  };
  return <>
    <button ref={trigger} className="button danger-outline" onClick={() => { setError(null); dialog.current?.showModal(); cancel.current?.focus(); }}>{label}</button>
    <dialog ref={dialog} className="confirmation" aria-labelledby={`${id}-title`} aria-describedby={`${id}-description`} onCancel={(event) => { if (busy) event.preventDefault(); }} onClose={() => trigger.current?.focus()}>
      <h2 id={`${id}-title`}>{title}</h2><p id={`${id}-description`}>{description}</p>
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <div className="dialog-actions"><button ref={cancel} className="button secondary" disabled={busy} onClick={() => dialog.current?.close()}>Cancel</button><button className="button danger-outline" disabled={busy} onClick={() => void confirm()}>{busy ? 'Please wait…' : confirmLabel}</button></div>
    </dialog>
  </>;
}
