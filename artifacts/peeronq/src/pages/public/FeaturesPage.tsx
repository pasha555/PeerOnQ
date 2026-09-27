import { Link } from "wouter";
import {
  ArrowRight,
  BookUser,
  CheckCircle2,
  ClipboardCheck,
  Download,
  FileCheck2,
  FolderSync,
  Gauge,
  History,
  KeyRound,
  LockKeyhole,
  MonitorCog,
  MonitorSmartphone,
  MousePointer2,
  Network,
  RefreshCw,
  Send,
  ShieldCheck,
  SlidersHorizontal,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { MarketingCta, PublicPageHero, SectionHeading } from "@/components/PublicMarketing";

const featureGroups = [
  {
    eyebrow: "Connect",
    title: "A session that adapts without losing its boundaries.",
    description: "Choose the access mode, reach the device across real networks, and keep the approved scope intact through reconnects.",
    features: [
      { icon: MonitorSmartphone, title: "Remote view", text: "View the approved Windows display through the installed PeerOnQ client." },
      { icon: MousePointer2, title: "Full control", text: "Forward keyboard and pointer input only after the remote side approves control." },
      { icon: Network, title: "Direct + relay paths", text: "Prefer direct connectivity and fall back to authenticated TURN when required." },
      { icon: RefreshCw, title: "Bounded resume", text: "Recover safely from temporary network changes while expired sessions fail closed." },
    ],
  },
  {
    eyebrow: "Collaborate",
    title: "Move work forward, not just pixels.",
    description: "The core collaboration tools live inside the active session and remain visible to both endpoints.",
    features: [
      { icon: FolderSync, title: "File transfer", text: "Send selected files and folders with progress, pause, resume, and integrity validation." },
      { icon: ClipboardCheck, title: "Controlled clipboard", text: "Synchronize bounded plain text only when the active profile allows it." },
      { icon: Send, title: "Support invitations", text: "Create expiring, revocable support invitations that still require remote approval." },
      { icon: BookUser, title: "Address book", text: "Organize known devices locally without presenting saved contacts as automatically trusted." },
    ],
  },
  {
    eyebrow: "Operate",
    title: "Know what happened and stay in control afterward.",
    description: "Trust, diagnostics, session history, and updates are treated as product features—not hidden maintenance work.",
    features: [
      { icon: KeyRound, title: "Unattended access", text: "A separate, default-off workflow with protected credentials, recovery, and revocation." },
      { icon: History, title: "Session history", text: "Review local connection outcomes and security events without storing session content." },
      { icon: Gauge, title: "Live diagnostics", text: "See measured resolution, frame rate, bitrate, path, and connection health when available." },
      { icon: ShieldCheck, title: "Verified updates", text: "Validate release metadata, package integrity, and publisher trust before installation." },
    ],
  },
];

const modes = [
  { icon: MonitorCog, title: "View only", text: "Observe the remote display without granting input." },
  { icon: MousePointer2, title: "Full control", text: "Add keyboard and pointer input to an approved view session." },
  { icon: FileCheck2, title: "File transfer", text: "Transfer files without starting screen capture or remote input." },
  { icon: SlidersHorizontal, title: "Custom scope", text: "Keep clipboard, transfer, and input permissions explicit and separable." },
];

export function FeaturesPage() {
  return (
    <div>
      <PublicPageHero
        eyebrow="PeerOnQ capabilities"
        title={<>One remote-access platform. <span className="text-primary">Every permission in view.</span></>}
        description="Connect, control, transfer, support, and troubleshoot through a Windows client designed to make capability boundaries understandable."
        actions={
          <>
            <Button asChild size="lg" className="h-12 px-6 text-base"><a href="/downloads"><Download className="mr-2 h-4 w-4" aria-hidden="true" />Download</a></Button>
            <Button asChild size="lg" variant="outline" className="h-12 border-marketing-line bg-marketing-panel/60 px-6 text-base text-marketing-foreground hover:bg-marketing-panel hover:text-marketing-foreground">
              <Link href="/security">How security works<ArrowRight className="ml-2 h-4 w-4" aria-hidden="true" /></Link>
            </Button>
          </>
        }
      >
        <div className="grid gap-3 sm:grid-cols-2" aria-label="Remote access modes">
          {modes.map((mode) => (
            <article key={mode.title} className="rounded-2xl border border-marketing-line bg-marketing-panel/85 p-5 backdrop-blur">
              <mode.icon className="h-5 w-5 text-primary" aria-hidden="true" />
              <h2 className="mt-5 font-semibold text-marketing-foreground">{mode.title}</h2>
              <p className="mt-2 text-sm leading-6 text-marketing-muted">{mode.text}</p>
            </article>
          ))}
        </div>
      </PublicPageHero>

      <section className="border-b bg-card px-5 py-8 sm:px-8 lg:px-10">
        <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-center gap-x-8 gap-y-3 text-sm text-muted-foreground">
          {["Installed-client remote control", "Explicit capability requests", "Direct or relayed connectivity", "No browser control surface"].map((item) => (
            <span key={item} className="inline-flex items-center gap-2"><CheckCircle2 className="h-4 w-4 text-primary" aria-hidden="true" />{item}</span>
          ))}
        </div>
      </section>

      {featureGroups.map((group, groupIndex) => (
        <section key={group.eyebrow} className={`px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28 ${groupIndex % 2 === 1 ? "border-y bg-secondary/45" : ""}`}>
          <div className="mx-auto grid max-w-7xl gap-12 lg:grid-cols-[0.72fr_1.28fr]">
            <div className="lg:sticky lg:top-28 lg:self-start">
              <SectionHeading eyebrow={group.eyebrow} title={group.title} description={group.description} />
            </div>
            <div className="grid gap-5 sm:grid-cols-2">
              {group.features.map((feature) => (
                <article key={feature.title} className="marketing-card rounded-2xl p-6">
                  <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-primary/10 text-primary"><feature.icon className="h-5 w-5" aria-hidden="true" /></div>
                  <h3 className="mt-6 text-xl font-semibold tracking-tight">{feature.title}</h3>
                  <p className="mt-3 leading-7 text-muted-foreground">{feature.text}</p>
                </article>
              ))}
            </div>
          </div>
        </section>
      ))}

      <section className="border-y bg-card px-5 py-20 sm:px-8 sm:py-24 lg:px-10">
        <div className="mx-auto max-w-7xl">
          <SectionHeading centered eyebrow="Designed differently" title="Power without pretending security is invisible." description="PeerOnQ combines the practical speed of remote support with controls that stay understandable before, during, and after a session." />
          <div className="mx-auto mt-12 grid max-w-5xl gap-4 md:grid-cols-3">
            {[
              [LockKeyhole, "Permission is specific", "Viewing, input, clipboard, transfer, trust, and unattended access are not one hidden master switch."],
              [ShieldCheck, "Sensitive actions are visible", "Connection state, approval, capability changes, and local security events stay observable."],
              [BookUser, "Ownership stays flexible", "The MIT-licensed core has no product activation or paid entitlement check."],
            ].map(([Icon, title, text]) => {
              const DifferenceIcon = Icon as typeof LockKeyhole;
              return (
                <article key={title as string} className="rounded-2xl border bg-background p-6 text-center">
                  <DifferenceIcon className="mx-auto h-6 w-6 text-primary" aria-hidden="true" />
                  <h3 className="mt-5 font-semibold">{title as string}</h3>
                  <p className="mt-3 text-sm leading-6 text-muted-foreground">{text as string}</p>
                </article>
              );
            })}
          </div>
        </div>
      </section>

      <MarketingCta title="Put the full workflow on your desktop." description="Choose the available Windows build or review the real status of every planned platform." secondaryHref="/about" secondaryLabel="Meet PeerOnQ" />
    </div>
  );
}
