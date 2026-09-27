import { CloudCog, ExternalLink } from "lucide-react";

interface DevelopmentLink {
  label: string;
  href: string;
}

function normalizeDevelopmentUrl(value: string | undefined): string | null {
  if (!value?.trim()) return null;

  try {
    const url = new URL(value.trim());
    const hostname = url.hostname.toLowerCase();
    const loopback = hostname === "localhost" || hostname === "127.0.0.1" || hostname.endsWith(".localhost");
    if (url.protocol !== "https:" && !(url.protocol === "http:" && loopback)) return null;
    return url.toString();
  } catch {
    return null;
  }
}

export function Phase6DevToolbar() {
  if (!import.meta.env.DEV) return null;

  const configuredLinks: Array<[string, string | undefined]> = [
    ["Admin console", import.meta.env.VITE_PEERONQ_ADMIN_PANEL_URL],
    ["Cloud health", import.meta.env.VITE_PEERONQ_CLOUD_HEALTH_URL],
    ["Grafana", import.meta.env.VITE_PEERONQ_GRAFANA_URL],
    ["Prometheus", import.meta.env.VITE_PEERONQ_PROMETHEUS_URL],
  ];
  const links: DevelopmentLink[] = configuredLinks.flatMap(([label, value]) => {
    const href = normalizeDevelopmentUrl(value);
    return href ? [{ label, href }] : [];
  });

  if (links.length === 0) return null;

  return (
    <aside className="border-b border-primary/25 bg-primary/10 text-foreground" aria-label="Phase 6 development tools">
      <div className="mx-auto flex min-h-11 max-w-7xl flex-wrap items-center justify-between gap-2 px-6 py-2 text-sm">
        <span className="flex items-center gap-2 font-medium">
          <CloudCog className="h-4 w-4 text-primary" aria-hidden="true" />
          Phase 6 development stack
        </span>
        <nav className="flex flex-wrap items-center gap-x-4 gap-y-2" aria-label="Phase 6 services">
          {links.map((link) => (
            <a
              key={link.label}
              href={link.href}
              target="_blank"
              rel="noreferrer"
              className="inline-flex min-h-8 items-center gap-1 rounded-md px-2 font-medium text-primary transition-colors hover:bg-primary/10 hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {link.label}
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
            </a>
          ))}
        </nav>
      </div>
    </aside>
  );
}
