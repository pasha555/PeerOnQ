import { Download } from "lucide-react";
import type { ReactNode } from "react";
import { Button } from "@/components/ui/button";

type WindowsSource = "server" | "embedded" | "tracked" | "local" | "none";

interface ClientDownload {
  label: string;
  href: string;
  fileName?: string;
}

interface WindowsRelease {
  source: WindowsSource;
  version: string;
  unsigned: boolean;
  downloads: ClientDownload[];
}

type ClientPlatform = "windows" | "macos" | "linux" | "android" | "ios" | "ipados" | "unknown";

interface ClientDevice {
  platform: ClientPlatform;
  label: string;
  architecture?: "x64" | "arm64";
  detected: boolean;
}

const VERSION_PATTERN = /^[0-9]+(?:\.[0-9]+){2,3}$/;
const LOCAL_MSI_PATTERN = /^\/downloads\/[A-Za-z0-9._-]+\.msi$/;
const HTTPS_PATTERN = /^https:\/\/[a-z0-9.-]+(?::\d+)?(?:\/[^\s]*)?$/i;
const LOCAL_ARTIFACT_QUALIFIERS = new Set(["unsigned-development", "unsigned-public-pilot"]);

const PLATFORM_DOWNLOAD_ENV = {
  macos: "VITE_PEERONQ_MACOS_DOWNLOAD_URL",
  linuxX64: "VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL",
  linuxArm64: "VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL",
  android: "VITE_PEERONQ_ANDROID_DOWNLOAD_URL",
  ios: "VITE_PEERONQ_IOS_DOWNLOAD_URL",
} as const satisfies Record<string, keyof ImportMetaEnv>;

function readEnv(name: keyof ImportMetaEnv): string {
  return import.meta.env[name]?.trim() ?? "";
}

function validateBoolean(value: string, name: string): void {
  if (value && value !== "true" && value !== "false") {
    throw new Error(`${name} must be an exact boolean.`);
  }
}

function readOptionalHttpsDownload(name: keyof ImportMetaEnv): string {
  const value = readEnv(name);
  if (value && !HTTPS_PATTERN.test(value)) {
    throw new Error(`${String(name)} must be an HTTPS URL.`);
  }
  return value;
}

function validateEmbeddedRelease(url: string, version: string, unsignedPilot: string): void {
  if (url && !LOCAL_MSI_PATTERN.test(url)) {
    throw new Error("VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL must be a safe local MSI path.");
  }
  if (url && !VERSION_PATTERN.test(version)) {
    throw new Error("An embedded Windows client requires a numeric VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION.");
  }
  if (url && !unsignedPilot) {
    throw new Error("An embedded Windows client requires an explicit VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT flag.");
  }
  if (!url && unsignedPilot) {
    throw new Error("VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT requires an embedded Windows client URL.");
  }
}

function resolveSource(server: string, embeddedUrl: string, trackedBase: string, local: string): WindowsSource {
  if (server === "true") return "server";
  if (embeddedUrl) return "embedded";
  if (trackedBase) return "tracked";
  if (local === "true") return "local";
  return "none";
}

function buildDownloads(source: WindowsSource, embeddedUrl: string, trackedBase: string, version: string, artifactQualifier: string, enabled: boolean): ClientDownload[] {
  const x64 = { label: "Download for Windows x64", fileName: "PeerOnQ-Windows-x64.msi" };
  if (source === "server") return [{ ...x64, href: "/downloads/PeerOnQ-Windows-x64.msi" }];
  if (source === "embedded") {
    return [{ ...x64, href: `${embeddedUrl}?v=${encodeURIComponent(version)}` }];
  }
  if (source === "none") return [];
  if (!enabled) return [];

  const artifactPrefix = `PeerOnQ-${version}-${artifactQualifier}`;
  const tracked = source === "tracked";
  return [
    {
      ...x64,
      href: tracked
        ? `${trackedBase}/windows/latest?architecture=x64&channel=stable&source=website`
        : `/downloads/${artifactPrefix}-x64.msi`,
      fileName: tracked ? x64.fileName : `${artifactPrefix}-x64.msi`,
    },
    {
      label: "Download for Windows ARM64",
      href: tracked
        ? `${trackedBase}/windows/latest?architecture=arm64&channel=stable&source=website`
        : `/downloads/${artifactPrefix}-arm64.msi`,
      fileName: tracked ? "PeerOnQ-Windows-arm64.msi" : `${artifactPrefix}-arm64.msi`,
    },
  ];
}

