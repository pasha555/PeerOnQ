import { Activity, ArrowUpRight, Building2, Download, FileClock, HelpCircle, KeyRound, Laptop, LayoutDashboard, LogOut, Menu, Moon, Settings2, ShieldCheck, Sun, UserRound, UsersRound, X } from 'lucide-react';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'wouter';
import { api } from './api';
import { useAuth } from './auth';
import { Brand, publicWebsite } from './brand';
import { Notice } from './components';
import type { Organization } from './types';

interface OrganizationValue {
  organizations: Organization[];
  selected: Organization | null;
  loading: boolean;
  error: string | null;
  reload(): Promise<void>;
  select(id: string): void;
}
const OrganizationContext = createContext<OrganizationValue | null>(null);
export function useOrganization() {
  const value = useContext(OrganizationContext);
  if (!value) throw new Error('Organization context is missing.');
  return value;
}

export function OrganizationProvider({ children }: { children: ReactNode }) {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [selectedId, setSelectedId] = useState(() => window.localStorage.getItem('peeronq_portal_organization'));
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const request = useRef(0);
  const reload = useCallback(async () => {
    const current = ++request.current;
    setLoading(true);
    setError(null);
    try {
      const items = await api<Organization[]>('/organizations/');
      if (current !== request.current) return;
      setOrganizations(items);
      setSelectedId((previous) => items.some((item) => item.id === previous) ? previous : items[0]?.id ?? null);
    } catch {
      if (current !== request.current) return;
      setOrganizations([]);
      setError('Your organizations could not be loaded. Try again to restore access.');
    } finally {
      if (current === request.current) setLoading(false);
    }
  }, []);
  useEffect(() => { void reload(); return () => { request.current++; }; }, [reload]);
  const select = useCallback((id: string) => {
    setSelectedId(id);
    window.localStorage.setItem('peeronq_portal_organization', id);
  }, []);
  const selected = organizations.find((item) => item.id === selectedId) ?? null;
  const value = useMemo(() => ({ organizations, selected, loading, error, reload, select }), [organizations, selected, loading, error, reload, select]);
  return <OrganizationContext.Provider value={value}>{children}</OrganizationContext.Provider>;
}

const navigation = [
  { label: 'Workspace', links: [
    ['/', 'Overview', LayoutDashboard], ['/devices', 'Devices', Laptop], ['/remote-sessions', 'Sessions', Activity],
    ['/downloads', 'Downloads', Download], ['/security', 'Security', ShieldCheck],
  ] },
  { label: 'Organization', links: [
    ['/organizations', 'Organizations', Building2], ['/members', 'Members', UserRound], ['/teams', 'Teams', UsersRound],
    ['/invitations', 'Invitations', UsersRound], ['/policy', 'Settings', Settings2], ['/audit', 'Audit history', FileClock],
  ] },
  { label: 'Account', links: [
    ['/account', 'My account', UserRound], ['/sessions', 'Browser sessions', Activity],
    ['/trusted-devices', 'Sign-in trust', KeyRound], ['/privacy', 'Privacy & data', ShieldCheck],
  ] },
] as const;

