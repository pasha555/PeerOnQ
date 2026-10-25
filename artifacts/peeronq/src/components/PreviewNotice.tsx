import { AlertTriangle, Info } from "lucide-react";
import { cn } from "@/lib/utils";

interface PreviewNoticeProps {
  title?: string;
  description: string;
  variant?: 'warning' | 'info';
  className?: string;
}

export function PreviewNotice({ title, description, variant = 'warning', className }: PreviewNoticeProps) {
  return (
    <div className={cn(
      "flex items-start gap-3 p-4 rounded-lg border",
      variant === 'warning' ? "bg-warning/10 border-warning/20 text-warning-foreground dark:text-warning" : "bg-primary/10 border-primary/20 text-primary-foreground dark:text-primary",
      className
    )}>
      {variant === 'warning' ? <AlertTriangle className="h-5 w-5 shrink-0" /> : <Info className="h-5 w-5 shrink-0" />}
      <div>
        {title && <h4 className="text-sm font-semibold mb-1">{title}</h4>}
        <p className="text-sm opacity-90">{description}</p>
      </div>
    </div>
  );
}
