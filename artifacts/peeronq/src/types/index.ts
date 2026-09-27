export type DeviceStatus = "online" | "offline" | "unknown";
export type ConnectionMode = "view" | "control" | "file-transfer";
export type SessionDirection = "incoming" | "outgoing";
export type SessionState = "requesting" | "awaiting-permission" | "connecting" | "connected" | "reconnecting" | "ended" | "failed";
export type SessionMode = "view" | "control" | "file-transfer";

export interface Device {
  id: string;
  name: string;
  peerOnQId: string; // display format: XXX-XXX-XXX-XXX
  status: DeviceStatus;
  os: string;
  appVersion: string;
  lastSeen?: string;
  isPrototypeRecord?: boolean; // true = created locally in browser
}

export interface Session {
  id: string;
  remoteDeviceId: string;
  remoteDeviceName?: string;
  direction: SessionDirection;
  mode: SessionMode;
  state: SessionState;
  startedAt: string;
  endedAt?: string;
  durationMs?: number;
  isPrototypeRecord?: boolean;
}

export type TransferDirection = "upload" | "download";
export type TransferState = "queued" | "transferring" | "paused" | "completed" | "failed" | "cancelled";

export interface FileTransfer {
  id: string;
  fileName: string;
  sizeBytes: number;
  transferredBytes: number;
  direction: TransferDirection;
  state: TransferState;
  startedAt?: string;
}

export interface Contact {
  id: string;
  name: string;
  peerOnQId: string;
  groupId?: string;
  tags?: string[];
  isFavorite?: boolean;
  notes?: string;
  isPrototypeRecord?: boolean;
}

export interface ContactGroup {
  id: string;
  name: string;
}

export interface AppSettings {
  launchAtStartup: boolean;
  startMinimized: boolean;
  closeToTray: boolean;
  language: string;
  defaultConnectionMode: ConnectionMode;
  theme: "system" | "light" | "dark";
  compactMode: boolean;
  reducedMotion: boolean;
  quality: "auto" | "balanced" | "performance" | "quality";
  frameRate: "30" | "60" | "auto";
  hardwareAcceleration: boolean;
  showRemoteCursor: boolean;
  requireApproval: boolean;
  rememberApprovedDevices: boolean;
  unattendedAccessEnabled: boolean;
  diagnosticLevel: "none" | "basic" | "full";
  crashReports: boolean;
  analytics: boolean;
}

export interface NotConfigured { _type: "NotConfigured"; reason: string; }
export function isNotConfigured(v: unknown): v is NotConfigured {
  return typeof v === "object" && v !== null && (v as NotConfigured)._type === "NotConfigured";
}
