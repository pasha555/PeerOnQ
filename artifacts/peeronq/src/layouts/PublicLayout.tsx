import { useEffect, useRef, useState, type ReactNode } from "react";
import { ArrowUpRight, Github, Menu, X } from "lucide-react";
import { Link } from "wouter";
import { Button } from "@/components/ui/button";
import { getAccountPortalUrl } from "@/lib/accountPortal";

interface PublicLayoutProps {
  children: ReactNode;
}

const publicLinks = [
  { label: "Product", href: "/#product" },
  { label: "Security", href: "/#security" },
  { label: "Open source", href: "/#open-source" },
];

export function PublicLayout({ children }: PublicLayoutProps) {
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const menuButton = useRef<HTMLButtonElement>(null);
  const portalUrl = getAccountPortalUrl();
  const navigation = [...publicLinks, { label: "Portal", href: portalUrl }];
  useEffect(() => {
    if (!mobileMenuOpen) return;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setMobileMenuOpen(false);
        menuButton.current?.focus();
      }
    };
    document.addEventListener("keydown", closeOnEscape);
    return () => document.removeEventListener("keydown", closeOnEscape);
  }, [mobileMenuOpen]);

  return (
    <div className="public-site min-h-screen bg-background text-foreground">
      <a href="#main-content" className="sr-only z-[100] rounded-md bg-primary px-4 py-3 font-semibold text-primary-foreground focus:not-sr-only focus:fixed focus:left-4 focus:top-4">
        Skip to main content
      </a>
      <header className="sticky top-0 z-50 border-b bg-background/90 backdrop-blur-xl">
        <div className="public-container grid h-20 grid-cols-[1fr_auto_1fr] items-center gap-4">
          <Link href="/" className="col-start-1 row-start-1 flex min-h-11 items-center gap-2 justify-self-start" aria-label="PeerOnQ home">
            <img src="/brand/peeronq-mark.svg" alt="" className="block h-7 w-7 dark:hidden" />
            <img src="/brand/peeronq-mark-light.svg" alt="" className="hidden h-7 w-7 dark:block" />
            <img src="/brand/peeronq-wordmark.svg" alt="PeerOnQ" className="block h-[1.15rem] dark:hidden" />
            <img src="/brand/peeronq-wordmark-light.svg" alt="PeerOnQ" className="hidden h-[1.15rem] dark:block" />
          </Link>

          <nav aria-label="Public website" className="col-start-2 row-start-1 hidden items-center gap-1 lg:flex">
            {navigation.map((item) => (
              <a key={item.href} href={item.href} className="inline-flex min-h-11 items-center rounded-lg px-4 py-2.5 text-sm font-medium text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground">
                {item.label}
              </a>
            ))}
          </nav>

          <div className="col-start-3 row-start-1 flex items-center gap-2 justify-self-end">
            <a href={portalUrl} className="public-button public-button-secondary hidden min-[400px]:inline-flex">Sign in<ArrowUpRight className="h-3.5 w-3.5" aria-hidden="true" /></a>
            <Button ref={menuButton} type="button" variant="ghost" size="icon" className="h-11 w-11 lg:hidden" aria-label={mobileMenuOpen ? "Close menu" : "Open menu"} aria-expanded={mobileMenuOpen} aria-controls={mobileMenuOpen ? "public-mobile-navigation" : undefined} onClick={() => setMobileMenuOpen((open) => !open)}>
              {mobileMenuOpen ? <X className="h-5 w-5" aria-hidden="true" /> : <Menu className="h-5 w-5" aria-hidden="true" />}
            </Button>
          </div>
        </div>
        {mobileMenuOpen && (
          <nav id="public-mobile-navigation" aria-label="Mobile public website" className="border-t bg-background px-5 py-4 lg:hidden">
            <div className="mx-auto grid max-w-7xl gap-1">
              {navigation.map((item) => (
                <a key={item.href} href={item.href} onClick={() => setMobileMenuOpen(false)} className="rounded-lg px-3 py-3 font-medium hover:bg-secondary">{item.label}</a>
              ))}
              <a href={portalUrl} className="public-button public-button-primary mt-2" onClick={() => setMobileMenuOpen(false)}>Sign in to PeerOnQ<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a>
            </div>
          </nav>
        )}
      </header>

      <main id="main-content">{children}</main>

      <footer className="border-t bg-card">
        <div className="public-container grid gap-10 py-14 md:grid-cols-[1.5fr_1fr_1fr]">
          <div className="max-w-md">
            <Link href="/" className="inline-flex min-h-11 items-center gap-2" aria-label="PeerOnQ home">
              <img src="/brand/peeronq-lockup.svg" alt="PeerOnQ" className="block h-7 dark:hidden" />
              <img src="/brand/peeronq-lockup-light.svg" alt="PeerOnQ" className="hidden h-7 dark:block" />
            </Link>
            <p className="mt-5 max-w-xs text-sm leading-7 text-muted-foreground">Remote access with clear permissions and source you can inspect. Your devices, on your terms.</p>
            <a href="https://github.com/pasha555/PeerOnQ" className="mt-4 inline-flex min-h-11 items-center gap-2 text-sm font-medium hover:text-primary"><Github className="h-4 w-4" aria-hidden="true" />PeerOnQ on GitHub<ArrowUpRight className="h-3.5 w-3.5" aria-hidden="true" /></a>
          </div>
          <div className="flex flex-col items-start gap-1 text-sm text-muted-foreground">
            <p className="mb-3 font-semibold text-foreground">Product</p>
            <a href="/#product" className="inline-flex min-h-11 items-center py-2 hover:text-primary">Features</a>
            <a href="/#download" className="inline-flex min-h-11 items-center py-2 hover:text-primary">Downloads</a>
            <a href={portalUrl} className="inline-flex min-h-11 items-center py-2 hover:text-primary">Account portal</a>
            <a href="/#help" className="inline-flex min-h-11 items-center py-2 hover:text-primary">Help & FAQ</a>
          </div>
          <div className="flex flex-col items-start gap-1 text-sm text-muted-foreground">
            <p className="mb-3 font-semibold text-foreground">Open & transparent</p>
            <a href="https://github.com/pasha555/PeerOnQ/blob/main/LICENSE" className="inline-flex min-h-11 items-center py-2 hover:text-primary">MIT license</a>
            <a href="https://github.com/pasha555/PeerOnQ/issues" className="inline-flex min-h-11 items-center py-2 hover:text-primary">Community & issues</a>
            <Link href="/privacy" className="inline-flex min-h-11 items-center py-2 hover:text-primary">Privacy</Link>
            <Link href="/terms" className="inline-flex min-h-11 items-center py-2 hover:text-primary">Terms</Link>
          </div>
        </div>
        <div className="border-t">
          <div className="mx-auto flex max-w-7xl flex-col gap-2 px-5 py-6 text-xs text-muted-foreground sm:flex-row sm:items-center sm:justify-between sm:px-8 lg:px-10">
            <span>&copy; 2026 PeerOnQ. Open-source remote access.</span>
            <span>Built for clarity, consent, and control.</span>
          </div>
        </div>
      </footer>
    </div>
  );
}
