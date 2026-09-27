export interface Profile { id: string; email: string; displayName: string; emailVerified: boolean; mfaEnabled: boolean; createdAtUtc: string }
export interface AuthCapabilities {
  registrationMode: 'Closed' | 'InvitationOnly' | 'Open'; registrationAvailable: boolean;
  requireEmailVerification: boolean; passwordResetAvailable: boolean; mfaAvailable: boolean;
  passwordRules: { minLength: number; maxLength: number; requireUppercase: boolean; requireLowercase: boolean; requireDigit: boolean };
}
export interface Organization { id: string; name: string; role: 'Owner' | 'Administrator' | 'Technician' | 'Member' | 'Auditor'; ownerAccountId: string }
export interface Session { id: string; userAgentSummary: string; createdAtUtc: string; expiresAtUtc: string; revokedAtUtc: string | null }
export interface TrustedDevice { id: string; name: string; createdAtUtc: string; expiresAtUtc: string; revokedAtUtc: string | null }
export interface Member { accountId: string; displayName: string; email: string; role: Organization['role']; joinedAtUtc: string }
export interface Team { id: string; name: string; createdAtUtc: string }
export interface Invitation { id: string; email: string; role: Organization['role']; createdAtUtc: string; expiresAtUtc: string; acceptedAtUtc: string | null; revokedAtUtc: string | null }
export interface Device { id: string; maskedPublicDeviceId: string; displayName: string; lastSeenAtUtc: string; isRevoked: boolean }
export interface RemoteSession { id: string; permissionMode: string; connectionPath: string; lifecycle: string; startedAtUtc: string; endedAtUtc: string | null }
export interface SecurityEvent { id: string; action: string; result: string; timestampUtc: string; correlationId: string; safeMetadata: string | null }
export interface Policy { organizationId: string; viewOnlyAllowed: boolean; fullControlAllowed: boolean; fileTransferAllowed: boolean; clipboardAllowed: boolean; unattendedAccessAllowed: boolean; mfaRequired: boolean; trustedDeviceLifetimeDays: number; auditRetentionDays: number; approvedRelayRegionsCsv: string; minimumClientVersion: string; hybridSecurityRequired: boolean }
