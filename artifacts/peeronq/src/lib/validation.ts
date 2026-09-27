import { z } from "zod";

// PeerOnQ Device ID: XXX-XXX-XXX-XXX (four 3-digit groups).
// Accept digits with spaces or hyphens and normalize to the displayed grouped form.
export const deviceIdSchema = z.string()
  .transform((value) => {
    const compact = value.trim().replace(/[\s-]+/g, '');
    return /^\d{12}$/.test(compact)
      ? compact.replace(/(\d{3})(\d{3})(\d{3})(\d{3})/, '$1-$2-$3-$4')
      : value.trim();
  })
  .pipe(
    z.string().regex(/^\d{3}-\d{3}-\d{3}-\d{3}$/, "Invalid PeerOnQ ID format (e.g. 483-921-756-204)")
  );

export const contactNameSchema = z.string().min(1, "Name is required").max(100, "Name is too long");
export const deviceAliasSchema = z.string().min(1, "Alias is required").max(80, "Alias is too long");
const connectionModeSchema = z.enum(["view", "control", "file-transfer"]);

// Mirrors the AppSettings type. Used to reject corrupted or stale localStorage
// payloads instead of letting them reach the UI.
export const settingsSchema = z.object({
  launchAtStartup: z.boolean(),
  startMinimized: z.boolean(),
  closeToTray: z.boolean(),
  language: z.string().min(2).max(10),
  defaultConnectionMode: connectionModeSchema,
  theme: z.enum(["system", "light", "dark"]),
  compactMode: z.boolean(),
  reducedMotion: z.boolean(),
  quality: z.enum(["auto", "balanced", "performance", "quality"]),
  frameRate: z.enum(["30", "60", "auto"]),
  hardwareAcceleration: z.boolean(),
  showRemoteCursor: z.boolean(),
  requireApproval: z.boolean(),
  rememberApprovedDevices: z.boolean(),
  unattendedAccessEnabled: z.boolean(),
  diagnosticLevel: z.enum(["none", "basic", "full"]),
  crashReports: z.boolean(),
  analytics: z.boolean(),
});
