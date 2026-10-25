import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

interface PublicPageHeroProps {
  eyebrow: string;
  title: ReactNode;
  description: string;
  actions?: ReactNode;
  children?: ReactNode;
  compact?: boolean;
}

export function PublicPageHero({ eyebrow, title, description, actions, children, compact = false }: PublicPageHeroProps) {
  return (
    <section className="marketing-hero overflow-hidden border-b border-marketing-line">
      <div className="marketing-grid" aria-hidden="true" />
      <div className={cn(
        "relative mx-auto grid max-w-7xl gap-12 px-5 sm:px-8 lg:px-10",
        compact ? "py-14 sm:py-16 lg:py-20" : "py-20 sm:py-24",
        children && "lg:grid-cols-[1fr_0.82fr] lg:items-center",
        children && !compact && "lg:py-28",
      )}>
        <div className="min-w-0 max-w-3xl">
          <p className="marketing-eyebrow">{eyebrow}</p>
          <h1 className="mt-6 text-balance text-4xl font-semibold leading-[1.04] tracking-[-0.045em] text-marketing-foreground sm:text-5xl lg:text-6xl">
            {title}
          </h1>
          <p className="mt-6 max-w-2xl text-pretty text-lg leading-8 text-marketing-muted sm:text-xl">
            {description}
          </p>
          {actions && <div className="mt-9 flex flex-col gap-3 sm:flex-row">{actions}</div>}
        </div>
        {children}
      </div>
    </section>
  );
}