function getWindowsRelease(): WindowsRelease {
  const server = readEnv("VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE");
  const embeddedUrl = readEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL");
  const embeddedVersion = readEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION");
  const unsignedPilot = readEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT");
  const local = readEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE");
  const localVersion = readEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION");
  const localArtifactQualifier = readEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER");
  const trackedBase = readEnv("VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL").replace(/\/+$/, "");

  validateBoolean(server, "VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE");
  validateBoolean(unsignedPilot, "VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT");
  validateBoolean(local, "VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE");
  validateEmbeddedRelease(embeddedUrl, embeddedVersion, unsignedPilot);
  if (server === "true" && embeddedUrl) {
    throw new Error("A website cannot select both the server-bundled and an embedded Windows client.");
  }
  if (trackedBase && !HTTPS_PATTERN.test(trackedBase)) {
    throw new Error("VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL must be an HTTPS URL.");
  }

  const source = resolveSource(server, embeddedUrl, trackedBase, local);
  if (source === "local" && !VERSION_PATTERN.test(localVersion)) {
    throw new Error("Local Windows downloads require a numeric VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION.");
  }
  if (source === "local" && !LOCAL_ARTIFACT_QUALIFIERS.has(localArtifactQualifier)) {
    throw new Error("Local Windows downloads require an explicit supported VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER.");
  }
  const version = source === "embedded" ? embeddedVersion : localVersion;
  return {
    source,
    version,
    unsigned: source === "embedded" && unsignedPilot === "true",
    downloads: buildDownloads(source, embeddedUrl, trackedBase, version, localArtifactQualifier, local === "true"),
  };
}

function detectClientDevice(): ClientDevice {
  if (typeof navigator === "undefined") {
    return { platform: "unknown", label: "This device", detected: false };
  }

  const userAgent = navigator.userAgent.toLowerCase();
  const platform = navigator.platform.toLowerCase();
  const arm64 = /arm64|aarch64/.test(`${userAgent} ${platform}`);

  if (/android/.test(userAgent)) return { platform: "android", label: "Android", detected: true };
  if (/ipad/.test(userAgent) || (platform === "macintel" && navigator.maxTouchPoints > 1)) {
    return { platform: "ipados", label: "iPadOS", detected: true };
  }
  if (/iphone|ipod/.test(userAgent)) return { platform: "ios", label: "iOS", detected: true };
  if (/windows|win32|win64/.test(`${userAgent} ${platform}`)) {
    return { platform: "windows", label: "Windows", architecture: arm64 ? "arm64" : "x64", detected: true };
  }
  if (/macintosh|mac os|macintel/.test(`${userAgent} ${platform}`)) {
    return { platform: "macos", label: "macOS", architecture: arm64 ? "arm64" : "x64", detected: true };
  }
  if (/linux/.test(`${userAgent} ${platform}`)) {
    return { platform: "linux", label: "Linux", architecture: arm64 ? "arm64" : "x64", detected: true };
  }

  return { platform: "unknown", label: "This device", detected: false };
}

function configuredDownload(label: string, href: string): ClientDownload | undefined {
  return href ? { label: `Download for ${label}`, href } : undefined;
}

