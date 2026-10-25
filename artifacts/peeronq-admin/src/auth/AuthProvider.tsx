import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { ApiError, createAdminApiClient } from '../services/apiClient';
import type { AdminSession } from '../types/api';

type AuthStatus = 'booting' | 'anonymous' | 'mfa_required' | 'authenticated' | 'unavailable';
export type AdminPolicy = 'admin.read' | 'admin.diagnostics' | 'admin.operations' | 'admin.security'
  | 'admin.release' | 'admin.platform-upgrade';

export function canAccessPolicy(session: AdminSession | null, policy: AdminPolicy): boolean {
  if (!session) return false;
  if (policy === 'admin.read') return session.roles.length > 0;
  if (!session.mfaEnabled) return false;
  if (policy === 'admin.platform-upgrade') return session.roles.includes('Owner');
  if (session.roles.includes('Owner')) return true;
  if (policy === 'admin.diagnostics') {
    return session.roles.includes('SecurityAdministrator') || session.roles.includes('SupportAgent');
  }
  if (policy === 'admin.operations') return session.roles.includes('OperationsAdministrator');
  if (policy === 'admin.security') return session.roles.includes('SecurityAdministrator');
  return session.roles.includes('ReleaseManager');
}

interface AuthContextValue {
  status: AuthStatus;
  session: AdminSession | null;
  api: ReturnType<typeof createAdminApiClient>;
  challengeId: string | null;
  error: ApiError | null;
  login(email: string, password: string): Promise<void>;
  verifyMfa(code: string): Promise<void>;
  logout(): Promise<void>;
  retrySession(): Promise<void>;
  cancelMfa(): void;
  can(policy: AdminPolicy): boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('booting');
  const [session, setSession] = useState<AdminSession | null>(null);
  const [challengeId, setChallengeId] = useState<string | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const invalidateSession = useCallback(() => {
    setSession(null);
    setChallengeId(null);
    setError(null);
    setStatus('anonymous');
  }, []);
  const api = useMemo(() => createAdminApiClient(invalidateSession), [invalidateSession]);

  const loadSession = useCallback(async () => {
    const next = await api.session();
    setSession(next);
    setStatus('authenticated');
    setError(null);
  }, [api]);

  const retrySession = useCallback(async () => {
    setStatus('booting');
    setError(null);
    try {
      await api.refresh();
      await loadSession();
    } catch (cause) {
      const apiError = cause instanceof ApiError
        ? cause
        : new ApiError(0, 'unknown_error', 'The administrator session could not be restored.');
      setError(apiError);
      setSession(null);
      setStatus(apiError.status === 401 || apiError.status === 403 ? 'anonymous' : 'unavailable');
    }
  }, [api, loadSession]);

  useEffect(() => {
    void retrySession();
  }, [retrySession]);

  const login = useCallback(
    async (email: string, password: string) => {
      setError(null);
      const result = await api.login(email, password);
      if (result.status === 'mfa_required') {
        setChallengeId(result.mfaChallengeId);
        setStatus('mfa_required');
        return;
      }
      await loadSession();
    },
    [api, loadSession],
  );

  const verifyMfa = useCallback(
    async (code: string) => {
      if (!challengeId) throw new ApiError(409, 'mfa_challenge_missing', 'The MFA challenge expired.');
      setError(null);
      await api.verifyMfa(challengeId, code);
      setChallengeId(null);
      await loadSession();
    },
    [api, challengeId, loadSession],
  );

  const logout = useCallback(async () => {
    try {
      await api.logout();
    } finally {
      setSession(null);
      setChallengeId(null);
      setStatus('anonymous');
    }
  }, [api]);

  const cancelMfa = useCallback(() => {
    api.clearSession();
    setChallengeId(null);
    setStatus('anonymous');
    setError(null);
  }, [api]);

  const can = useCallback(
    (policy: AdminPolicy) => {
      return canAccessPolicy(session, policy);
    },
    [session],
  );

  const value = useMemo<AuthContextValue>(
    () => ({
      status,
      session,
      api,
      challengeId,
      error,
      login,
      verifyMfa,
      logout,
      retrySession,
      cancelMfa,
      can,
    }),
    [status, session, api, challengeId, error, login, verifyMfa, logout, retrySession, cancelMfa, can],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside AuthProvider.');
  return value;
}
