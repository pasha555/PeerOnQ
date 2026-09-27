import { ArrowUpRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { ThemeToggle } from './theme';

export const publicWebsite = 'https://peeronq.com';
export const sourceRepository = 'https://github.com/pasha555/PeerOnQ';

export function Brand({ light = false }: { light?: boolean }) {
  return <span className="brand-content">
    <img className={light ? undefined : 'brand-default'} src={`/brand/peeronq-lockup${light ? '-light' : ''}.svg`} width="146" height="30" alt="PeerOnQ" />
    {!light ? <img className="brand-on-dark" src="/brand/peeronq-lockup-light.svg" width="146" height="30" alt="PeerOnQ" /> : null}
    <span className="brand-divider" aria-hidden="true" />
    <span className="brand-label">Portal</span>
  </span>;
}

export function AuthFrame({ children }: { children: ReactNode }) {
  return <main className="auth-screen">
    <header className="auth-topbar">
      <a className="brand" href={publicWebsite} aria-label="PeerOnQ public website"><Brand /></a>
      <nav aria-label="Public links"><a href={`${publicWebsite}/#download`}>Download the app</a><ThemeToggle /></nav>
    </header>
    {children}
    <footer className="auth-footer"><a href={publicWebsite}>Back to PeerOnQ</a><a href={sourceRepository}>MIT-licensed source<ArrowUpRight size={14} aria-hidden="true" /></a></footer>
  </main>;
}
