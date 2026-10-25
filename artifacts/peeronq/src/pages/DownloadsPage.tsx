import { Download } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Link } from "wouter";
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
  architectureRecommended?: boolean;
  detected: boolean;
}

interface ArchitectureHints {
  architecture?: string;
  bitness?: string;
  wow64?: boolean;
}

interface LocalClientHints {
  platform?: string;
  getHighEntropyValues?(values: string[]): Promise<ArchitectureHints>;
}

function localClientHints(): LocalClientHints | undefined {
  return typeof navigator === "undefined" ? undefined
    : (navigator as Navigator & { userAgentData?: LocalClientHints }).userAgentData;
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

function buildDownloads(source: WindowsSource, embeddedUrl: string, embeddedArm64Url: string, serverArm64: boolean, trackedBase: string, version: string, artifactQualifier: string, enabled: boolean): ClientDownload[] {
  const x64 = { label: "Download for Windows x64", fileName: "PeerOnQ-Windows-x64.msi" };
  const arm64 = { label: "Download for Windows ARM64", fileName: "PeerOnQ-Windows-arm64.msi" };
  if (source === "server") return [{ ...x64, href: "/downloads/PeerOnQ-Windows-x64.msi" }, ...(serverArm64 ? [{ ...arm64, href: "/downloads/PeerOnQ-Windows-arm64.msi" }] : [])];
  if (source === "embedded") {
    return [{ ...x64, href: `${embeddedUrl}?v=${encodeURIComponent(version)}` }, ...(embeddedArm64Url ? [{ ...arm64, href: `${embeddedArm64Url}?v=${encodeURIComponent(version)}` }] : [])];
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
  const embeddedArm64Url = readEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_ARM64_URL");
  const serverArm64 = readEnv("VITE_PEERONQ_SERVER_WINDOWS_ARM64_AVAILABLE");
  const embeddedVersion = readEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION");
  const unsignedPilot = readEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT");
  const local = readEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE");
  const localVersion = readEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION");
  const localArtifactQualifier = readEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER");
  const trackedBase = readEnv("VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL").replace(/\/+$/, "");

  validateBoolean(server, "VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE");
  validateBoolean(serverArm64, "VITE_PEERONQ_SERVER_WINDOWS_ARM64_AVAILABLE");
  validateBoolean(unsignedPilot, "VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT");
  validateBoolean(local, "VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE");
  validateEmbeddedRelease(embeddedUrl, embeddedVersion, unsignedPilot);
  if (embeddedArm64Url && (!embeddedUrl || !LOCAL_MSI_PATTERN.test(embeddedArm64Url))) {
    throw new Error("An embedded ARM64 client requires a safe local MSI path and the matching embedded x64 release.");
  }
  if (serverArm64 === "true" && server !== "true") {
    throw new Error("Server ARM64 availability requires the matching server x64 release.");
  }
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
    downloads: buildDownloads(source, embeddedUrl, embeddedArm64Url, serverArm64 === "true", trackedBase, version, localArtifactQualifier, local === "true"),
  };
}

function detectClientDevice(): ClientDevice {
  if (typeof navigator === "undefined") {
    return { platform: "unknown", label: "This device", detected: false };
  }

  const userAgent = navigator.userAgent.toLowerCase();
  const platform = `${navigator.platform} ${localClientHints()?.platform ?? ""}`.trim().toLowerCase();
  const arm64 = /arm64|aarch64/.test(`${userAgent} ${platform}`);
  const legacyX64 = /\b(?:win64|x64|amd64|x86_64)\b/.test(`${userAgent} ${platform}`);

  if (/android/.test(userAgent)) return { platform: "android", label: "Android", detected: true };
  if (/ipad/.test(userAgent) || (platform.startsWith("macintel") && navigator.maxTouchPoints > 1)) {
    return { platform: "ipados", label: "iPadOS", detected: true };
  }
  if (/iphone|ipod/.test(userAgent)) return { platform: "ios", label: "iOS", detected: true };
  if (/windows|win32|win64/.test(`${userAgent} ${platform}`)) {
    // Frozen desktop UAs can mask ARM64. A legacy x64 token is a recommendation,
    // not a claim that the browser verified the machine's native architecture.
    const recommendX64 = legacyX64 || /\bwow64\b/.test(userAgent);
    return { platform: "windows", label: "Windows", architecture: arm64 ? "arm64" : recommendX64 ? "x64" : undefined, architectureRecommended: !arm64 && recommendX64, detected: true };
  }
  if (/macintosh|mac os|macintel/.test(`${userAgent} ${platform}`)) {
    return { platform: "macos", label: "macOS", architecture: arm64 ? "arm64" : "x64", detected: true };
  }
  if (/linux/.test(`${userAgent} ${platform}`)) {
    return { platform: "linux", label: "Linux", architecture: arm64 ? "arm64" : legacyX64 ? "x64" : undefined, architectureRecommended: !arm64 && legacyX64, detected: true };
  }

  return { platform: "unknown", label: "This device", detected: false };
}

function applyArchitectureHints(device: ClientDevice, values: ArchitectureHints): ClientDevice {
  // Never erase an explicit ARM64 identity with partial or contradictory metadata.
  if (device.architecture === "arm64" && !device.architectureRecommended) return device;
  const family = values.architecture?.trim().toLowerCase() ?? "";
  const bitness = values.bitness?.trim() ?? "";
  const is64Bit = bitness === "64" || (device.platform === "windows" && values.wow64 === true);
  const is32Bit = bitness === "32" && !is64Bit;
  const architecture = !is32Bit && (/^(arm64|aarch64)$/.test(family) || (family === "arm" && is64Bit)) ? "arm64"
    : !is32Bit && (/^(x64|x86_64|amd64)$/.test(family) || (family === "x86" && is64Bit)) ? "x64" : undefined;
  if (architecture) return { ...device, architecture, architectureRecommended: false };
  // A positively reported unsupported family/bitness takes precedence over a UA
  // recommendation. Missing/restricted fields leave the local fallback intact.
  if (is32Bit || (family && family !== "x86")) {
    return { ...device, architecture: undefined, architectureRecommended: false };
  }
  return device;
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
      const architecture = device.architecture;
      if (!architecture) return undefined;
      return windowsDownloads.find((download) => download.label.toLowerCase().includes(architecture));
    }
    case "macos":
      return configuredDownload(device.label, configuredDownloads.macos);
    case "linux":
      if (!device.architecture) return undefined;
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
  const [device, setDevice] = useState(detectClientDevice);
  const manuallySelected = useRef(false);
  const [hints] = useState(localClientHints);
  const [checkingArchitecture, setCheckingArchitecture] = useState(() => Boolean(hints?.getHighEntropyValues && ["windows", "linux"].includes(device.platform)));
  useEffect(() => {
    if (manuallySelected.current || !hints?.getHighEntropyValues || !["windows", "linux"].includes(device.platform)) return;
    let active = true;
    // Restricted or stalled browser metadata must not leave the action checking forever.
    const timeout = window.setTimeout(() => { if (active && !manuallySelected.current) setCheckingArchitecture(false); }, 1500);
    // Client Hints read local browser metadata; this does not contact a server.
    void Promise.resolve().then(() => hints.getHighEntropyValues!(["architecture", "bitness", "wow64"])).then((values) => {
      if (!active || manuallySelected.current) return;
      setDevice((current) => applyArchitectureHints(current, values));
    }).catch(() => {
      // Preserve explicit local ARM64 or the clearly labeled UA recommendation.
    }).finally(() => { window.clearTimeout(timeout); if (active) setCheckingArchitecture(false); });
    return () => { active = false; window.clearTimeout(timeout); };
  }, [hints, device.platform]);
  const selectedDownload = selectDeviceDownload(device, release.downloads);
  const needsArchitecture = ["windows", "linux"].includes(device.platform) && !device.architecture;
  const download = checkingArchitecture || needsArchitecture
    ? undefined : selectedDownload;
  const architectureLabel = device.architecture === "arm64" ? "ARM64" : device.architecture === "x64" ? "x64" : "Choose architecture";
  const deviceLabel = !device.architectureRecommended && (device.platform === "windows" || device.platform === "linux")
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
  const unavailableReason = checkingArchitecture ? "Checking the browser's architecture information…" : needsArchitecture
    ? "The browser did not identify a supported 64-bit architecture. Choose the target device's architecture above to select an installer."
    : device.detected
    ? `A download for ${device.label}${device.architecture && device.platform !== "macos" ? ` ${architectureLabel}` : ""} is not currently available on this site.`
    : "We could not identify this device. No installer was selected.";

  return (
    <div id="client-download" className="scroll-mt-24 space-y-4" aria-label="PeerOnQ download">
      <div>
        <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Your device</p>
        <p className="mt-1 text-lg font-semibold">{device.detected ? deviceLabel : "Device not detected"}</p>
        {download && device.architectureRecommended && <p className="mt-1 text-sm text-muted-foreground">Recommended installer: {device.label} {architectureLabel}</p>}
        {download && version && <p className="mt-1 text-sm text-muted-foreground">Version {version}</p>}
      </div>
      <details className="text-sm text-muted-foreground" open={!checkingArchitecture && needsArchitecture ? true : undefined}>
        <summary className="w-fit cursor-pointer rounded-sm py-2 font-medium text-foreground">Choose another device or architecture</summary>
        <div className="mt-2 flex flex-wrap gap-3">
          <label className="grid gap-1">Platform<select className="rounded-md border bg-background p-2 text-foreground" value={device.platform} onChange={(event) => {
            manuallySelected.current = true; setCheckingArchitecture(false);
            const platform = event.target.value as ClientPlatform;
            const labels: Record<ClientPlatform, string> = { windows: "Windows", macos: "macOS", linux: "Linux", android: "Android", ios: "iOS", ipados: "iPadOS", unknown: "This device" };
            setDevice({ platform, label: labels[platform], detected: platform !== "unknown", architecture: ["windows", "linux"].includes(platform) ? undefined : device.architecture });
          }}><option value="unknown">Choose platform</option><option value="windows">Windows</option><option value="macos">macOS</option><option value="linux">Linux</option><option value="android">Android</option><option value="ios">iOS</option><option value="ipados">iPadOS</option></select></label>
          {["windows", "linux"].includes(device.platform) ? <label className="grid gap-1">Architecture<select className="rounded-md border bg-background p-2 text-foreground" value={device.architecture ?? ""} onChange={(event) => { manuallySelected.current = true; setCheckingArchitecture(false); setDevice({ ...device, architecture: event.target.value === "arm64" ? "arm64" : event.target.value === "x64" ? "x64" : undefined, architectureRecommended: false }); }}><option value="">Choose architecture</option><option value="x64">x64</option><option value="arm64">ARM64</option></select></label> : null}
        </div>
      </details>
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
        <details className="text-sm text-muted-foreground">
          <summary className="w-fit cursor-pointer rounded-sm py-2 font-medium text-foreground">Installer details</summary>
          <div className="mt-1 space-y-1">
            <p>{unsignedLabel}</p>
            <p>Digital signature: Not signed.</p>
            <p><Link href="/code-signing" className="font-medium text-primary hover:underline">Code signing policy</Link></p>
          </div>
        </details>
      )}
      <p id="client-download-status" className="text-sm leading-6 text-muted-foreground" role="status" aria-live="polite">
        {download ? device.architectureRecommended
          ? "Recommended from your browser's 64-bit signature. Native architecture could not be verified; if this is an ARM64 device, choose ARM64 above."
          : "Selected for this platform and architecture. Choose another device above if needed." : unavailableReason}
      </p>
    </div>
  );
}
