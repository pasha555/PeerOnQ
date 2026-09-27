import type { ReactNode } from "react";
import { ArrowRight, Download } from "lucide-react";
import { Link } from "wouter";
import { Button } from "@/components/ui/button";
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

interface SectionHeadingProps {
  eyebrow: string;
  title: string;
  description?: string;
  centered?: boolean;
}

export function SectionHeading({ eyebrow, title, description, centered = false }: SectionHeadingProps) {
  return (
    <div className={cn("max-w-3xl", centered && "mx-auto text-center")}>
      <p className="text-sm font-semibold uppercase tracking-[0.18em] text-primary">{eyebrow}</p>
      <h2 className="mt-4 text-balance text-3xl font-semibold tracking-[-0.035em] sm:text-4xl lg:text-5xl">{title}</h2>
      {description && <p className="mt-5 text-pretty text-lg leading-8 text-muted-foreground">{description}</p>}
    </div>
  );
}

interface MarketingCtaProps {
  title: string;
  description: string;
  secondaryHref?: string;
  secondaryLabel?: string;
}

export function MarketingCta({ title, description, secondaryHref = "/features", secondaryLabel = "Explore features" }: MarketingCtaProps) {
  return (
    <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10">
      <div className="marketing-cta mx-auto max-w-7xl overflow-hidden rounded-[2rem] border border-marketing-line px-6 py-12 text-center sm:px-12 sm:py-16">
        <p className="marketing-eyebrow justify-center">Ready when you are</p>
        <h2 className="mx-auto mt-5 max-w-3xl text-balance text-3xl font-semibold tracking-[-0.035em] text-marketing-foreground sm:text-5xl">{title}</h2>
        <p className="mx-auto mt-5 max-w-2xl text-pretty text-lg leading-8 text-marketing-muted">{description}</p>
        <div className="mt-8 flex flex-col justify-center gap-3 sm:flex-row">
          <Button asChild size="lg" className="h-12 px-6 text-base">
            <a href="/downloads"><Download className="mr-2 h-4 w-4" aria-hidden="true" />Download</a>
          </Button>
          <Button asChild size="lg" variant="outline" className="h-12 border-marketing-line bg-marketing-panel/60 px-6 text-base text-marketing-foreground hover:bg-marketing-panel hover:text-marketing-foreground">
            <Link href={secondaryHref}>{secondaryLabel}<ArrowRight className="ml-2 h-4 w-4" aria-hidden="true" /></Link>
          </Button>
        </div>
      </div>
    </section>
  );
}
