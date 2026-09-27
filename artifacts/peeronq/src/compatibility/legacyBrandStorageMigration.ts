export const STORAGE_KEYS = {
  devices: "peeronq_devices",
  sessions: "peeronq_sessions",
  contacts: "peeronq_contacts",
  groups: "peeronq_groups",
  settings: "peeronq_settings",
  theme: "peeronq_theme",
} as const;

type JsonRecord = Record<string, unknown>;

function isRecord(value: unknown): value is JsonRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function migrateIdentifiedRecords(value: unknown): JsonRecord[] {
  if (!Array.isArray(value) || !value.every(isRecord)) {
    throw new Error("Invalid stored record collection");
  }

  return value.map((record) => {
    const { linkoraId, ...currentRecord } = record;
    const peerOnQId = currentRecord.peerOnQId ?? linkoraId;

    if (typeof peerOnQId !== "string") {
      throw new Error("Stored record is missing its device ID");
    }

    return { ...currentRecord, peerOnQId };
  });
}

function requireRecordArray(value: unknown): JsonRecord[] {
  if (!Array.isArray(value) || !value.every(isRecord)) {
    throw new Error("Invalid stored record collection");
  }
  return value;
}

function requireRecord(value: unknown): JsonRecord {
  if (!isRecord(value)) {
    throw new Error("Invalid stored settings");
  }
  return value;
}

function migrateJson(rawValue: string, transform: (value: unknown) => unknown): string {
  return JSON.stringify(transform(JSON.parse(rawValue)));
}

const migrations = [
  {
    legacyKey: "linkora_devices",
    currentKey: STORAGE_KEYS.devices,
    migrate: (rawValue: string) => migrateJson(rawValue, migrateIdentifiedRecords),
  },
  {
    legacyKey: "linkora_sessions",
    currentKey: STORAGE_KEYS.sessions,
    migrate: (rawValue: string) => migrateJson(rawValue, requireRecordArray),
  },
  {
    legacyKey: "linkora_contacts",
    currentKey: STORAGE_KEYS.contacts,
    migrate: (rawValue: string) => migrateJson(rawValue, migrateIdentifiedRecords),
  },
  {
    legacyKey: "linkora_groups",
    currentKey: STORAGE_KEYS.groups,
    migrate: (rawValue: string) => migrateJson(rawValue, requireRecordArray),
  },
  {
    legacyKey: "linkora_settings",
    currentKey: STORAGE_KEYS.settings,
    migrate: (rawValue: string) => migrateJson(rawValue, requireRecord),
  },
  {
    legacyKey: "linkora_theme",
    currentKey: STORAGE_KEYS.theme,
    migrate: (rawValue: string) => {
      if (rawValue !== "system" && rawValue !== "light" && rawValue !== "dark") {
        throw new Error("Invalid stored theme");
      }
      return rawValue;
    },
  },
] as const;

export function migrateLegacyStorage(storage?: Storage): void {
  let targetStorage = storage;

  if (!targetStorage) {
    try {
      targetStorage = window.localStorage;
    } catch {
      return;
    }
  }

  for (const migration of migrations) {
    try {
      if (targetStorage.getItem(migration.currentKey) !== null) continue;

      const legacyValue = targetStorage.getItem(migration.legacyKey);
      if (legacyValue === null) continue;

      targetStorage.setItem(migration.currentKey, migration.migrate(legacyValue));
    } catch {
      // Preserve the legacy value and leave the new key unset when reading,
      // validation, serialization, or storage fails.
    }
  }
}
