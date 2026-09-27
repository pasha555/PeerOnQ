import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import App from "../App";
import { OpenAppButton } from "../components/OpenAppButton";
import { DownloadsPage } from "../pages/DownloadsPage";

beforeAll(() => {
  Object.defineProperty(window, "matchMedia", {
    configurable: true,
    value: vi.fn().mockImplementation(() => ({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  vi.useRealTimers();
  window.history.replaceState(null, "", "/");
});

function expectDownloadsLinksToUseDocumentNavigation() {
  const links = screen.getAllByRole("link").filter((link) => link.getAttribute("href") === "/downloads");
  expect(links.length).toBeGreaterThan(0);

  for (const link of links) {
    let preventedBeforeBrowserNavigation = false;
    document.addEventListener("click", (event) => {
      preventedBeforeBrowserNavigation = event.defaultPrevented;
      event.preventDefault();
    }, { once: true });
    const click = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
    link.dispatchEvent(click);
    expect(preventedBeforeBrowserNavigation).toBe(false);
  }
}

describe("route surface separation", () => {
  it("renders public routes without the desktop sidebar", () => {
    window.history.replaceState(null, "", "/features");
    render(<App />);

    expect(screen.getByRole("navigation", { name: "Public website" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Remote Access" })).not.toBeInTheDocument();
    expect(screen.queryByText("Desktop application UI preview — this is not the production website.")).not.toBeInTheDocument();
  });

  it("keeps development service links out of the public website", () => {
    vi.stubEnv("VITE_PEERONQ_ADMIN_PANEL_URL", "https://admin.dev.localhost:8443");
    vi.stubEnv("VITE_PEERONQ_CLOUD_HEALTH_URL", "https://api.dev.localhost:8443/health/ready");
    window.history.replaceState(null, "", "/features");
    render(<App />);

    expect(screen.queryByRole("complementary", { name: "Phase 6 development tools" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Admin console" })).not.toBeInTheDocument();
  });

  it("shows exactly one device-matched Download action on the public page", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.30");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER", "unsigned-development");
    window.history.replaceState(null, "", "/");
    render(<App />);

    const header = screen.getByRole("banner");
    expect(header).not.toHaveTextContent("Download PeerOnQ");
    expect(screen.getAllByRole("link", { name: /Download PeerOnQ for Windows/i })).toHaveLength(1);
  });

  it("states the hybrid post-quantum connection design accurately", () => {
    window.history.replaceState(null, "", "/");
    render(<App />);

    expect(screen.getByText("Hybrid post-quantum connection setup")).toBeInTheDocument();
    expect(screen.getByText(/ML-KEM-768 and X25519 establish session key material/)).toBeInTheDocument();
    expect(screen.getByText(/AES-256-GCM protects encoded video/)).toBeInTheDocument();
  });

  it("uses one continuous narrative without the retired repeated step section", () => {
    window.history.replaceState(null, "", "/");
    render(<App />);

    expect(screen.queryByText("Three deliberate steps. No mystery.")).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Everything the session needs. Nothing hidden around it." })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Security is the path, not a badge." })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Built in the right order." })).toBeInTheDocument();
  });

  it("opens and closes the accessible mobile public menu", () => {
    window.history.replaceState(null, "", "/features");
    render(<App />);

    const openButton = screen.getByRole("button", { name: "Open menu" });
    expect(openButton).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(openButton);

    expect(screen.getByRole("navigation", { name: "Mobile public website" })).toBeInTheDocument();
    const closeButton = screen.getByRole("button", { name: "Close menu" });
    expect(closeButton).toHaveAttribute("aria-expanded", "true");
    fireEvent.click(closeButton);

    expect(screen.queryByRole("navigation", { name: "Mobile public website" })).not.toBeInTheDocument();
  });

  it.each([
    ["/features", "#product"],
    ["/security", "#security"],
    ["/downloads", "#download"],
    ["/about", "#strategy"],
    ["/help", "#help"],
  ])(
    "redirects the retired public route %s into the single page",
    async (sourcePath, destinationHash) => {
      window.history.replaceState(null, "", sourcePath);
      render(<App />);

      await waitFor(() => {
        expect(window.location.pathname).toBe("/");
        expect(window.location.hash).toBe(destinationHash);
      });
    },
  );

  it("exposes the real Phase 6 development services on the desktop preview", () => {
    vi.stubEnv("VITE_PEERONQ_ADMIN_PANEL_URL", "https://admin.dev.localhost:8443");
    vi.stubEnv("VITE_PEERONQ_CLOUD_HEALTH_URL", "https://api.dev.localhost:8443/health/ready");
    vi.stubEnv("VITE_PEERONQ_GRAFANA_URL", "https://grafana.dev.localhost:8443");
    vi.stubEnv("VITE_PEERONQ_PROMETHEUS_URL", "http://localhost:9090");
    window.history.replaceState(null, "", "/desktop-preview/dashboard");
    render(<App />);

    expect(screen.getByRole("complementary", { name: "Phase 6 development tools" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Admin console" })).toHaveAttribute(
      "href",
      "https://admin.dev.localhost:8443/",
    );
    expect(screen.getByRole("link", { name: "Cloud health" })).toHaveAttribute(
      "href",
      "https://api.dev.localhost:8443/health/ready",
    );
    expect(screen.getByRole("link", { name: "Grafana" })).toHaveAttribute(
      "href",
      "https://grafana.dev.localhost:8443/",
    );
    expect(screen.getByRole("link", { name: "Prometheus" })).toHaveAttribute("href", "http://localhost:9090/");
  });

  it("does not expose an insecure non-loopback development link", () => {
    vi.stubEnv("VITE_PEERONQ_ADMIN_PANEL_URL", "http://admin.example.test");
    window.history.replaceState(null, "", "/features");
    render(<App />);

    expect(screen.queryByRole("link", { name: "Admin console" })).not.toBeInTheDocument();
  });

  it("offers one device-matched local Windows download", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.30");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER", "unsigned-development");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toHaveAttribute(
      "href",
      "/downloads/PeerOnQ-0.9.30-unsigned-development-x64.msi",
    );
    expect(screen.queryByRole("link", { name: "Download PeerOnQ for Windows ARM64" })).not.toBeInTheDocument();
    expect(screen.queryByText(/Detected:/)).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "View SHA-256 checksums" })).not.toBeInTheDocument();
    expect(screen.queryByText("Unsigned controlled-test package available.")).not.toBeInTheDocument();
  });

  it("fails closed when local Windows download version metadata is missing", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");

    expect(() => render(<DownloadsPage />)).toThrow(
      "Local Windows downloads require a numeric VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION.",
    );
  });

  it("fails closed when local Windows download classification metadata is missing", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.30");

    expect(() => render(<DownloadsPage />)).toThrow(
      "Local Windows downloads require an explicit supported VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER.",
    );
  });

  it("uses an explicitly classified public-pilot package pair", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.66");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER", "unsigned-public-pilot");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toHaveAttribute(
      "href",
      "/downloads/PeerOnQ-0.9.66-unsigned-public-pilot-x64.msi",
    );
  });

  it("detects macOS without exposing a fake download", () => {
    vi.spyOn(window.navigator, "userAgent", "get").mockReturnValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5)");
    vi.spyOn(window.navigator, "platform", "get").mockReturnValue("MacIntel");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("button", { name: "macOS app is not available yet" })).toBeDisabled();
    expect(screen.queryByRole("link", { name: /download peeronq/i })).not.toBeInTheDocument();
  });

  it.each([
    [
      "macOS",
      "VITE_PEERONQ_MACOS_DOWNLOAD_URL",
      "https://download.peeronq.example/macos/latest",
      "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5)",
      "MacIntel",
    ],
    [
      "iOS",
      "VITE_PEERONQ_IOS_DOWNLOAD_URL",
      "https://apps.apple.com/app/peeronq/id1234567890",
      "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)",
      "iPhone",
    ],
    [
      "Android",
      "VITE_PEERONQ_ANDROID_DOWNLOAD_URL",
      "https://play.google.com/store/apps/details?id=io.peeronq.android",
      "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro)",
      "Linux armv8l",
    ],
    [
      "Linux",
      "VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL",
      "https://download.peeronq.example/linux/latest?architecture=arm64",
      "Mozilla/5.0 (X11; Linux aarch64)",
      "Linux aarch64",
    ],
  ])("offers the configured %s app to the matching device", (label, envName, href, userAgent, platform) => {
    vi.stubEnv(envName, href);
    vi.spyOn(window.navigator, "userAgent", "get").mockReturnValue(userAgent);
    vi.spyOn(window.navigator, "platform", "get").mockReturnValue(platform);
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: `Download PeerOnQ for ${label}` })).toHaveAttribute("href", href);
  });

  it("uses the iOS install URL for an iPadOS device", () => {
    const href = "https://apps.apple.com/app/peeronq/id1234567890";
    vi.stubEnv("VITE_PEERONQ_IOS_DOWNLOAD_URL", href);
    vi.spyOn(window.navigator, "userAgent", "get").mockReturnValue("Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X)");
    vi.spyOn(window.navigator, "platform", "get").mockReturnValue("iPad");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for iPadOS" })).toHaveAttribute("href", href);
  });

  it("rejects an insecure configured platform download", () => {
    vi.stubEnv("VITE_PEERONQ_MACOS_DOWNLOAD_URL", "http://download.peeronq.example/macos/latest");

    expect(() => render(<DownloadsPage />)).toThrow(
      "VITE_PEERONQ_MACOS_DOWNLOAD_URL must be an HTTPS URL.",
    );
  });

  it("does not guess Windows when the device platform is unknown", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.66");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER", "unsigned-public-pilot");
    vi.spyOn(window.navigator, "userAgent", "get").mockReturnValue("PeerOnQ test client");
    vi.spyOn(window.navigator, "platform", "get").mockReturnValue("Unknown");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("button", { name: "This device app is not available yet" })).toBeDisabled();
    expect(screen.queryByRole("link", { name: /download peeronq/i })).not.toBeInTheDocument();
  });

  it("selects the ARM64 package for a detected Windows ARM64 device", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.30");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER", "unsigned-development");
    vi.spyOn(window.navigator, "userAgent", "get").mockReturnValue("Mozilla/5.0 (Windows NT 10.0; ARM64)");
    vi.spyOn(window.navigator, "platform", "get").mockReturnValue("Win32");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows ARM64" })).toHaveAttribute(
      "href",
      "/downloads/PeerOnQ-0.9.30-unsigned-development-arm64.msi",
    );
    expect(screen.queryByRole("link", { name: "Download PeerOnQ for Windows x64" })).not.toBeInTheDocument();
  });

  it('uses only the canonical PeerOnQ download availability flag', () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION", "0.9.30");
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER", "unsigned-development");
    window.history.replaceState(null, "", "/downloads");
    const { unmount } = render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toBeInTheDocument();

    unmount();
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "false");
    render(<App />);

    expect(screen.queryByRole("link", { name: "Download PeerOnQ for Windows x64" })).not.toBeInTheDocument();
  });

  it("exposes only the explicitly embedded x64 controlled pilot", () => {
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL", "/downloads/PeerOnQ-Windows-x64.msi");
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION", "0.5.1");
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT", "true");
    vi.stubEnv("VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL", "https://download.peeronq.com");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toHaveAttribute(
      "href",
      "/downloads/PeerOnQ-Windows-x64.msi?v=0.5.1",
    );
    expect(screen.queryByRole("link", { name: "Download PeerOnQ for Windows ARM64" })).not.toBeInTheDocument();
    expect(screen.queryByText(/Detected:/)).not.toBeInTheDocument();
    expect(screen.queryByText("Unsigned controlled-test package available.")).not.toBeInTheDocument();
  });

  it("links a website patch to the canonical server client without claiming its version or signature", () => {
    vi.stubEnv("VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL", "https://download.peeronq.example");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toHaveAttribute(
      "href",
      "/downloads/PeerOnQ-Windows-x64.msi",
    );
    expect(screen.queryByRole("link", { name: "Download PeerOnQ for Windows ARM64" })).not.toBeInTheDocument();
    expect(screen.queryByText("Installer metadata is owned by the server release.")).not.toBeInTheDocument();
    expect(screen.queryByText(/signed embedded release|unsigned controlled pilot|latest signed stable release/i)).not.toBeInTheDocument();
  });

  it("labels an explicitly signed embedded Windows client without a pilot warning", () => {
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL", "/downloads/PeerOnQ-Windows-x64.msi");
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION", "0.5.1");
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT", "false");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toBeInTheDocument();
    expect(screen.queryByText("Unsigned controlled-test package available.")).not.toBeInTheDocument();
  });

  it("fails closed when embedded-client signature metadata is absent", () => {
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL", "/downloads/PeerOnQ-Windows-x64.msi");
    vi.stubEnv("VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION", "0.5.1");

    expect(() => render(<DownloadsPage />)).toThrow(
      "An embedded Windows client requires an explicit VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT flag.",
    );
  });

  it("routes production packages through the tracked download service", () => {
    vi.stubEnv("VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE", "true");
    vi.stubEnv("VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL", "https://download.peeronq.example");
    window.history.replaceState(null, "", "/downloads");
    render(<App />);

    expect(screen.getByRole("link", { name: "Download PeerOnQ for Windows x64" })).toHaveAttribute(
      "href",
      "https://download.peeronq.example/windows/latest?architecture=x64&channel=stable&source=website",
    );
    expect(screen.queryByRole("link", { name: "Download PeerOnQ for Windows ARM64" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "View SHA-256 checksums" })).not.toBeInTheDocument();
    expect(screen.queryByText("Latest signed stable release")).not.toBeInTheDocument();
  });

  it("does not ship the retired localStorage account-portal preview routes", () => {
    window.history.replaceState(null, "", "/app/devices");
    render(<App />);

    expect(screen.getByRole("heading", { name: "404" })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Account portal" })).not.toBeInTheDocument();
  });

  it("keeps sign-in and account portal links off the public website", () => {
    window.history.replaceState(null, "", "/");
    render(<App />);

    expect(screen.queryByRole("link", { name: "Sign in" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Account portal" })).not.toBeInTheDocument();
  });

  it("does not expose a billing, subscription, or entitlement portal surface", () => {
    window.history.replaceState(null, "", "/app/billing");
    render(<App />);

    expect(screen.getByRole("heading", { name: "404" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /billing/i })).not.toBeInTheDocument();
  });

  it("states the public open-source and no-activation model accurately", () => {
    window.history.replaceState(null, "", "/");
    render(<App />);

    expect(screen.getByText("Product strategy")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Built in the right order." })).toBeInTheDocument();
    expect(screen.getByText(/add macOS and mobile only when their complete clients are ready/i)).toBeInTheDocument();
    expect(screen.getAllByText("MIT licensed").length).toBeGreaterThan(0);
    expect(screen.getAllByText("No activation key").length).toBeGreaterThan(0);
  });

  it("renders desktop preview routes with the permanent banner and desktop sidebar only", () => {
    window.history.replaceState(null, "", "/desktop-preview/dashboard");
    render(<App />);

    expect(screen.getByText("Desktop application UI preview — this is not the production website.")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Remote Access" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /Preview User/i })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Visit public website" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open account portal" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open account portal" })).toHaveAttribute("href", "https://portal.peeronq.com");
    expect(screen.getByRole("link", { name: "Exit desktop preview" })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Public website" })).not.toBeInTheDocument();
  });

  it.each([
    ["/dashboard", "/desktop-preview/dashboard"],
    ["/remote-access", "/desktop-preview/dashboard"],
    ["/desktop-preview/remote-access", "/desktop-preview/dashboard"],
    ["/devices", "/desktop-preview/devices"],
    ["/sessions", "/desktop-preview/sessions"],
    ["/files", "/desktop-preview/file-transfer"],
    ["/file-transfer", "/desktop-preview/file-transfer"],
    ["/address-book", "/desktop-preview/address-book"],
    ["/settings", "/desktop-preview/settings"],
  ])("redirects legacy route %s to %s", async (legacyPath, destination) => {
    window.history.replaceState(null, "", legacyPath);
    render(<App />);

    await waitFor(() => expect(window.location.pathname).toBe(destination));
  });
});

describe("Open App", () => {
  it("attempts the native protocol and shows the required fallback", () => {
    vi.useFakeTimers();
    let attemptedHref = "";
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function () {
      attemptedHref = this.href;
    });

    render(<OpenAppButton />);
    fireEvent.click(screen.getByRole("button", { name: "Open App" }));

    expect(attemptedHref).toBe("peeronq://open");
    act(() => vi.advanceTimersByTime(1500));

    expect(screen.getByRole("dialog", { name: "PeerOnQ could not be opened" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Download" })).toHaveAttribute("href", "/downloads");
    expectDownloadsLinksToUseDocumentNavigation();
    expect(screen.getByRole("link", { name: /Installation help/i })).toHaveAttribute("href", "/help");
    expect(screen.getByRole("link", { name: /Open web account portal/i })).toHaveAttribute("href", "https://portal.peeronq.com");
  });
});
