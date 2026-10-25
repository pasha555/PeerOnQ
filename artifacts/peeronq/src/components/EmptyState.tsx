import { ReactNode } from "react";
import { cn } from "@/lib/utils";

interface EmptyStateProps {
  icon?: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}

export function EmptyState({ icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div className={cn("flex flex-col items-center justify-center p-12 text-center rounded-xl border border-dashed bg-card/50", className)}>
      {icon && <div className="mb-4 text-muted-foreground">{icon}</div>}
      <h3 className="text-card-title text-foreground mb-1">{title}</h3>
      {description && <p className="text-secondary-body text-muted-foreground max-w-sm mb-6">{description}</p>}
      {action && <div>{action}</div>}
    </div>
  );
}
