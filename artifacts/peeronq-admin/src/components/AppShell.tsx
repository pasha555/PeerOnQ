import {
  Activity,
  BellRing,
  Boxes,
  Bug,
  ChevronLeft,
  ChevronRight,
  CircleGauge,
  CloudCog,
  Download,
  FileClock,
  HardDriveDownload,
  History,
  KeyRound,
  LogOut,
  Menu,
  MonitorSmartphone,
  Moon,
  Radio,
  ServerCog,
  Sun,
  UploadCloud,
  UsersRound,
  X,
  type LucideIcon,
} from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
import { Link, useLocation } from 'wouter';
import { useAuth, type AdminPolicy } from '../auth/AuthProvider';
import { useOnlineStatus } from '../hooks/useOnlineStatus';
import { Brand } from './Brand';

interface NavItem {
  path: string;
  label: string;
  icon: LucideIcon;
  policy: AdminPolicy;
}

const navigation: NavItem[] = [
  { path: '/', label: 'Overview', icon: CircleGauge, policy: 'admin.read' },
  { path: '/devices', label: 'Devices', icon: MonitorSmartphone, policy: 'admin.read' },
  { path: '/installations', label: 'Installations', icon: HardDriveDownload, policy: 'admin.read' },
  { path: '/presence', label: 'Presence', icon: Radio, policy: 'admin.read' },
  { path: '/sessions', label: 'Sessions', icon: Activity, policy: 'admin.read' },
  { path: '/downloads', label: 'Downloads', icon: Download, policy: 'admin.read' },
  { path: '/releases', label: 'Releases', icon: Boxes, policy: 'admin.read' },
  { path: '/upgrade', label: 'Upgrade', icon: UploadCloud, policy: 'admin.release' },
  { path: '/diagnostics', label: 'Diagnostics', icon: Bug, policy: 'admin.diagnostics' },
  { path: '/infrastructure', label: 'Infrastructure', icon: ServerCog, policy: 'admin.operations' },
  { path: '/audit', label: 'Audit', icon: FileClock, policy: 'admin.security' },
  { path: '/admin-sessions', label: 'Admin sessions', icon: KeyRound, policy: 'admin.security' },
  { path: '/alerts', label: 'Alerts', icon: BellRing, policy: 'admin.operations' },
];

