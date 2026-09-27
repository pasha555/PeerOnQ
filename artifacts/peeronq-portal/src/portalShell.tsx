import { Activity, ArrowUpRight, Building2, Download, FileClock, HelpCircle, KeyRound, Laptop, LayoutDashboard, LogOut, Menu, Settings2, ShieldCheck, UserRound, UsersRound, X } from 'lucide-react';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'wouter';
import { api } from './api';
import { useAuth } from './auth';
import { Brand, publicWebsite } from './brand';
import { EmptyState, LoadingState, Notice } from './components';
import { ThemeToggle } from './theme';
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
  const [selectedId, setSelectedId] = useState(() => {
    try { return window.localStorage.getItem('peeronq_portal_organization'); } catch { return null; }
  });
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
    try { window.localStorage.setItem('peeronq_portal_organization', id); } catch { /* Selection remains in memory. */ }
  }, []);
  const selected = organizations.find((item) => item.id === selectedId) ?? null;
  const value = useMemo(() => ({ organizations, selected, loading, error, reload, select }), [organizations, selected, loading, error, reload, select]);
  return <OrganizationContext.Provider value={value}>{children}</OrganizationContext.Provider>;
}

const navigation = [
  { label: 'Workspace', links: [
    ['/', 'Overview', LayoutDashboard], ['/downloads', 'Downloads', Download],
  ] },
  { label: 'Account', links: [
    ['/profile', 'Profile', UserRound], ['/security', 'Security', ShieldCheck], ['/sessions', 'Sign-in sessions', Activity],
    ['/trusted-devices', 'Trusted sign-in devices', KeyRound], ['/privacy', 'Privacy & data', ShieldCheck],
  ] },
  { label: 'Organization', links: [
    ['/organizations', 'Organizations', Building2], ['/devices', 'Managed devices', Laptop], ['/remote-sessions', 'Remote sessions', Activity],
    ['/members', 'Members', UserRound], ['/teams', 'Teams', UsersRound],
    ['/invitations', 'Invitations', UsersRound], ['/policy', 'Policy', Settings2], ['/audit', 'Audit history', FileClock],
  ] },
] as const;

export function OrganizationPage({ children }: { children: ReactNode }) {
  const { selected, loading, error } = useOrganization();
  if (loading) return <LoadingState label="Loading your organizations…" />;
  if (error) return <p>Restore your organization list to view this page.</p>;
  if (!selected) return <EmptyState title="Your shared workspace starts here">Select or create an organization to use this page. <Link href="/organizations">Manage organizations</Link></EmptyState>;
  // Remount forms and their pending feedback at the organization boundary.
  return <div key={selected.id}>{children}</div>;
}

export function Shell({ children }: { children: ReactNode }) {
  const [location] = useLocation();
  const { profile, logout } = useAuth();
  const { organizations, selected, select, loading, error, reload } = useOrganization();
  const [menu, setMenu] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const [logoutError, setLogoutError] = useState<string | null>(null);
  const toggle = useRef<HTMLButtonElement>(null);
  const sidebar = useRef<HTMLElement>(null);
  const returnFocus = useRef<HTMLElement | null>(null);
  const previousLocation = useRef(location);
  useEffect(() => {
    setMenu(false);
    if (previousLocation.current !== location) {
      if (menu) returnFocus.current = document.getElementById('main');
      else document.getElementById('main')?.focus();
    }
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
    if (!menu) { returnFocus.current?.focus(); returnFocus.current = null; return; }
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    sidebar.current?.querySelector<HTMLButtonElement>('button')?.focus();
    const keydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { returnFocus.current = toggle.current; setMenu(false); }
      if (event.key !== 'Tab') return;
      const controls = sidebar.current?.querySelectorAll<HTMLElement>('a[href], button');
      const first = controls?.[0];
      const last = controls?.[controls.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
    document.addEventListener('keydown', keydown);
    return () => { document.removeEventListener('keydown', keydown); document.body.style.overflow = previousOverflow; };
  }, [menu]);
  const closeMenu = () => { returnFocus.current = toggle.current; setMenu(false); };
  const signOut = async () => {
    setSigningOut(true); setLogoutError(null);
    try { await logout(); }
    catch { setLogoutError('Sign out could not be confirmed. Please try again.'); }
    finally { setSigningOut(false); }
  };
  const activePath = location === '/account' ? '/profile' : location;
  return <div className="shell">
    <a className="skip" href="#main" inert={menu}>Skip to main content</a>
    <header className="topbar" inert={menu}>
      <button ref={toggle} className="icon mobile" aria-label="Open navigation" aria-expanded={menu} aria-controls="portal-navigation" onClick={() => setMenu(true)}><Menu /></button>
      <Link href="/" className="brand" aria-label="PeerOnQ Portal home"><Brand light /></Link>
      <label className="organization-select"><span>Workspace</span><select aria-label="Active organization" value={selected?.id ?? ''} onChange={(event) => select(event.target.value)} disabled={loading || !organizations.length}>
        {organizations.length ? organizations.map((item) => <option key={item.id} value={item.id}>{item.name}</option>) : <option value="">{loading ? 'Loading organizations…' : error ? 'Organizations unavailable' : 'Personal account'}</option>}
      </select></label>
      <div className="top-actions">
        <Link href="/profile" className="user"><strong>{profile?.displayName}</strong><small>{profile?.email}</small></Link>
        <ThemeToggle />
        <button className="icon" aria-label={signingOut ? 'Signing out…' : 'Sign out'} disabled={signingOut} onClick={() => void signOut()}><LogOut aria-hidden="true" /></button>
      </div>
    </header>
    {menu ? <button className="scrim" tabIndex={-1} aria-label="Close navigation" onClick={closeMenu} /> : null}
    <aside ref={sidebar} id="portal-navigation" className={menu ? 'sidebar open' : 'sidebar'} role={menu ? 'dialog' : undefined} aria-modal={menu || undefined} aria-label={menu ? 'Portal navigation' : undefined}>
      <button className="icon close" aria-label="Close navigation" onClick={closeMenu}><X /></button>
      <nav aria-label="Account portal">{navigation.map((group) => <div className="nav-group" key={group.label}>
        <p className="nav-heading">{group.label}</p>
        {group.links.filter(([path]) => group.label !== 'Organization' || selected || path === '/organizations').map(([path, label, Icon]) => <Link key={path} href={path} className={activePath === path ? 'nav active' : 'nav'} aria-current={activePath === path ? 'page' : undefined} onClick={() => { if (menu) returnFocus.current = document.getElementById('main'); else if (location === path) document.getElementById('main')?.focus(); setMenu(false); }}><Icon aria-hidden="true" /><span>{label}</span></Link>)}
      </div>)}</nav>
      <div className="sidebar-footer"><Link href="/support" className={location === '/support' ? 'nav active' : 'nav'} aria-current={location === '/support' ? 'page' : undefined}><HelpCircle aria-hidden="true" />Help & support</Link><a href={publicWebsite} className="nav"><ArrowUpRight aria-hidden="true" />Public website</a><p>PeerOnQ is open source under MIT.</p></div>
    </aside>
    <main id="main" tabIndex={-1} inert={menu}>
      {logoutError ? <Notice tone="danger">{logoutError}</Notice> : null}
      {error ? <Notice tone="danger">{error} <button className="link-button" onClick={() => void reload()}>Retry organizations</button></Notice> : null}
      {children}
    </main>
  </div>;
}