export function Shell({ children }: { children: ReactNode }) {
  const [location] = useLocation();
  const { profile, logout } = useAuth();
  const { organizations, selected, select, loading, error, reload } = useOrganization();
  const [menu, setMenu] = useState(false);
  const [dark, setDark] = useState(() => window.localStorage.getItem('peeronq_portal_theme') === 'dark');
  const toggle = useRef<HTMLButtonElement>(null);
  const sidebar = useRef<HTMLElement>(null);
  const previousLocation = useRef(location);
  useEffect(() => {
    document.documentElement.classList.toggle('dark', dark);
    window.localStorage.setItem('peeronq_portal_theme', dark ? 'dark' : 'light');
  }, [dark]);
  useEffect(() => {
    setMenu(false);
    if (previousLocation.current !== location) document.getElementById('main')?.focus();
    previousLocation.current = location;
  }, [location]);
  useEffect(() => {
    const viewport = window.matchMedia?.('(max-width: 1100px)');
    if (!viewport) return;
    const closeDesktopDrawer = () => {
      if (viewport.matches) return;
      setMenu(false);
      if (sidebar.current?.contains(document.activeElement)) {
        const destination = sidebar.current.querySelector<HTMLElement>('[aria-current="page"]') ?? document.getElementById('main');
        destination?.focus();
      }
    };
    closeDesktopDrawer();
    viewport.addEventListener('change', closeDesktopDrawer);
    return () => viewport.removeEventListener('change', closeDesktopDrawer);
  }, []);
  useEffect(() => {
    if (!menu) return;
    sidebar.current?.querySelector<HTMLButtonElement>('button')?.focus();
    const keydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { setMenu(false); toggle.current?.focus(); }
      if (event.key !== 'Tab') return;
      const controls = sidebar.current?.querySelectorAll<HTMLElement>('a[href], button');
      const first = controls?.[0];
      const last = controls?.[controls.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
    document.addEventListener('keydown', keydown);
    return () => document.removeEventListener('keydown', keydown);
  }, [menu]);
  const closeMenu = () => { setMenu(false); toggle.current?.focus(); };
  return <div className="shell">
    <a className="skip" href="#main">Skip to main content</a>
    <header className="topbar">
      <button ref={toggle} className="icon mobile" aria-label="Open navigation" aria-expanded={menu} aria-controls="portal-navigation" onClick={() => setMenu(true)}><Menu /></button>
      <Link href="/" className="brand" aria-label="PeerOnQ Portal home"><Brand light /></Link>
      <label className="organization-select"><span>Workspace</span><select aria-label="Active organization" value={selected?.id ?? ''} onChange={(event) => select(event.target.value)} disabled={loading || !organizations.length}>
        {organizations.length ? organizations.map((item) => <option key={item.id} value={item.id}>{item.name}</option>) : <option value="">{loading ? 'Loading organizations…' : error ? 'Organizations unavailable' : 'Personal account'}</option>}
      </select></label>
      <div className="top-actions">
        <Link href="/account" className="user"><strong>{profile?.displayName}</strong><small>{profile?.email}</small></Link>
        <button className="icon" aria-label={dark ? 'Use light theme' : 'Use dark theme'} onClick={() => setDark((value) => !value)}>{dark ? <Sun /> : <Moon />}</button>
        <button className="icon" aria-label="Sign out" onClick={() => void logout()}><LogOut /></button>
      </div>
    </header>
    {menu ? <button className="scrim" tabIndex={-1} aria-label="Close navigation" onClick={closeMenu} /> : null}
    <aside ref={sidebar} id="portal-navigation" className={menu ? 'sidebar open' : 'sidebar'}>
      <button className="icon close" aria-label="Close navigation" onClick={closeMenu}><X /></button>
      <nav aria-label="Account portal">{navigation.map((group) => <div className="nav-group" key={group.label}>
        <p className="nav-heading">{group.label}</p>
        {group.links.map(([path, label, Icon]) => <Link key={path} href={path} className={location === path ? 'nav active' : 'nav'} aria-current={location === path ? 'page' : undefined}><Icon aria-hidden="true" /><span>{label}</span></Link>)}
      </div>)}</nav>
      <div className="sidebar-footer"><Link href="/support" className={location === '/support' ? 'nav active' : 'nav'} aria-current={location === '/support' ? 'page' : undefined}><HelpCircle aria-hidden="true" />Help & support</Link><a href={publicWebsite} className="nav"><ArrowUpRight aria-hidden="true" />Public website</a><p>PeerOnQ is open source under MIT.</p></div>
    </aside>
    <main id="main" tabIndex={-1}>
      {error ? <Notice tone="danger">{error} <button className="link-button" onClick={() => void reload()}>Retry organizations</button></Notice> : null}
      {children}
    </main>
  </div>;
}
