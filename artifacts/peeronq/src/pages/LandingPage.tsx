import {
  ArrowRight,
  Check,
  CheckCircle2,
  ClipboardCheck,
  FileCheck2,
  KeyRound,
  Laptop,
  LockKeyhole,
  Monitor,
  MousePointer2,
  Network,
  Router,
  ShieldCheck,
  UserCheck,
} from "lucide-react";
import { DownloadsPage } from "@/pages/DownloadsPage";

const capabilities = [
  {
    icon: Monitor,
    title: "Remote view that stays responsive",
    description: "Adaptive quality keeps the approved display readable as resolution, bandwidth, and network conditions change.",
  },
  {
    icon: MousePointer2,
    title: "Control only after approval",
    description: "Keyboard and pointer input begin only when the remote side accepts the exact capability request.",
  },
  {
    icon: FileCheck2,
    title: "File transfer with evidence",
    description: "Selected files and folders move with visible progress, bounded resume, and integrity validation.",
  },
  {
    icon: ClipboardCheck,
    title: "Trust that never hides",
    description: "Clipboard, saved contacts, trusted devices, and unattended access remain separate, revocable controls.",
  },
];

const securityPath = [
  {
    icon: KeyRound,
    label: "Identity",
    title: "Proof-bound device identity",
    description: "Installation keys bind the device proof; identity signatures combine ML-DSA-65 with Ed25519.",
  },
  {
    icon: LockKeyhole,
    label: "Key agreement",
    title: "Hybrid post-quantum connection setup",
    description: "ML-KEM-768 and X25519 establish session key material without relying on one cryptographic family.",
  },
  {
    icon: ShieldCheck,
    label: "Session traffic",
    title: "Separated encrypted channels",
    description: "AES-256-GCM protects encoded video and collaboration traffic with direction- and channel-specific keys.",
  },
  {
    icon: UserCheck,
    label: "Human control",
    title: "Visible permission boundaries",
    description: "The local person sees the request, approves its scope, and can stop or revoke access independently.",
  },
];

const roadmap = [
  ["01", "Complete the Windows core", "Finish the full remote-view, input, collaboration, reconnect, diagnostics, and update workflow."],
  ["02", "Prove trust and reliability", "Measure real session quality, keep permissions visible, and publish only verified release evidence."],
  ["03", "Preserve deployment freedom", "Support direct and authenticated relay paths while keeping an inspectable self-hosting direction."],
  ["04", "Expand without shortcuts", "Deliver the Linux viewer next; add macOS and mobile only when their complete clients are ready."],
];

const questions = [
  {
    question: "Does remote control run in this website?",
    answer: "No. This page selects the installer. Screen sharing, input, clipboard, and file transfer run only in the installed PeerOnQ client.",
  },
  {
    question: "Does saving a device make it trusted?",
    answer: "No. Address-book entries, trusted-device approval, and unattended access are separate. Saving a contact never grants access by itself.",
  },
  {
    question: "Does PeerOnQ require activation?",
    answer: "The MIT-licensed core does not require a license key, product activation, paid entitlement check, or hidden browser control surface.",
  },
];

