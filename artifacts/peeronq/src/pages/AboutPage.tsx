import { Link } from "wouter";
import { ArrowRight, Code2, Download, Eye, HeartHandshake, LockKeyhole, Network, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { MarketingCta, PublicPageHero, SectionHeading } from "@/components/PublicMarketing";

const values = [
  { icon: Eye, title: "Make access visible", text: "People should always understand when a session is active and what it can do." },
  { icon: HeartHandshake, title: "Earn permission", text: "Remote support works better when consent is specific, clear, and reversible." },
  { icon: Code2, title: "Build in the open", text: "An inspectable core and self-hosting path create durable technical trust." },
];

export function AboutPage() {
  return (
    <div>
      <PublicPageHero
        eyebrow="About PeerOnQ"
        title={<>Remote access should feel <span className="text-primary">modern and accountable.</span></>}
        description="PeerOnQ is being built to combine a polished remote-work experience with boundaries that remain understandable to the people on both sides of the connection."
        actions={
          <>
            <Button asChild size="lg" className="h-12 px-6 text-base"><a href="/downloads"><Download className="mr-2 h-4 w-4" aria-hidden="true" />Download</a></Button>
            <Button asChild size="lg" variant="outline" className="h-12 border-marketing-line bg-marketing-panel/60 px-6 text-base text-marketing-foreground hover:bg-marketing-panel hover:text-marketing-foreground">
              <Link href="/features">Explore the product<ArrowRight className="ml-2 h-4 w-4" aria-hidden="true" /></Link>
            </Button>
          </>
        }
      >
        <div className="rounded-[1.75rem] border border-marketing-line bg-marketing-panel/85 p-6 backdrop-blur sm:p-8">
          <p className="text-xs font-semibold uppercase tracking-[0.16em] text-primary">The premise</p>
          <blockquote className="mt-5 text-balance text-2xl font-semibold leading-9 text-marketing-foreground">“Powerful remote access does not need hidden control, forced activation, or an outdated experience.”</blockquote>
          <p className="mt-6 border-t border-marketing-line pt-5 text-sm leading-6 text-marketing-muted">PeerOnQ is an independent open-source project focused on permission-first remote connectivity.</p>
        </div>
      </PublicPageHero>

      <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto grid max-w-7xl gap-12 lg:grid-cols-[0.8fr_1.2fr] lg:items-center">
          <SectionHeading eyebrow="Our mission" title="Make distance operationally irrelevant—without making trust invisible." description="Remote-access software sits at a sensitive boundary: it can help someone solve a problem in minutes, but only when every participant can see and understand the access being granted." />
          <div className="grid gap-5 sm:grid-cols-3 lg:grid-cols-1">
            {values.map((value) => (
              <article key={value.title} className="marketing-card grid rounded-2xl p-6 lg:grid-cols-[auto_1fr] lg:items-start lg:gap-5">
                <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-primary/10 text-primary"><value.icon className="h-5 w-5" aria-hidden="true" /></span>
                <div><h3 className="mt-5 text-lg font-semibold lg:mt-0">{value.title}</h3><p className="mt-2 text-sm leading-6 text-muted-foreground">{value.text}</p></div>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="border-y bg-secondary/45 px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto max-w-7xl">
          <SectionHeading centered eyebrow="Product strategy" title="Win on trust, usability, and deployment freedom" description="PeerOnQ starts with a complete Windows experience, turns security into something people can see, and expands to each platform only after the client is ready to earn users' trust." />
          <div className="mt-12 grid gap-5 md:grid-cols-2">
            {[
              [Network, "01 / Complete the core workflow", "Make the Windows client fast and coherent across display streaming, permission-gated input, file transfer, collaboration, reconnect, diagnostics, and signed updates."],
              [LockKeyhole, "02 / Make security a product advantage", "Use visible consent and scoped permissions, then establish session keys through ML-KEM-768 plus X25519 and protect session traffic with AES-256-GCM."],
              [Code2, "03 / Give teams deployment freedom", "Support direct or authenticated relay connectivity, keep customer and operator surfaces separate, and preserve an inspectable open core with a self-hosting path."],
              [ShieldCheck, "04 / Expand by proof, not promises", "Develop the Linux viewer next, while macOS and mobile remain planned until their builds, security boundaries, and complete workflows are verified."],
            ].map(([Icon, title, text]) => {
              const StatusIcon = Icon as typeof Network;
              return (
                <article key={title as string} className="rounded-2xl border bg-card p-7">
                  <StatusIcon className="h-6 w-6 text-primary" aria-hidden="true" />
                  <h3 className="mt-6 text-xl font-semibold">{title as string}</h3>
                  <p className="mt-3 leading-7 text-muted-foreground">{text as string}</p>
                </article>
              );
            })}
          </div>
        </div>
      </section>

      <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto grid max-w-7xl gap-12 lg:grid-cols-2 lg:items-center">
          <div>
            <p className="text-sm font-semibold uppercase tracking-[0.18em] text-primary">Open by design</p>
            <h2 className="mt-4 text-balance text-3xl font-semibold tracking-[-0.035em] sm:text-5xl">Use it without asking a license server for permission.</h2>
          </div>
          <div className="rounded-2xl border bg-card p-7 sm:p-8">
            <p className="text-lg leading-8 text-muted-foreground">
              PeerOnQ is open-source software released under the MIT License. The installed client does not require a license key, product activation, a paid subscription, or an online entitlement check.
            </p>
            <p className="mt-5 leading-7 text-muted-foreground">Core LAN use can work without an account, and internet deployments can use a self-hosted PeerOnQ stack.</p>
          </div>
        </div>
      </section>

      <MarketingCta title="Help shape a better remote-access standard." description="Start with the current client, inspect the open core, and follow platform availability without inflated promises." secondaryHref="/security" secondaryLabel="Review security" />
    </div>
  );
}
