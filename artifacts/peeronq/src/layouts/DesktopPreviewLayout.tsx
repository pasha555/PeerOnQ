import { useState, type ReactNode } from "react";
import { useLocation } from "wouter";
import { ErrorBoundary } from "@/components/ErrorBoundary";
import { Phase6DevToolbar } from "@/components/Phase6DevToolbar";
import { Sidebar } from "@/components/Sidebar";
import { Topbar } from "@/components/Topbar";

interface DesktopPreviewLayoutProps {
  children: ReactNode;
}

const pageTitles: Record<string, string> = {
  "/desktop-preview": "Dashboard",
  "/desktop-preview/dashboard": "Dashboard",
  "/desktop-preview/devices": "Devices",
  "/desktop-preview/sessions": "Sessions",
  "/desktop-preview/file-transfer": "File Transfer",
  "/desktop-preview/address-book": "Address Book",
  "/desktop-preview/security": "Security",
  "/desktop-preview/settings": "Settings",
};

export function DesktopPreviewLayout({ children }: DesktopPreviewLayoutProps) {
  const [collapsed, setCollapsed] = useState(false);
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const [location] = useLocation();

  return (
    <div className="flex h-[100dvh] w-full flex-col overflow-hidden bg-background">
      <Phase6DevToolbar />
      <div className="flex min-h-0 w-full flex-1 overflow-hidden">
        {mobileMenuOpen && (
          <button
            type="button"
            className="fixed inset-0 z-40 bg-foreground/50 backdrop-blur-sm md:hidden"
            onClick={() => setMobileMenuOpen(false)}
            aria-label="Close desktop preview menu"
          />
        )}

        <div className={`fixed inset-y-0 left-0 z-50 transform transition-transform duration-300 ease-in-out md:relative md:translate-x-0 ${mobileMenuOpen ? "translate-x-0" : "-translate-x-full"}`}>
          <Sidebar
            collapsed={collapsed}
            onToggle={() => setCollapsed((value) => !value)}
            mobile={mobileMenuOpen}
            onNavigate={() => setMobileMenuOpen(false)}
          />
        </div>

        <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
          <Topbar onMenuClick={() => setMobileMenuOpen(true)} title={pageTitles[location] ?? "Desktop Preview"} />
          <div role="status" className="border-b border-warning/30 bg-warning/10 px-4 py-2 text-center text-sm font-medium text-warning">
            Desktop application UI preview — this is not the production website.
          </div>
          <main className="flex-1 overflow-y-auto p-4 md:p-8">
            <div className="mx-auto flex h-full max-w-6xl flex-col">
              <ErrorBoundary>{children}</ErrorBoundary>
            </div>
          </main>
        </div>
      </div>
    </div>
  );
}