function SessionModel() {
  return (
    <figure className="relative min-w-0 overflow-hidden rounded-[2rem] border border-marketing-line bg-marketing-panel/80 p-4 shadow-2xl backdrop-blur sm:p-7" aria-label="Illustration of the PeerOnQ permission-first connection model">
      <div className="absolute -right-24 -top-24 h-64 w-64 rounded-full bg-primary/15 blur-3xl" aria-hidden="true" />
      <figcaption className="relative flex flex-wrap items-center justify-between gap-3 border-b border-marketing-line pb-5">
        <span className="text-sm font-semibold text-marketing-foreground">Permission-first session</span>
        <span className="inline-flex items-center gap-2 rounded-full border border-primary/25 bg-primary/10 px-3 py-1.5 text-xs font-semibold text-primary">
          <span className="h-1.5 w-1.5 rounded-full bg-primary" aria-hidden="true" />Approval required
        </span>
      </figcaption>

      <div className="relative grid items-center gap-3 py-5 sm:grid-cols-[1fr_auto_1fr] sm:gap-4 sm:py-8">
        <div className="rounded-2xl border border-marketing-line bg-marketing-hero/55 p-4 sm:p-5">
          <Laptop className="h-6 w-6 text-primary" aria-hidden="true" />
          <p className="mt-3 text-sm font-semibold text-marketing-foreground sm:mt-5">Your device</p>
          <p className="mt-1 text-xs text-marketing-muted">Requests a defined scope</p>
        </div>

        <div className="flex flex-col items-center justify-center text-primary sm:flex-row" aria-hidden="true">
          <span className="h-4 w-px bg-primary/50 sm:h-px sm:w-10" />
          <span className="mx-2 flex h-9 w-9 items-center justify-center rounded-full border border-primary/30 bg-primary/10 sm:h-10 sm:w-10">
            <ArrowRight className="h-4 w-4 rotate-90 sm:rotate-0" />
          </span>
          <span className="h-4 w-px bg-primary/50 sm:h-px sm:w-10" />
        </div>

        <div className="rounded-2xl border border-marketing-line bg-marketing-hero/55 p-4 sm:p-5">
          <Monitor className="h-6 w-6 text-primary" aria-hidden="true" />
          <p className="mt-3 text-sm font-semibold text-marketing-foreground sm:mt-5">Remote device</p>
          <p className="mt-1 text-xs text-marketing-muted">Reviews before access</p>
        </div>
      </div>

      <div className="relative border-t border-marketing-line pt-5">
        <p className="text-xs font-semibold uppercase tracking-[0.16em] text-marketing-muted">Requested capabilities</p>
        <div className="mt-4 flex flex-wrap gap-2">
          {["Remote view", "Keyboard + pointer", "File transfer"].map((item) => (
            <span key={item} className="inline-flex items-center gap-2 rounded-lg border border-marketing-line bg-marketing-hero/45 px-3 py-2 text-xs font-medium text-marketing-foreground">
              <Check className="h-3.5 w-3.5 text-primary" aria-hidden="true" />{item}
            </span>
          ))}
        </div>
        <p className="mt-5 text-xs leading-5 text-marketing-muted">Hybrid post-quantum setup · direct path or authenticated relay · always visible locally</p>
      </div>
    </figure>
  );
}

