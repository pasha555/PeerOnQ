import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, ApiError, post } from './api';
import type { Profile } from './types';

type Status = 'loading' | 'anonymous' | 'authenticated' | 'unavailable';
interface AuthValue {
  status: Status;
  profile: Profile | null;
  error: string | null;
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

  const load = useCallback(async () => {
    try {
      const next = await api<Profile>('/account/profile');
      setProfile(next); setStatus('authenticated'); setError(null);
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 401) {
        try { await post('/auth/refresh', {}); const next = await api<Profile>('/account/profile'); setProfile(next); setStatus('authenticated'); setError(null); return; }
        catch (refreshCause) { if (!(refreshCause instanceof ApiError) || ![401, 403].includes(refreshCause.status)) { setError('The account service is unavailable.'); setStatus('unavailable'); return; } }
        setProfile(null); setStatus('anonymous'); setError(null); return;
      }
      setProfile(null); setStatus('unavailable'); setError('The account service is unavailable.');
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const login = useCallback(async (email: string, password: string, mfaCode?: string) => {
    setError(null);
    await post('/auth/login', { email, password, mfaCode: mfaCode || null });
    await load();
  }, [load]);
  const register = useCallback(async (email: string, displayName: string, password: string, invitationToken?: string) => {
    setError(null);
    return post<{ emailVerificationRequired: boolean }>('/auth/register', { email, displayName, password, invitationToken: invitationToken || null });
  }, []);
  const logout = useCallback(async () => { try { await post('/auth/logout'); } finally { setProfile(null); setStatus('anonymous'); } }, []);
  const value = useMemo(() => ({ status, profile, error, login, register, logout, reload: load }), [status, profile, error, login, register, logout, load]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside AuthProvider.');
  return value;
}
