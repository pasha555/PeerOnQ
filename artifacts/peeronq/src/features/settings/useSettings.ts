import { useMemo } from 'react';
import { useLocalStorage } from '@/hooks/useLocalStorage';
import { settingsSchema } from '@/lib/validation';
import { STORAGE_KEYS } from '@/compatibility/legacyBrandStorageMigration';
import type { AppSettings } from '@/types';

const defaultSettings: AppSettings = {
  launchAtStartup: false,
  startMinimized: false,
  closeToTray: false,
  language: 'en-US',
  defaultConnectionMode: 'view',
  theme: 'system',
  compactMode: false,
  reducedMotion: false,
  quality: 'auto',
  frameRate: 'auto',
  hardwareAcceleration: true,
  showRemoteCursor: true,
  requireApproval: true,
  rememberApprovedDevices: false,
  unattendedAccessEnabled: false,
  diagnosticLevel: 'none',
  crashReports: false,
  analytics: false,
};

// Stored settings can be stale (written before a key existed) or corrupted by
// hand-editing localStorage. Keep whatever fields are still valid, default the rest.
function sanitize(value: unknown): AppSettings {
  const result = settingsSchema.safeParse(value);
  if (result.success) return result.data;

  const partial = settingsSchema.partial().safeParse(value);
  return { ...defaultSettings, ...(partial.success ? partial.data : {}) };
}

export function useSettings() {
  const [storedSettings, setSettings] = useLocalStorage<AppSettings>(STORAGE_KEYS.settings, defaultSettings);

  const settings = useMemo(() => sanitize(storedSettings), [storedSettings]);

  const updateSetting = <K extends keyof AppSettings>(key: K, value: AppSettings[K]) => {
    setSettings((prev) => ({ ...sanitize(prev), [key]: value }));
  };

  const updateSettings = (newSettings: Partial<AppSettings>) => {
    setSettings((prev) => ({ ...sanitize(prev), ...newSettings }));
  };

  return { settings, updateSetting, updateSettings, defaultSettings };
}
