import type { AuthCapabilities } from '../types';
export const capabilities: AuthCapabilities = {
  registrationMode: 'Open', registrationAvailable: true, requireEmailVerification: true,
  passwordResetAvailable: true, mfaAvailable: true,
  passwordRules: { minLength: 12, maxLength: 128, requireUppercase: true, requireLowercase: true, requireDigit: true },
};
