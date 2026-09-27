import { AlertTriangle, Inbox, LoaderCircle, RefreshCw } from 'lucide-react';
import type { ReactNode } from 'react';

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
