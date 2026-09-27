import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, ApiError, post } from './api';
import type { AuthCapabilities, Profile } from './types';

type Status = 'loading' | 'anonymous' | 'authenticated' | 'unavailable';
interface AuthValue {
  status: Status;
  profile: Profile | null;
  error: string | null;
  capabilities: AuthCapabilities | null;
  capabilitiesError: string | null;
  reloadCapabilities(): Promise<void>;
  login(email: string, password: string, mfaCode?: string): Promise<void>;
  register(email: string, displayName: string, password: string, invitationToken?: string): Promise<{ emailVerificationRequired: boolean }>;
  logout(): Promise<void>;
  reload(): Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<Status>('loading');
  const [profile, setProfile] = useState<Profile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [capabilities, setCapabilities] = useState<AuthCapabilities | null>(null);
  const [capabilitiesError, setCapabilitiesError] = useState<string | null>(null);
  const reloadCapabilities = useCallback(async () => {
    setCapabilitiesError(null);
    try { setCapabilities(await api<AuthCapabilities>('/auth/capabilities')); }
    catch { setCapabilities(null); setCapabilitiesError('Sign-in options are unavailable. Please try again.'); }
  }, []);
  useEffect(() => { void reloadCapabilities(); }, [reloadCapabilities]);

  const load = useCallback(async () => {
    try {
      const next = await api<Profile>('/account/profile');
      setProfile(next); setStatus('authenticated'); setError(null);
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 401) {
        setProfile(null); setStatus('anonymous'); setError(null); return;
      }
      setProfile(null); setStatus('unavailable'); setError('The account service is unavailable.');
    }
  }, []);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    const expired = () => { setProfile(null); setStatus('anonymous'); setError('Your sign-in session expired. Please sign in again.'); };
    window.addEventListener('peeronq:session-expired', expired);
    return () => window.removeEventListener('peeronq:session-expired', expired);
  }, []);

  const login = useCallback(async (email: string, password: string, mfaCode?: string) => {
    setError(null);
    await post('/auth/login', { email, password, mfaCode: mfaCode || null });
    // Auth links may be opened on an account page. Successful login starts at Overview.
    if (location.pathname !== '/invitations/accept') window.history.replaceState({}, '', '/');
    await load();
  }, [load]);
  const register = useCallback(async (email: string, displayName: string, password: string, invitationToken?: string) => {
    setError(null);
    return post<{ emailVerificationRequired: boolean }>('/auth/register', { email, displayName, password, invitationToken: invitationToken || null });
  }, []);
  const logout = useCallback(async () => { await post('/auth/logout'); setProfile(null); setStatus('anonymous'); }, []);
  const value = useMemo(() => ({ status, profile, error, capabilities, capabilitiesError, reloadCapabilities, login, register, logout, reload: load }), [status, profile, error, capabilities, capabilitiesError, reloadCapabilities, login, register, logout, load]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside AuthProvider.');
  return value;
}
