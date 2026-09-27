import { Moon, Sun } from 'lucide-react';
import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';

const ThemeContext = createContext({ dark: false, toggle: () => {} });

export function PortalThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreference] = useState<string | null>(() => {
    try { return window.localStorage.getItem('peeronq_portal_theme'); } catch { return null; }
  });
  const [systemDark, setSystemDark] = useState(() => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false);
  const dark = preference === 'dark' || (preference !== 'light' && systemDark);
  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    const change = () => setSystemDark(media?.matches ?? false);
    media?.addEventListener('change', change);
    return () => media?.removeEventListener('change', change);
  }, []);
  useEffect(() => { document.documentElement.classList.toggle('dark', dark); }, [dark]);
  const toggle = () => {
    const next = dark ? 'light' : 'dark';
    setPreference(next);
    try { window.localStorage.setItem('peeronq_portal_theme', next); } catch { /* Theme still works without persistence. */ }
  };
  return <ThemeContext.Provider value={{ dark, toggle }}>{children}</ThemeContext.Provider>;
}

export function ThemeToggle() {
  const { dark, toggle } = useContext(ThemeContext);
  return <button type="button" className="icon" aria-label={dark ? 'Use light theme' : 'Use dark theme'} onClick={toggle}>{dark ? <Sun aria-hidden="true" /> : <Moon aria-hidden="true" />}</button>;
}