function selectDeviceDownload(device: ClientDevice, windowsDownloads: readonly ClientDownload[]): ClientDownload | undefined {
  const configuredDownloads = {
    macos: readOptionalHttpsDownload(PLATFORM_DOWNLOAD_ENV.macos),
    linuxX64: readOptionalHttpsDownload(PLATFORM_DOWNLOAD_ENV.linuxX64),
    linuxArm64: readOptionalHttpsDownload(PLATFORM_DOWNLOAD_ENV.linuxArm64),
    android: readOptionalHttpsDownload(PLATFORM_DOWNLOAD_ENV.android),
    ios: readOptionalHttpsDownload(PLATFORM_DOWNLOAD_ENV.ios),
  };

  switch (device.platform) {
    case "windows": {
      const architecture = device.architecture ?? "x64";
      return windowsDownloads.find((download) => download.label.toLowerCase().includes(architecture));
    }
    case "macos":
      return configuredDownload(device.label, configuredDownloads.macos);
    case "linux":
      return configuredDownload(
        device.label,
        device.architecture === "arm64" ? configuredDownloads.linuxArm64 : configuredDownloads.linuxX64,
      );
    case "android":
      return configuredDownload(device.label, configuredDownloads.android);
    case "ios":
    case "ipados":
      return configuredDownload(device.label, configuredDownloads.ios);
    case "unknown":
      return undefined;
  }
}

export function DownloadsPage({ secondaryAction }: { secondaryAction?: ReactNode }) {
  const release = getWindowsRelease();
  const device = detectClientDevice();
  const download = selectDeviceDownload(device, release.downloads);
  const architectureLabel = device.architecture === "arm64" ? "ARM64" : "x64";
  const deviceLabel = device.platform === "windows" || device.platform === "linux"
    ? `${device.label} · ${architectureLabel}`
    : device.label;
  const version = device.platform === "windows" && (release.source === "local" || release.source === "embedded")
    ? release.version
    : "";
  const unsignedLabel = device.platform !== "windows"
    ? ""
    : release.source === "local"
      ? readEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER") === "unsigned-public-pilot"
        ? "Unsigned public pilot"
        : "Unsigned development build"
      : release.source === "embedded" && release.unsigned
        ? "Unsigned controlled pilot"
        : "";
  const unavailableReason = device.detected
    ? `A download for ${device.label}${device.architecture && device.platform !== "macos" ? ` ${architectureLabel}` : ""} is not currently available on this site.`
    : "We could not identify this device. No installer was selected.";

  return (
    <div id="client-download" className="scroll-mt-24 space-y-4" aria-label="PeerOnQ download">
      <div>
        <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Your device</p>
        <p className="mt-1 text-lg font-semibold">{device.detected ? deviceLabel : "Device not detected"}</p>
        {download && version && <p className="mt-1 text-sm text-muted-foreground">Version {version}</p>}
      </div>
      <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
        {download ? (
          <Button asChild size="lg" className="h-12 w-full rounded-xl text-sm shadow-lg shadow-primary/20 sm:w-auto">
            <a
              href={download.href}
              download={download.fileName}
              aria-label={device.platform === "windows"
                ? `Download PeerOnQ for Windows ${architectureLabel}`
                : `Download PeerOnQ for ${device.label}`}
            >
              <Download className="mr-2 h-5 w-5" aria-hidden="true" />Download PeerOnQ for {device.label}
            </a>
          </Button>
        ) : (
          <Button size="lg" className="h-12 w-full rounded-xl text-sm sm:w-auto" variant="secondary" disabled aria-label={`${device.label} app is not available yet`} aria-describedby="client-download-status">
            Download unavailable
          </Button>
        )}
        {secondaryAction}
      </div>
      {download && unsignedLabel && (
        <div className="rounded-lg border border-warning/30 bg-warning/10 px-4 py-3 text-sm">
          <p className="font-semibold">{unsignedLabel}</p>
          <p className="mt-1 text-muted-foreground">For testing only. This installer is not production-signed.</p>
        </div>
      )}
      <p id="client-download-status" className="text-sm leading-6 text-muted-foreground" role="status" aria-live="polite">
        {download ? "Selected using your browser's device information." : unavailableReason}
      </p>
    </div>
  );
}
