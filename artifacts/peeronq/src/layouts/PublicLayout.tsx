import { useState, type ReactNode } from "react";
import { Menu, X } from "lucide-react";
import { Link } from "wouter";
import { Button } from "@/components/ui/button";

interface PublicLayoutProps {
  children: ReactNode;
}

const publicLinks = [
  { label: "Product", href: "/#product" },
  { label: "Security", href: "/#security" },
  { label: "Strategy", href: "/#strategy" },
];

export function PublicLayout({ children }: PublicLayoutProps) {
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  return (
    <div className="min-h-screen bg-background text-foreground">
      <a href="#main-content" className="sr-only z-[100] rounded-md bg-primary px-4 py-3 font-semibold text-primary-foreground focus:not-sr-only focus:fixed focus:left-4 focus:top-4">
        Skip to main content
      </a>
      <header className="sticky top-0 z-50 border-b bg-background/90 backdrop-blur-xl">
        <div className="mx-auto grid h-20 max-w-7xl grid-cols-[1fr_auto_1fr] items-center px-5 sm:px-8 lg:px-10">
          <Link href="/" className="col-start-1 row-start-1 flex items-center gap-2 justify-self-start" aria-label="PeerOnQ home">
            <img src="/brand/peeronq-mark.svg" alt="" className="block h-7 w-7 dark:hidden" />
            <img src="/brand/peeronq-mark-light.svg" alt="" className="hidden h-7 w-7 dark:block" />
            <img src="/brand/peeronq-wordmark.svg" alt="PeerOnQ" className="block h-[1.15rem] dark:hidden" />
            <img src="/brand/peeronq-wordmark-light.svg" alt="PeerOnQ" className="hidden h-[1.15rem] dark:block" />
          </Link>

          <nav aria-label="Public website" className="col-start-2 row-start-1 hidden items-center gap-1 md:flex">
            {publicLinks.map((item) => (
              <a key={item.href} href={item.href} className="rounded-lg px-4 py-2.5 text-sm font-medium text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground">
                {item.label}
              </a>
            ))}
          </nav>

          <div className="col-start-3 row-start-1 flex items-center gap-2 justify-self-end">
            <Button type="button" variant="ghost" size="icon" className="md:hidden" aria-label={mobileMenuOpen ? "Close menu" : "Open menu"} aria-expanded={mobileMenuOpen} onClick={() => setMobileMenuOpen((open) => !open)}>
              {mobileMenuOpen ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
            </Button>
          </div>
        </div>
        {mobileMenuOpen && (
          <nav aria-label="Mobile public website" className="border-t bg-background px-5 py-4 md:hidden">
            <div className="mx-auto grid max-w-7xl gap-1">
              {publicLinks.map((item) => (
                <a key={item.href} href={item.href} onClick={() => setMobileMenuOpen(false)} className="rounded-lg px-3 py-3 font-medium hover:bg-secondary">{item.label}</a>
              ))}
            </div>
          </nav>
        )}
      </header>

      <main id="main-content">{children}</main>

      <footer className="border-t bg-card">
        <div className="mx-auto flex max-w-7xl flex-col gap-8 px-5 py-12 sm:px-8 md:flex-row md:items-end md:justify-between lg:px-10">
          <div className="max-w-md">
            <Link href="/" className="inline-flex items-center gap-2" aria-label="PeerOnQ home">
              <img src="/brand/peeronq-lockup.svg" alt="PeerOnQ" className="block h-7 dark:hidden" />
              <img src="/brand/peeronq-lockup-light.svg" alt="PeerOnQ" className="hidden h-7 dark:block" />
            </Link>
            <p className="mt-5 text-sm leading-6 text-muted-foreground">Permission-first remote access with visible controls, secure transport, and an open-source core.</p>
          </div>
          <div className="flex flex-wrap gap-x-6 gap-y-3 text-sm text-muted-foreground">
            <Link href="/privacy" className="hover:text-foreground">Privacy</Link>
            <Link href="/terms" className="hover:text-foreground">Terms</Link>
            <span>MIT licensed</span>
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
