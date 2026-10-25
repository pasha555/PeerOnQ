import { KeyRound, LoaderCircle, LockKeyhole, ShieldCheck } from 'lucide-react';
import { useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Brand } from '../components/Brand';
import { StatePanel } from '../components/StatePanel';
import { ApiError } from '../services/apiClient';

function friendlyAuthError(error: ApiError): string {
  const messages: Record<number, string> = {
    401: 'The credentials or verification code could not be verified.',
    409: 'This verification challenge is no longer current. Sign in again.',
    423: 'This administrator account is temporarily locked. Contact the security owner.',
  };
  if (error.status === 429) {
    return error.retryAfterSeconds
      ? `Too many attempts. Try again after ${error.retryAfterSeconds} seconds.`
      : 'Too many attempts. Wait before trying again.';
  }
  return messages[error.status] ?? error.message;
}

interface CredentialsFormProps {
  email: string;
  password: string;
  submitting: boolean;
  error: ApiError | null;
  onEmail(value: string): void;
  onPassword(value: string): void;
  onSubmit(event: FormEvent): void;
}

function FormError({ error }: { error: ApiError | null }) {
  return error ? <p className="form-error" role="alert">{friendlyAuthError(error)}</p> : null;
}

function SubmitIcon({ busy, mfa = false }: { busy: boolean; mfa?: boolean }) {
  if (busy) return <LoaderCircle className="spin" size={17} />;
  return mfa ? <ShieldCheck size={17} /> : <LockKeyhole size={17} />;
}

function credentialsDisabled(email: string, password: string, submitting: boolean): boolean {
  return submitting || !email.trim() || password.length < 12;
}

function CredentialsForm({ email, password, submitting, error, onEmail, onPassword, onSubmit }: CredentialsFormProps) {
  return <form onSubmit={onSubmit} noValidate>
    <span className="eyebrow">Administrator sign in</span>
    <h2 id="form-heading">Welcome back</h2>
    <p className="form-intro">Use your PeerOnQ administrator account. Device credentials cannot sign in here.</p>
    <label htmlFor="email">Work email</label>
    <input id="email" name="email" type="email" autoComplete="username" value={email}
      onChange={(event) => onEmail(event.target.value)} required autoFocus />
    <label htmlFor="password">Password</label>
    <input id="password" name="password" type="password" autoComplete="current-password" value={password}
      onChange={(event) => onPassword(event.target.value)} minLength={12} required />
    <FormError error={error} />
    <button className="button primary full" type="submit" disabled={credentialsDisabled(email, password, submitting)}>
      <SubmitIcon busy={submitting} />
      Sign in securely
    </button>
  </form>;
}

interface MfaFormProps {
  code: string;
  submitting: boolean;
  error: ApiError | null;
  onCode(value: string): void;
  onCancel(): void;
  onSubmit(event: FormEvent): void;
}

function MfaForm({ code, submitting, error, onCode, onCancel, onSubmit }: MfaFormProps) {
  return <form onSubmit={onSubmit} noValidate>
    <span className="eyebrow">Second factor</span>
    <h2 id="form-heading">Verify administrator</h2>
    <p className="form-intro">Enter the current authenticator code or a single-use recovery code.</p>
    <label htmlFor="mfa-code">Verification code</label>
    <input id="mfa-code" name="mfa-code" type="text" inputMode="text" autoComplete="one-time-code"
      value={code} onChange={(event) => onCode(event.target.value)} minLength={6} maxLength={32}
      required autoFocus />
    <FormError error={error} />
    <button className="button primary full" type="submit" disabled={submitting || code.trim().length < 6}>
      <SubmitIcon busy={submitting} mfa />
      Verify and continue
    </button>
    <button className="button quiet full" type="button" disabled={submitting} onClick={onCancel}>Back to sign in</button>
  </form>;
}

export function LoginPage() {
  const { status, login, verifyMfa, cancelMfa, retrySession, error: sessionError } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [formError, setFormError] = useState<ApiError | null>(null);

  if (status === 'booting') {
    return <div className="auth-screen"><StatePanel state="loading" title="Restoring secure session" /></div>;
  }

  if (status === 'unavailable') {
    return (
      <div className="auth-screen">
        <Brand />
        <StatePanel state="error" error={sessionError} onRetry={() => void retrySession()} title="Admin service unavailable" />
      </div>
    );
  }

  const handleCredentials = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setFormError(null);
    try {
      await login(email.trim(), password);
    } catch (cause) {
      setFormError(cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'Sign-in failed.'));
    } finally {
      setPassword('');
      setSubmitting(false);
    }
  };

  const handleMfa = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setFormError(null);
    try {
      await verifyMfa(code.trim());
    } catch (cause) {
      setFormError(cause instanceof ApiError ? cause : new ApiError(0, 'unknown_error', 'Verification failed.'));
    } finally {
      setCode('');
      setSubmitting(false);
    }
  };

  return (
    <main className="auth-screen">
      <div className="auth-layout">
        <section className="auth-intro" aria-labelledby="auth-heading">
          <Brand />
          <div>
            <span className="eyebrow">Restricted administration</span>
            <h1 id="auth-heading">Operate PeerOnQ with verified access.</h1>
            <p>Live service health, presence, releases, diagnostics, and immutable audit history in one protected workspace.</p>
          </div>
          <ul className="security-points">
            <li><ShieldCheck size={18} aria-hidden="true" /> Privileged roles require multi-factor authentication.</li>
            <li><LockKeyhole size={18} aria-hidden="true" /> Access tokens remain in memory and expire quickly.</li>
            <li><KeyRound size={18} aria-hidden="true" /> Every sensitive operation is authorized and audited by the server.</li>
          </ul>
        </section>

        <section className="auth-card" aria-labelledby="form-heading">
          {status === 'mfa_required'
            ? <MfaForm code={code} submitting={submitting} error={formError} onCode={setCode}
                onCancel={cancelMfa} onSubmit={handleMfa} />
            : <CredentialsForm email={email} password={password} submitting={submitting} error={formError}
                onEmail={setEmail} onPassword={setPassword} onSubmit={handleCredentials} />}
          <p className="auth-support">Access problems are security events. Contact the designated PeerOnQ security owner.</p>
        </section>
      </div>
    </main>
  );
}
