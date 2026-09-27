import { Link } from "wouter";
import {
  ArrowRight,
  CheckCircle2,
  Download,
  Eye,
  FileCheck2,
  Fingerprint,
  KeyRound,
  LockKeyhole,
  Network,
  ScrollText,
  ServerCog,
  ShieldCheck,
  UserCheck,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { MarketingCta, PublicPageHero, SectionHeading } from "@/components/PublicMarketing";

const principles = [
  { icon: UserCheck, title: "Consent before capability", text: "Incoming sessions show the requested access and require visible approval by default." },
  { icon: Eye, title: "No invisible session", text: "The local client keeps active screen sharing and remote input visible while they are in use." },
  { icon: KeyRound, title: "Trust is separate", text: "Saving a contact does not create trust; unattended access remains a distinct, default-off workflow." },
  { icon: ScrollText, title: "Evidence without content", text: "Security history records sanitized outcomes and integrity evidence, not screen, clipboard, or file content." },
];

const layers = [
  {
    icon: Fingerprint,
    label: "Identity",
    title: "Proof-bound device identity",
    text: "Each installed client owns a distinct protected device identity. Authentication challenges bind registration to the installation key rather than trusting a user-entered label.",
  },
  {
    icon: LockKeyhole,
    label: "Handshake",
    title: "Hybrid cryptographic agreement",
    text: "The current Windows architecture combines ML-KEM-768 with X25519 for key agreement and ML-DSA-65 with Ed25519 for identity signatures.",
  },
  {
    icon: ShieldCheck,
    label: "Session traffic",
    title: "Separated application-layer keys",
    text: "Encoded video and collaboration records are protected with AES-256-GCM using distinct HKDF-derived keys by direction, channel, transfer, and epoch.",
  },
  {
    icon: Network,
    label: "Connectivity",
    title: "Signaling cannot grant control",
    text: "Authenticated signaling coordinates connection state. Screen, input, clipboard, and file content remain on the separately negotiated WebRTC session path.",
  },
];

const safeguards = [
  ["Connection scope", "View, input, clipboard, transfer, and unattended access remain individually constrained."],
  ["Reconnect", "Resume credentials are short-lived, rotating, and bounded; expiry fails closed."],
  ["Local secrets", "Sensitive Windows collaboration state is protected with DPAPI rather than plaintext storage."],
  ["Updates", "Manifest signature, exact hash, file size, Authenticode trust, and publisher identity are checked before installation."],
  ["Diagnostics", "Uploads require consent and use bounded, sanitized packages without session content or credentials."],
  ["Administration", "Privileged operator actions are policy-controlled, CSRF-protected where applicable, and audited."],
];

export function PublicSecurityPage() {
  return (
    <div>
      <PublicPageHero
        eyebrow="Security architecture"
        title={<>Remote access with <span className="text-primary">human-visible boundaries.</span></>}
        description="PeerOnQ treats permission, identity, transport, trust, updates, and operational access as one security system—not a collection of hidden defaults."
        actions={
          <>
            <Button asChild size="lg" className="h-12 px-6 text-base"><a href="/downloads"><Download className="mr-2 h-4 w-4" aria-hidden="true" />Download</a></Button>
            <Button asChild size="lg" variant="outline" className="h-12 border-marketing-line bg-marketing-panel/60 px-6 text-base text-marketing-foreground hover:bg-marketing-panel hover:text-marketing-foreground">
              <Link href="/features">Explore features<ArrowRight className="ml-2 h-4 w-4" aria-hidden="true" /></Link>
            </Button>
          </>
        }
      >
        <div className="rounded-[1.75rem] border border-marketing-line bg-marketing-panel/85 p-6 backdrop-blur sm:p-7">
          <div className="flex items-center gap-3">
            <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-primary/15 text-primary"><ShieldCheck className="h-5 w-5" aria-hidden="true" /></span>
            <div><p className="font-semibold text-marketing-foreground">Session security</p><p className="text-sm text-marketing-muted">Layered from identity to audit</p></div>
          </div>
          <div className="mt-7 grid gap-3">
            {["Device proof verified", "Permission scope approved", "Hybrid post-quantum handshake complete", "Session remains visible"].map((item) => (
              <div key={item} className="flex items-center gap-3 rounded-xl border border-marketing-line bg-marketing-hero/45 px-4 py-3 text-sm font-medium text-marketing-foreground">
                <CheckCircle2 className="h-4 w-4 shrink-0 text-primary" aria-hidden="true" />{item}
              </div>
            ))}
          </div>
        </div>
      </PublicPageHero>

      <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto max-w-7xl">
          <SectionHeading centered eyebrow="Security starts with clarity" title="Four principles the interface cannot hide" description="The safest remote session is one every participant can understand and stop." />
          <div className="mt-12 grid gap-5 md:grid-cols-2 lg:grid-cols-4">
            {principles.map((principle) => (
              <article key={principle.title} className="marketing-card rounded-2xl p-6">
                <principle.icon className="h-6 w-6 text-primary" aria-hidden="true" />
                <h3 className="mt-6 text-lg font-semibold">{principle.title}</h3>
                <p className="mt-3 text-sm leading-6 text-muted-foreground">{principle.text}</p>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="border-y bg-secondary/45 px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto max-w-7xl">
          <SectionHeading eyebrow="Defense in depth" title="Protection across the complete session lifecycle" description="Every layer has one job, a defined boundary, and a fail-closed path when required proof is unavailable." />
          <div className="mt-12 grid gap-5 lg:grid-cols-2">
            {layers.map((layer, index) => (
              <article key={layer.label} className="rounded-2xl border bg-card p-6 sm:p-7">
                <div className="flex items-center justify-between gap-4">
                  <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-primary/10 text-primary"><layer.icon className="h-5 w-5" aria-hidden="true" /></span>
                  <span className="font-mono text-xs font-semibold text-muted-foreground">0{index + 1}</span>
                </div>
                <p className="mt-7 text-xs font-semibold uppercase tracking-[0.16em] text-primary">{layer.label}</p>
                <h3 className="mt-3 text-xl font-semibold">{layer.title}</h3>
                <p className="mt-3 leading-7 text-muted-foreground">{layer.text}</p>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto grid max-w-7xl gap-12 lg:grid-cols-[0.78fr_1.22fr] lg:items-start">
          <SectionHeading eyebrow="Operational safeguards" title="Security continues after the handshake." description="Trustworthy remote access also depends on safe updates, scoped administration, private diagnostics, and useful evidence." />
          <div className="overflow-hidden rounded-2xl border bg-card">
            {safeguards.map(([title, text], index) => (
              <article key={title} className={`grid gap-2 p-5 sm:grid-cols-[10rem_1fr] sm:gap-6 sm:p-6 ${index > 0 ? "border-t" : ""}`}>
                <h3 className="font-semibold">{title}</h3>
                <p className="text-sm leading-6 text-muted-foreground">{text}</p>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="border-y bg-card px-5 py-20 sm:px-8 sm:py-24 lg:px-10">
        <div className="mx-auto grid max-w-7xl gap-8 lg:grid-cols-3">
          {[
            [FileCheck2, "Open core", "The MIT-licensed client can be inspected and self-hosted; security does not depend on obscurity."],
            [ServerCog, "No browser remote control", "This website and the account portal do not capture screens or start remote-control sessions."],
            [LockKeyhole, "No absolute claims", "Security review results are published only after the relevant assessment is complete."],
          ].map(([Icon, title, text]) => {
            const FactIcon = Icon as typeof FileCheck2;
            return (
              <article key={title as string} className="rounded-2xl border bg-background p-6">
                <FactIcon className="h-6 w-6 text-primary" aria-hidden="true" />
                <h3 className="mt-5 font-semibold">{title as string}</h3>
                <p className="mt-3 text-sm leading-6 text-muted-foreground">{text as string}</p>
              </article>
            );
          })}
        </div>
      </section>

      <MarketingCta title="Security you can see before you connect." description="Choose the current client build, review its release status, and keep remote access inside the installed application." secondaryHref="/features" secondaryLabel="Explore capabilities" />
    </div>
  );
}