function initialTheme(): 'light' | 'dark' {
  const stored = window.localStorage.getItem('peeronq_admin_theme');
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

type Theme = 'light' | 'dark';

function MobileMenuButton({ open, onToggle }: { open: boolean; onToggle(): void }) {
  return <button className="icon-button mobile-menu" type="button"
    aria-label={open ? 'Close navigation' : 'Open navigation'} aria-expanded={open} onClick={onToggle}>
    {open ? <X size={20} /> : <Menu size={20} />}
  </button>;
}

function ConnectionStatus({ online }: { online: boolean }) {
  return <span className={`connection-state ${online ? 'online' : 'offline'}`} role="status">
    <span aria-hidden="true" />{online ? 'Browser online' : 'Browser offline'}
  </span>;
}

function ThemeButton({ theme, onToggle }: { theme: Theme; onToggle(): void }) {
  const nextTheme = theme === 'dark' ? 'light' : 'dark';
  return <button className="icon-button" type="button" aria-label={`Use ${nextTheme} theme`} onClick={onToggle}>
    {theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
  </button>;
}

function Topbar({ current, mobileOpen, online, role, theme, loggingOut, onMobileToggle, onThemeToggle, onLogout }: {
  current: string; mobileOpen: boolean; online: boolean; role: string; theme: Theme; loggingOut: boolean;
  onMobileToggle(): void; onThemeToggle(): void; onLogout(): void;
}) {
  return <header className="topbar">
    <MobileMenuButton open={mobileOpen} onToggle={onMobileToggle} />
    <Brand />
    <div className="topbar-title"><CloudCog size={17} aria-hidden="true" /> {current}</div>
    <div className="topbar-actions">
      <ConnectionStatus online={online} />
      <ThemeButton theme={theme} onToggle={onThemeToggle} />
      <div className="user-summary"><strong>Administrator</strong><small>{role}</small></div>
      <button className="icon-button" type="button" aria-label="Sign out" disabled={loggingOut} onClick={onLogout}>
        <LogOut size={18} />
      </button>
    </div>
  </header>;
}

function AdminNavLink({ item, location, collapsed }: { item: NavItem; location: string; collapsed: boolean }) {
  const active = location === item.path;
  const Icon = item.icon;
  return <Link href={item.path} className={`nav-link ${active ? 'active' : ''}`}
    aria-current={active ? 'page' : undefined} title={collapsed ? item.label : undefined}>
    <Icon size={19} aria-hidden="true" /><span>{item.label}</span>
  </Link>;
}

function AdminSidebar({ items, location, collapsed, mobileOpen, onCollapse }: {
  items: NavItem[]; location: string; collapsed: boolean; mobileOpen: boolean; onCollapse(): void;
}) {
  return <aside className={`sidebar ${mobileOpen ? 'mobile-open' : ''}`}>
    <nav aria-label="Administration">
      {items.map((item) => <AdminNavLink key={item.path} item={item} location={location} collapsed={collapsed} />)}
    </nav>
    <div className="sidebar-footer">
      <div className="authority-note"><UsersRound size={17} aria-hidden="true" />
        <span>Visibility follows assigned roles. The API authorizes every request.</span>
      </div>
      <button className="collapse-button" type="button" onClick={onCollapse}
        aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
        {collapsed ? <ChevronRight size={18} /> : <ChevronLeft size={18} />}<span>Collapse</span>
      </button>
    </div>
  </aside>;
}

function OfflineBanner({ online }: { online: boolean }) {
  if (online) return null;
  return <div className="global-banner warning" role="status">
    This browser is offline. Displayed values may be stale; administrative requests are paused.
  </div>;
}

function shellClass(collapsed: boolean): string {
  return collapsed ? 'app-shell sidebar-collapsed' : 'app-shell';
}

function NavigationScrim({ open, onClose }: { open: boolean; onClose(): void }) {
  return open ? <button className="nav-scrim" type="button" aria-label="Close navigation" onClick={onClose} /> : null;
}

function nextTheme(theme: Theme): Theme {
  return theme === 'dark' ? 'light' : 'dark';
}

export function AppShell({ children }: { children: ReactNode }) {
  const [location] = useLocation();
  const { session, logout, can } = useAuth();
  const online = useOnlineStatus();
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [theme, setTheme] = useState<'light' | 'dark'>(initialTheme);
  const [loggingOut, setLoggingOut] = useState(false);
  const items = navigation.filter((item) => can(item.policy));
  const current = navigation.find((item) => item.path === location)?.label ?? 'PeerOnQ Operations';

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark');
    window.localStorage.setItem('peeronq_admin_theme', theme);
  }, [theme]);

  useEffect(() => setMobileOpen(false), [location]);

  const handleLogout = async () => {
    if (loggingOut) return;
    setLoggingOut(true);
    try {
      await logout();
    } finally {
      setLoggingOut(false);
    }
  };

  return (
    <div className={shellClass(collapsed)}>
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <Topbar current={current} mobileOpen={mobileOpen} online={online}
        role={session?.roles[0] ?? 'No assigned role'} theme={theme} loggingOut={loggingOut}
        onMobileToggle={() => setMobileOpen((value) => !value)}
        onThemeToggle={() => setTheme(nextTheme)}
        onLogout={() => void handleLogout()} />

      <NavigationScrim open={mobileOpen} onClose={() => setMobileOpen(false)} />
      <AdminSidebar items={items} location={location} collapsed={collapsed} mobileOpen={mobileOpen}
        onCollapse={() => setCollapsed((value) => !value)} />

      <main id="main-content" className="main-content" tabIndex={-1}>
        <OfflineBanner online={online} />
        {children}
      </main>
    </div>
  );
}
