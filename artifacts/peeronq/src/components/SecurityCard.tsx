import { ReactNode } from "react";
import { cn } from "@/lib/utils";

interface SecurityCardProps {
  title: string;
  description: string;
  icon: ReactNode;
  status?: ReactNode;
  planned?: boolean;
  className?: string;
}

export function SecurityCard({ title, description, icon, status, planned, className }: SecurityCardProps) {
  return (
    <div className={cn("p-5 rounded-xl border bg-card hover:bg-secondary/20 transition-colors flex items-start gap-4", className)}>
      <div className="mt-0.5 p-2 bg-primary/10 rounded-lg text-primary">
        {icon}
      </div>
      <div className="flex-1">
        <div className="flex items-center justify-between mb-1 gap-4">
          <h4 className="text-card-title text-foreground">{title}</h4>
          {planned && (
            <span className="text-[10px] font-medium px-2 py-0.5 rounded bg-muted text-muted-foreground whitespace-nowrap">
              Planned
            </span>
          )}
          {status && !planned && <div>{status}</div>}
        </div>
        <p className="text-secondary-body text-muted-foreground">{description}</p>
      </div>
    </div>
  );
}
