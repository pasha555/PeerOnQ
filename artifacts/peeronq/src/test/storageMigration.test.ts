import { beforeEach, describe, expect, it } from "vitest";
import { migrateLegacyStorage, STORAGE_KEYS } from '../compatibility/legacyBrandStorageMigration';

describe("legacy browser storage migration", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("copies every legacy key, renames device ID fields, and remains idempotent", () => {
    window.localStorage.setItem(
      "linkora_devices",
      JSON.stringify([{ id: "device-1", name: "Office", linkoraId: "111-222-333-444" }]),
    );
    window.localStorage.setItem(
      "linkora_sessions",
      JSON.stringify([{ id: "session-1", remoteDeviceId: "111-222-333-444" }]),
    );
    window.localStorage.setItem(
      "linkora_contacts",
      JSON.stringify([{ id: "contact-1", name: "Alex", linkoraId: "555-666-777-888" }]),
    );
    window.localStorage.setItem("linkora_groups", JSON.stringify([{ id: "group-1", name: "Team" }]));
    window.localStorage.setItem("linkora_settings", JSON.stringify({ theme: "dark", analytics: false }));
    window.localStorage.setItem("linkora_theme", "dark");

    migrateLegacyStorage();

    expect(JSON.parse(window.localStorage.getItem(STORAGE_KEYS.devices) ?? "[]")).toEqual([
      { id: "device-1", name: "Office", peerOnQId: "111-222-333-444" },
    ]);
    expect(JSON.parse(window.localStorage.getItem(STORAGE_KEYS.sessions) ?? "[]")).toEqual([
      { id: "session-1", remoteDeviceId: "111-222-333-444" },
    ]);
    expect(JSON.parse(window.localStorage.getItem(STORAGE_KEYS.contacts) ?? "[]")).toEqual([
      { id: "contact-1", name: "Alex", peerOnQId: "555-666-777-888" },
    ]);
    expect(JSON.parse(window.localStorage.getItem(STORAGE_KEYS.groups) ?? "[]")).toEqual([
      { id: "group-1", name: "Team" },
    ]);
    expect(JSON.parse(window.localStorage.getItem(STORAGE_KEYS.settings) ?? "{}")).toEqual({
      theme: "dark",
      analytics: false,
    });
    expect(window.localStorage.getItem(STORAGE_KEYS.theme)).toBe("dark");

    const migratedDevices = window.localStorage.getItem(STORAGE_KEYS.devices);
    migrateLegacyStorage();

    expect(window.localStorage.getItem(STORAGE_KEYS.devices)).toBe(migratedDevices);
    expect(window.localStorage.getItem("linkora_devices")).not.toBeNull();
    expect(window.localStorage.getItem("linkora_theme")).toBe("dark");
  });

  it("never overwrites a current key", () => {
    const currentDevices = JSON.stringify([
      { id: "current", name: "Current", peerOnQId: "999-888-777-666" },
    ]);
    window.localStorage.setItem(STORAGE_KEYS.devices, currentDevices);
    window.localStorage.setItem(
      "linkora_devices",
      JSON.stringify([{ id: "legacy", name: "Legacy", linkoraId: "111-222-333-444" }]),
    );

    migrateLegacyStorage();

    expect(window.localStorage.getItem(STORAGE_KEYS.devices)).toBe(currentDevices);
  });

  it("leaves malformed legacy values untouched and does not create current keys", () => {
    window.localStorage.setItem("linkora_devices", "{not-json");
    window.localStorage.setItem("linkora_theme", "blue");

    migrateLegacyStorage();

    expect(window.localStorage.getItem(STORAGE_KEYS.devices)).toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEYS.theme)).toBeNull();
    expect(window.localStorage.getItem("linkora_devices")).toBe("{not-json");
    expect(window.localStorage.getItem("linkora_theme")).toBe("blue");
  });
});
