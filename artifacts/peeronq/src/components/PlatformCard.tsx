import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { CheckCircle2, Download } from "lucide-react";
import { ReactNode } from "react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface PlatformCardProps {
  platform: string;
  icon: ReactNode;
  description?: string;
  version?: string;
  requirements?: string;
  active?: boolean;
  downloads?: ReadonlyArray<{
    label: string;
    href: string;
    fileName: string;
  }>;
  availabilityNote?: string;
  compact?: boolean;
  className?: string;
}

type PlatformDownload = NonNullable<PlatformCardProps["downloads"]>[number];

function PlatformAvailability({ active }: { active: boolean }) {
  return active ? (
    <span className="rounded-full border border-primary/20 bg-primary/10 px-3 py-1 text-xs font-semibold text-primary">
      Current platform
    </span>
  ) : (
    <span className="rounded-full border bg-muted px-3 py-1 text-xs font-semibold text-muted-foreground">
      Planned
    </span>
  );
}

function DownloadLinks({ platform, downloads }: { platform: string; downloads: readonly PlatformDownload[] }) {
  return (
    <div className="space-y-2" aria-label={`${platform} downloads`}>
      {downloads.map((download) => (
        <Button key={download.href} className="h-11 w-full" asChild>
          <a href={download.href} download={download.fileName}>
            <Download className="mr-2 h-4 w-4" aria-hidden="true" />
            {download.label}
          </a>
        </Button>
      ))}
    </div>
  );
}

function DisabledDownload({ active, platform, note, compact = false }: { active: boolean; platform: string; note?: string; compact?: boolean }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="w-full">
          <Button
            aria-label={active ? `${platform} build required` : `${platform} app is not available yet`}
            className={cn("w-full", compact ? "h-10" : "h-11")}
            disabled
            variant="secondary"
          >
            <Download className="mr-2 h-4 w-4" aria-hidden="true" />
            {active ? "Build required" : "Not yet available"}
          </Button>
        </span>
      </TooltipTrigger>
      <TooltipContent>
        <p>{note ?? "This platform is not available yet."}</p>
      </TooltipContent>
    </Tooltip>
  );
}

export function PlatformCard({
  platform,
  icon,
  description,
  version,
  requirements,
  active = false,
  downloads = [],
  availabilityNote,
  compact = false,
  className,
}: PlatformCardProps) {
  if (compact && !active) {
    return (
      <article className={cn("group rounded-2xl border bg-card/70 p-4 transition-colors hover:border-primary/30 hover:bg-card", className)}>
        <div className="flex items-center justify-between gap-3">
          <span className="flex h-10 w-10 items-center justify-center rounded-xl border bg-secondary/70 text-foreground transition-colors group-hover:border-primary/20 group-hover:text-primary">
            {icon}
          </span>
          <PlatformAvailability active={false} />
        </div>
        <h3 className="mt-5 text-base font-semibold tracking-tight">{platform}</h3>
        {availabilityNote && <p className="mt-2 text-sm leading-5 text-muted-foreground">{availabilityNote}</p>}
      </article>
    );
  }

  return (
    <article className={cn("marketing-card relative overflow-hidden rounded-[1.75rem] border-primary/30 bg-gradient-to-br from-primary/[0.08] via-card to-card p-1 ring-1 ring-primary/10", className)}>
      <div className="pointer-events-none absolute -right-24 -top-24 h-64 w-64 rounded-full bg-primary/10 blur-3xl" aria-hidden="true" />
      <div className="relative grid gap-5 p-4 sm:p-6 lg:grid-cols-[minmax(0,1fr)_22rem] lg:items-stretch">
        <div className="min-w-0 p-1 sm:p-2">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div className="flex items-center gap-4">
              <span className="flex h-12 w-12 items-center justify-center rounded-2xl bg-primary/15 text-primary ring-1 ring-primary/20">
                {icon}
              </span>
              <div>
                <p className="text-xs font-semibold uppercase tracking-[0.16em] text-primary">Available now</p>
                <h3 className="mt-1 text-2xl font-semibold tracking-tight">{platform}</h3>
              </div>
            </div>
            <PlatformAvailability active={active} />
          </div>

          {description && <p className="mt-5 max-w-2xl leading-7 text-muted-foreground">{description}</p>}

          <dl className="mt-6 grid gap-3 sm:grid-cols-2">
            {version && (
              <div className="rounded-xl border bg-background/65 px-4 py-3">
                <dt className="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Release</dt>
                <dd className="mt-1 text-sm font-medium">{version}</dd>
              </div>
            )}
            {requirements && (
              <div className="rounded-xl border bg-background/65 px-4 py-3">
                <dt className="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">System</dt>
                <dd className="mt-1 text-sm font-medium">{requirements}</dd>
              </div>
            )}
          </dl>

          {availabilityNote && (
            <p className="mt-4 flex items-start gap-2 text-sm leading-6 text-muted-foreground">
              <CheckCircle2 className="mt-1 h-4 w-4 shrink-0 text-primary" aria-hidden="true" />
              {availabilityNote}
            </p>
          )}
        </div>

        <div className="flex flex-col justify-center rounded-2xl border bg-background/80 p-4 shadow-sm sm:p-5">
          <p className="text-sm font-semibold">Choose a package</p>
          <p className="mt-1 text-xs leading-5 text-muted-foreground">Select the architecture used by this Windows device.</p>
          <div className="mt-4">
            {downloads.length > 0
              ? <DownloadLinks platform={platform} downloads={downloads} />
              : <DisabledDownload active={active} platform={platform} note={availabilityNote} />}
          </div>
          <p className="mt-4 text-xs leading-5 text-muted-foreground">Verify the published SHA-256 checksum before installation.</p>
        </div>
      </div>
    </article>
  );
}
