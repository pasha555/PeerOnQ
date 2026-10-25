import { cn } from "@/lib/utils";
import type { DeviceStatus, SessionState } from "@/types";

interface StatusBadgeProps {
  status: DeviceStatus | SessionState | string;
  className?: string;
  dot?: boolean;
}

const SUCCESS_STATUSES = new Set(["online", "connected"]);
const WARNING_STATUSES = new Set(["connecting", "reconnecting", "requesting", "awaiting-permission"]);
const BADGE_STYLES = {
  success: 'bg-success/15 text-success-foreground dark:text-success border-success/20',
  warning: 'bg-warning/15 text-warning-foreground dark:text-warning border-warning/20',
  neutral: 'bg-muted text-muted-foreground border-border',
} as const;
const DOT_STYLES = {
  success: 'bg-success',
  warning: 'bg-warning',
  neutral: 'bg-muted-foreground',
} as const;

function statusTone(status: string): keyof typeof BADGE_STYLES {
  if (SUCCESS_STATUSES.has(status)) return "success";
  if (WARNING_STATUSES.has(status)) return "warning";
  return "neutral";
}

export function StatusBadge({ status, className, dot = true }: StatusBadgeProps) {
  const tone = statusTone(status);

  return (
    <span
      role="status"
      className={cn(
        "inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium border",
        BADGE_STYLES[tone],
        className
      )}
    >
      {dot && <span className={cn("h-1.5 w-1.5 rounded-full", DOT_STYLES[tone])} />}
      {status.charAt(0).toUpperCase() + status.slice(1).replace('-', ' ')}
    </span>
  );
}