export function LandingPage() {
  return (
    <div className="overflow-x-hidden">
      <section className="marketing-hero relative overflow-hidden border-b border-marketing-line">
        <div className="marketing-grid" aria-hidden="true" />
        <div className="relative mx-auto max-w-7xl px-5 pb-12 pt-16 sm:px-8 sm:pb-14 sm:pt-20 lg:px-10 lg:pb-16 lg:pt-20">
          <div className="grid min-w-0 gap-x-14 gap-y-10 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)] lg:items-center lg:gap-y-12">
            <div className="order-1 min-w-0 max-w-2xl">
              <p className="marketing-eyebrow">Remote access, rebuilt around trust</p>
              <h1 className="mt-6 text-balance text-5xl font-semibold leading-[0.98] tracking-[-0.055em] text-marketing-foreground sm:text-6xl lg:text-7xl">
                Your desktop. Anywhere. <span className="block text-primary">Still yours.</span>
              </h1>
              <p className="mt-7 max-w-xl text-pretty text-lg leading-8 text-marketing-muted sm:text-xl">
                PeerOnQ combines responsive remote control, secure collaboration, and human-visible permission boundaries in one open-source desktop experience.
              </p>
              <div className="mt-8 max-w-sm">
                <DownloadsPage />
              </div>
              <div className="mt-6 flex flex-wrap gap-x-6 gap-y-3 text-sm text-marketing-muted">
                {["Consent before control", "Hybrid post-quantum setup", "Open-source core"].map((item) => (
                  <span key={item} className="inline-flex items-center gap-2">
                    <CheckCircle2 className="h-4 w-4 text-primary" aria-hidden="true" />{item}
                  </span>
                ))}
              </div>
            </div>
            <div className="order-2 min-w-0">
              <SessionModel />
            </div>
          </div>
        </div>
      </section>

      <section id="product" className="scroll-mt-24 px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto max-w-7xl">
          <div className="grid gap-6 lg:grid-cols-[0.72fr_1.28fr] lg:items-end">
            <div>
              <p className="text-sm font-semibold uppercase tracking-[0.18em] text-primary">One focused product</p>
              <h2 className="mt-4 text-balance text-4xl font-semibold tracking-[-0.045em] sm:text-5xl">Everything the session needs. Nothing hidden around it.</h2>
            </div>
            <p className="max-w-2xl text-pretty text-lg leading-8 text-muted-foreground lg:justify-self-end">
              Connection, control, collaboration, recovery, and trust live in one coherent workflow—without turning the product into a maze of separate dashboards.
            </p>
          </div>

          <div className="mt-14 border-y">
            {capabilities.map((capability, index) => (
              <article key={capability.title} className={`group grid gap-5 py-7 transition-colors hover:bg-secondary/35 sm:grid-cols-[4rem_1fr] sm:px-4 lg:grid-cols-[4rem_0.8fr_1.2fr] lg:items-center ${index > 0 ? "border-t" : ""}`}>
                <span className="font-mono text-sm font-semibold text-primary">0{index + 1}</span>
                <div className="flex items-center gap-4">
                  <span className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-primary/10 text-primary">
                    <capability.icon className="h-5 w-5" aria-hidden="true" />
                  </span>
                  <h3 className="text-xl font-semibold tracking-tight">{capability.title}</h3>
                </div>
                <p className="leading-7 text-muted-foreground sm:col-start-2 lg:col-start-3">{capability.description}</p>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section id="security" className="marketing-hero scroll-mt-24 border-y border-marketing-line px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto grid max-w-7xl gap-14 lg:grid-cols-[0.72fr_1.28fr] lg:items-start">
          <div className="lg:sticky lg:top-28">
            <p className="marketing-eyebrow">A visible chain of trust</p>
            <h2 className="mt-5 text-balance text-4xl font-semibold tracking-[-0.045em] text-marketing-foreground sm:text-5xl">Security is the path, not a badge.</h2>
            <p className="mt-6 max-w-xl text-pretty text-lg leading-8 text-marketing-muted">
              Every connection moves through identity proof, hybrid key agreement, separated traffic protection, and explicit human approval.
            </p>
            <div className="mt-8 flex flex-col gap-3 text-sm text-marketing-muted sm:flex-row sm:flex-wrap">
              <span className="inline-flex items-center gap-2"><Network className="h-4 w-4 text-primary" aria-hidden="true" />Direct when possible</span>
              <span className="inline-flex items-center gap-2"><Router className="h-4 w-4 text-primary" aria-hidden="true" />Authenticated relay when needed</span>
            </div>
          </div>

          <ol className="relative border-l border-marketing-line pl-7 sm:pl-10">
            {securityPath.map((layer, index) => (
              <li key={layer.label} className={`relative pb-10 ${index > 0 ? "pt-10 border-t border-marketing-line" : ""} ${index === securityPath.length - 1 ? "pb-0" : ""}`}>
                <span className="absolute -left-[2.9rem] top-0 flex h-10 w-10 items-center justify-center rounded-full border border-primary/30 bg-marketing-panel text-primary sm:-left-[3.8rem]">
                  <layer.icon className="h-4 w-4" aria-hidden="true" />
                </span>
                <p className="text-xs font-semibold uppercase tracking-[0.16em] text-primary">{layer.label}</p>
                <h3 className="mt-3 text-2xl font-semibold tracking-tight text-marketing-foreground">{layer.title}</h3>
                <p className="mt-3 max-w-2xl leading-7 text-marketing-muted">{layer.description}</p>
              </li>
            ))}
          </ol>
        </div>
      </section>

      <section id="strategy" className="scroll-mt-24 px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto max-w-7xl">
          <div className="max-w-4xl">
            <p className="text-sm font-semibold uppercase tracking-[0.18em] text-primary">Product strategy</p>
            <h2 className="mt-4 text-balance text-4xl font-semibold tracking-[-0.045em] sm:text-6xl">Built in the right order.</h2>
            <p className="mt-6 max-w-2xl text-pretty text-lg leading-8 text-muted-foreground">PeerOnQ earns expansion by completing the core experience first, then proving it under real conditions.</p>
          </div>

          <div className="mt-14 grid gap-8 lg:grid-cols-[1.25fr_0.75fr] lg:items-start">
            <ol className="border-t">
              {roadmap.map(([number, title, description]) => (
                <li key={number} className="grid gap-4 border-b py-7 sm:grid-cols-[4rem_1fr] sm:gap-6">
                  <span className="font-mono text-sm font-semibold text-primary">{number}</span>
                  <div>
                    <h3 className="text-xl font-semibold">{title}</h3>
                    <p className="mt-2 max-w-2xl leading-7 text-muted-foreground">{description}</p>
                  </div>
                </li>
              ))}
            </ol>

            <aside className="rounded-[2rem] border bg-card p-7 shadow-sm sm:p-8" aria-label="PeerOnQ platform roadmap">
              <p className="text-xs font-semibold uppercase tracking-[0.16em] text-primary">Platform direction</p>
              <div className="mt-7 space-y-6">
                {[
                  ["Available", "Windows", "Host + control"],
                  ["Next", "Linux", "Viewer track"],
                  ["Planned", "macOS + mobile", "After verified clients"],
                ].map(([status, platform, scope]) => (
                  <div key={status} className="grid grid-cols-[auto_1fr] gap-x-4 border-b pb-6 last:border-0 last:pb-0">
                    <span className="mt-1 h-2 w-2 rounded-full bg-primary" aria-hidden="true" />
                    <div>
                      <p className="text-xs font-semibold uppercase tracking-[0.14em] text-muted-foreground">{status}</p>
                      <p className="mt-1 font-semibold">{platform}</p>
                      <p className="mt-1 text-sm text-muted-foreground">{scope}</p>
                    </div>
                  </div>
                ))}
              </div>
              <div className="mt-7 flex flex-wrap gap-2 border-t pt-6 text-xs font-semibold text-muted-foreground">
                <span className="rounded-full bg-secondary px-3 py-1.5">MIT licensed</span>
                <span className="rounded-full bg-secondary px-3 py-1.5">No activation key</span>
              </div>
            </aside>
          </div>
        </div>
      </section>

      <section id="help" className="scroll-mt-24 border-t bg-card px-5 py-20 sm:px-8 sm:py-24 lg:px-10">
        <div className="mx-auto grid max-w-7xl gap-10 lg:grid-cols-[0.68fr_1.32fr]">
          <div>
            <p className="text-sm font-semibold uppercase tracking-[0.18em] text-primary">Essential answers</p>
            <h2 className="mt-4 text-balance text-3xl font-semibold tracking-[-0.035em] sm:text-4xl">Only what you need to know.</h2>
          </div>
          <div className="border-y">
            {questions.map((item, index) => (
              <details key={item.question} className={`group py-6 ${index > 0 ? "border-t" : ""}`}>
                <summary className="flex min-h-11 cursor-pointer list-none items-center justify-between gap-4 font-semibold focus-visible:rounded-md">
                  {item.question}<span className="text-primary transition-transform duration-200 group-open:rotate-45" aria-hidden="true">+</span>
                </summary>
                <p className="mt-4 max-w-3xl leading-7 text-muted-foreground">{item.answer}</p>
              </details>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
