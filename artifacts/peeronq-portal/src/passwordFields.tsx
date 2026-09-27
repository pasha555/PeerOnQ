import { useId, useState } from 'react';
import { useAuth } from './auth';
import type { AuthCapabilities } from './types';
import { ErrorState } from './components';

export function PasswordField({ name, label, autoComplete = 'new-password', describedBy }: {
  name: string; label: string; autoComplete?: 'current-password' | 'new-password'; describedBy?: string;
}) {
  const [visible, setVisible] = useState(false);
  const id = useId();
  return <div className="password-field"><label htmlFor={id}>{label}</label><div className="password-control">
    <input id={id} name={name} type={visible ? 'text' : 'password'} required maxLength={128} autoComplete={autoComplete} aria-describedby={describedBy} />
    <button type="button" className="button secondary" aria-label={`${visible ? 'Hide' : 'Show'} ${label.toLowerCase()}`} aria-pressed={visible} onClick={() => setVisible(!visible)}>{visible ? 'Hide' : 'Show'}</button>
  </div></div>;
}

export function NewPasswordFields({ label = 'New password' }: { label?: string }) {
  const { capabilities, capabilitiesError, reloadCapabilities } = useAuth();
  const helpId = useId();
  const rules = capabilities?.passwordRules;
  return <><PasswordField name="password" label={label} describedBy={helpId} /><PasswordField name="confirmPassword" label="Confirm password" />
    {capabilitiesError ? <ErrorState message={capabilitiesError} retry={() => void reloadCapabilities()} /> : null}
    <p id={helpId} className="field-help">{rules ? `Use ${rules.minLength}–${rules.maxLength} characters, including an uppercase letter, a lowercase letter and a number.` : 'Password rules are unavailable. Please wait or retry before submitting.'}</p></>;
}

export function passwordError(data: FormData, rules?: AuthCapabilities['passwordRules']): string | null {
  if (!rules) return 'Password rules are unavailable. Please reload and try again.';
  const password = String(data.get('password') ?? '');
  if (password !== data.get('confirmPassword')) return 'Passwords do not match.';
  if (password.length < rules.minLength || password.length > rules.maxLength ||
      (rules.requireUppercase && !/\p{Lu}/u.test(password)) || (rules.requireLowercase && !/\p{Ll}/u.test(password)) ||
      (rules.requireDigit && !/\p{Nd}/u.test(password))) return 'Use the required password length, an uppercase letter, a lowercase letter and a number.';
  return null;
}
