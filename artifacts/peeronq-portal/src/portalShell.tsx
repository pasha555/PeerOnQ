import { Activity, Building2, FileClock, KeyRound, Laptop, LogOut, Menu, Moon, ShieldCheck, Sun, UserRound, UsersRound, X } from 'lucide-react';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'wouter';
import { api } from './api';
import { useAuth } from './auth';
import type { Organization } from './types';

interface OrganizationValue { organizations: Organization[]; selected: Organization | null; loading: boolean; reload(): Promise<void>; select(id: string): void }
const OrganizationContext = createContext<OrganizationValue | null>(null);
export function useOrganization() { const value = useContext(OrganizationContext); if (!value) throw new Error('Organization context is missing.'); return value; }

export function OrganizationProvider({ children }: { children: ReactNode }) {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [selectedId, setSelectedId] = useState(() => window.localStorage.getItem('peeronq_portal_organization'));
  const [loading, setLoading] = useState(true);
  const reload = async () => { setLoading(true); try { const items = await api<Organization[]>('/organizations/'); setOrganizations(items); if (!items.some((item) => item.id === selectedId)) setSelectedId(items[0]?.id ?? null); } finally { setLoading(false); } };
  useEffect(() => { void reload(); }, []); // account identity is fixed for this provider lifetime
  const select = (id: string) => { setSelectedId(id); window.localStorage.setItem('peeronq_portal_organization', id); };
  const selected = organizations.find((item) => item.id === selectedId) ?? organizations[0] ?? null;
  const value = useMemo(() => ({ organizations, selected, loading, reload, select }), [organizations, selected, loading]);
  return <OrganizationContext.Provider value={value}>{children}</OrganizationContext.Provider>;
}

const links = [
  ['/', 'Profile', UserRound], ['/sessions', 'Sessions', Activity], ['/trusted-devices', 'Trusted devices', KeyRound],
  ['/organizations', 'Organizations', Building2], ['/members', 'Members', UserRound], ['/devices', 'Devices', Laptop], ['/teams', 'Teams', UsersRound],
  ['/invitations', 'Invitations', UsersRound], ['/security', 'Security', ShieldCheck], ['/audit', 'Audit', FileClock], ['/privacy', 'Privacy', ShieldCheck],
] as const;

export function Shell({ children }: { children: ReactNode }) {
  const [location] = useLocation();
  const { profile, logout } = useAuth();
  const { organizations, selected, select } = useOrganization();
  const [menu, setMenu] = useState(false);
  const [dark, setDark] = useState(() => window.localStorage.getItem('peeronq_portal_theme') === 'dark');
  useEffect(() => { document.documentElement.classList.toggle('dark', dark); window.localStorage.setItem('peeronq_portal_theme', dark ? 'dark' : 'light'); }, [dark]);
  useEffect(() => setMenu(false), [location]);
  return <div className="shell">
    <a className="skip" href="#main">Skip to main content</a>
    <header className="topbar">
      <button className="icon mobile" aria-label="Open navigation" onClick={() => setMenu(true)}><Menu /></button>
      <Link href="/" className="brand" aria-label="PeerOnQ Account Portal home"><span className="brand-mark">P</span><span><strong>PeerOnQ</strong><small>Account Portal</small></span></Link>
      <label className="organization-select"><span>Organization</span><select aria-label="Active organization" value={selected?.id ?? ''} onChange={(event) => select(event.target.value)} disabled={!organizations.length}>{organizations.length ? organizations.map((item) => <option key={item.id} value={item.id}>{item.name}</option>) : <option>No organization</option>}</select></label>
      <div className="top-actions"><span className="user"><strong>{profile?.displayName}</strong><small>{profile?.email}</small></span><button className="icon" aria-label={dark ? 'Use light theme' : 'Use dark theme'} onClick={() => setDark((value) => !value)}>{dark ? <Sun /> : <Moon />}</button><button className="icon" aria-label="Sign out" onClick={() => void logout()}><LogOut /></button></div>
    </header>
    {menu ? <button className="scrim" aria-label="Close navigation" onClick={() => setMenu(false)} /> : null}
    <aside className={menu ? 'sidebar open' : 'sidebar'}><button className="icon close" aria-label="Close navigation" onClick={() => setMenu(false)}><X /></button><nav aria-label="Account portal">{links.map(([path, label, Icon]) => <Link key={path} href={path} className={location === path ? 'nav active' : 'nav'}><Icon /><span>{label}</span></Link>)}</nav><p className="local-first">Accounts manage shared cloud metadata. PeerOnQ LAN connections remain accountless.</p></aside>
    <main id="main" tabIndex={-1}>{children}</main>
  </div>;
}
