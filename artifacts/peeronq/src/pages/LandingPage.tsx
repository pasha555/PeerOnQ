import {
  Activity, ArrowDown, ArrowRight, ArrowUpRight, Check, ChevronRight, Code2, Download,
  Eye, FileUp, Fingerprint, FolderOpen, Github, KeyRound, Laptop, LayoutDashboard,
  LockKeyhole, Monitor, MousePointer2, Network, RefreshCw, Settings, ShieldCheck, Users,
} from "lucide-react";
import { DownloadsPage } from "@/pages/DownloadsPage";
import { getAccountPortalUrl } from "@/lib/accountPortal";

const repository = "https://github.com/pasha555/PeerOnQ";
const capabilities = [
  { icon: Eye, title: "View Only", label: "See the same screen", description: "Walk through a problem together. Share a display while keeping keyboard and mouse control with the person at the remote device." },
  { icon: MousePointer2, title: "Full Control", label: "Help, hands on", description: "Use the remote keyboard and pointer after approval. The remote owner chooses the access scope and can end the session." },
  { icon: FileUp, title: "File Transfer", label: "Move what you need", description: "Send files and folders with visible progress and integrity checks. File access stays permission-controlled and depends on the capabilities negotiated for the connection." },
  { icon: KeyRound, title: "Unattended Access", label: "Your setup. Your decision.", description: "Prepare access to a device you manage with separate setup and trust decisions. Currently, the Windows host must remain running in a signed-in user session; this is not a background system service." },
];
const questions = [
  { question: "Where do I start?", answer: "Install an available PeerOnQ client on the devices you want to connect. Share the remote device’s PeerOnQ ID, then have its owner approve View Only or Full Control. The portal is where you manage your account and organization." },
  { question: "Can I control a computer from this website?", answer: "Remote viewing, keyboard and pointer control, and file transfer run in the installed client. This website provides product information and downloads. The portal manages account and organization information; it is not a browser remote-control client." },
  { question: "Do I need an account for every connection?", answer: "Accountless local LAN access remains supported. A portal account gives you access to your profile, account security, and organization tools. Device identity and organization ownership remain verified separately; signing in never grants control of a device." },
  { question: "Is PeerOnQ open source?", answer: "Yes. PeerOnQ’s source is public on GitHub under the MIT license. You can inspect the code, report issues, and contribute. Third-party components have their own terms, documented in the repository." },
  { question: "Which platforms can I use?", answer: "Windows is the main host and controller client. Linux, Android, and Apple viewer/controller projects are available as source previews with separate release gates. A platform gets a download only when a package has been published for it." },
  { question: "What should I expect from connection quality?", answer: "Quality and latency depend on the source display, hardware, network, and connection path. Resolution options include 4K/UHD on supported source displays, but sustained 4K performance and any particular latency are not guaranteed. Measured results and remaining limitations are published in the repository." },
];
const previewNavigation = [
  { icon: LayoutDashboard, label: "Dashboard" }, { icon: Monitor, label: "Devices" },
  { icon: FolderOpen, label: "File Transfer" }, { icon: ShieldCheck, label: "Security" },
  { icon: Settings, label: "Settings" },
];

function ClientPreview() {
  return <figure className="relative mx-auto w-full max-w-xl" aria-label="Illustration of the PeerOnQ desktop connection workflow">
    <div className="overflow-hidden rounded-2xl border border-marketing-line bg-marketing-hero text-marketing-foreground shadow-2xl shadow-marketing-hero/15">
      <div className="flex items-center justify-between border-b border-marketing-line px-5 py-4">
        <span className="flex items-center gap-2.5 text-sm font-semibold"><img src="/brand/peeronq-mark-light.svg" alt="" className="h-6 w-6" />PeerOnQ</span>
        <span className="rounded-md border border-marketing-line px-2 py-1 text-[11px] text-marketing-muted">Desktop client</span>
      </div>
      <div className="grid grid-cols-[52px_1fr] sm:grid-cols-[132px_1fr]">
        <div className="space-y-2 border-r border-marketing-line px-2 py-5 sm:px-3" aria-hidden="true">
          {previewNavigation.map(({ icon: Icon, label }, index) => <div key={label} className={`flex items-center gap-2 rounded-md px-2 py-2.5 text-[11px] ${index === 0 ? "bg-marketing-panel text-marketing-foreground" : "text-marketing-muted"}`}><Icon className="h-4 w-4 shrink-0" /><span className="hidden sm:inline">{label}</span></div>)}
        </div>
        <div className="min-w-0 p-5 sm:p-7">
          <p className="text-[11px] font-medium uppercase tracking-widest text-marketing-muted">Remote access</p>
          <p className="mt-2 text-2xl font-semibold tracking-tight">Make a connection.</p>
          <div className="my-6 flex items-center gap-4" aria-hidden="true">
            <div className="flex h-14 w-14 items-center justify-center rounded-xl border border-marketing-line bg-marketing-panel"><Laptop className="h-7 w-7" /></div>
            <div className="flex flex-1 items-center gap-1 text-marketing-muted"><span className="h-px flex-1 bg-marketing-line" /><LockKeyhole className="h-4 w-4" /><span className="h-px flex-1 bg-marketing-line" /></div>
            <div className="flex h-14 w-14 items-center justify-center rounded-xl border border-marketing-line bg-marketing-panel"><Monitor className="h-7 w-7" /></div>
          </div>
          <div className="rounded-xl border border-marketing-line bg-marketing-panel p-4">
            <p className="text-sm font-medium">Choose the access you approve</p>
            <div className="mt-4 grid gap-2 text-xs sm:grid-cols-2">
              <span className="flex items-center gap-2 rounded-md border border-marketing-line px-3 py-3"><Eye className="h-4 w-4" />View Only</span>
              <span className="flex items-center gap-2 rounded-md border border-marketing-line px-3 py-3"><MousePointer2 className="h-4 w-4" />Full Control</span>
            </div>
            <p className="mt-4 flex items-center gap-2 text-[11px] text-marketing-muted"><ShieldCheck className="h-3.5 w-3.5 shrink-0" />The remote owner stays in control.</p>
          </div>
        </div>
      </div>
    </div>
    <figcaption className="mt-4 text-center text-xs text-muted-foreground">Client workflow illustration. Connections run in the installed app.</figcaption>
  </figure>;
}

export function LandingPage() {
  const portalUrl = getAccountPortalUrl();
  return <div>
    <section className="relative overflow-hidden border-b">
      <div className="public-hero-wash pointer-events-none absolute inset-0" aria-hidden="true" />
      <div className="public-container relative grid items-center gap-14 py-16 sm:py-24 lg:grid-cols-[1fr_1.06fr] lg:gap-16 lg:py-28">
        <div>
          <a href="#open-source" className="inline-flex min-h-11 items-center gap-2 rounded-full border border-primary/20 bg-primary/5 px-3 py-1.5 text-xs font-semibold text-primary"><Code2 className="h-3.5 w-3.5" aria-hidden="true" />Open source. MIT licensed.<ChevronRight className="h-3.5 w-3.5" aria-hidden="true" /></a>
          <h1 className="mt-7 text-balance text-[clamp(2.8rem,5.3vw,4.7rem)] font-semibold leading-[1.06] tracking-[-0.055em]">Remote access.<br /><span className="text-primary">On your terms.</span></h1>
          <p className="mt-6 max-w-lg text-pretty text-lg leading-8 text-muted-foreground">Connect to your devices. Help someone get unstuck. Share a screen or take control with clear permissions and a native, open-source client.</p>
          <div className="mt-8">
            <DownloadsPage secondaryAction={<a href={portalUrl} className="public-button public-button-secondary">Open Portal<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a>} />
          </div>
          <a href={repository} className="mt-6 inline-flex min-h-11 items-center gap-2 text-sm font-medium text-muted-foreground hover:text-foreground"><Github className="h-4 w-4" aria-hidden="true" />View on GitHub<ArrowUpRight className="h-3.5 w-3.5" aria-hidden="true" /></a>
        </div>
        <ClientPreview />
      </div>
    </section>

    <div className="border-b bg-card"><div className="public-container grid grid-cols-2 gap-5 py-6 text-sm font-medium text-muted-foreground">
      <span className="flex items-center gap-2.5"><ShieldCheck className="h-4 w-4 shrink-0 text-primary" aria-hidden="true" />Permission-based access</span>
      <span className="flex items-center justify-end gap-2.5"><Code2 className="h-4 w-4 shrink-0 text-primary" aria-hidden="true" />Public source. Practical tools.</span>
    </div></div>

    <section id="product" className="public-section scroll-mt-24"><div className="public-container grid gap-12 lg:grid-cols-[0.75fr_1.25fr] lg:gap-20">
      <div><p className="public-eyebrow">The right access for the task</p><h2 className="public-heading mt-4">One client.<br />Clear choices.</h2><p className="mt-6 max-w-sm leading-7 text-muted-foreground">Help a colleague, move a file, or reach your own computer. Choose the access the task needs, with permissions the remote owner can understand.</p><a href="#security" className="mt-6 inline-flex min-h-11 items-center gap-2 text-sm font-semibold text-primary hover:underline">How permissions work<ArrowRight className="h-4 w-4" aria-hidden="true" /></a></div>
      <div className="divide-y border-y">{capabilities.map(({ icon: Icon, title, label, description }, index) => <article key={title} className="grid grid-cols-[2rem_1fr] gap-4 py-7 sm:grid-cols-[2rem_1fr_auto] sm:gap-6">
        <span className="pt-1 font-mono text-xs text-muted-foreground" aria-hidden="true">0{index + 1}</span>
        <div><p className="text-xs font-medium text-primary">{label}</p><h3 className="mt-2 text-xl font-semibold tracking-tight">{title}</h3><p className="mt-3 leading-7 text-muted-foreground">{description}</p></div>
        <Icon className="hidden h-5 w-5 text-muted-foreground sm:block" aria-hidden="true" />
      </article>)}</div>
    </div></section>

    <section className="public-section border-t" aria-labelledby="connection-heading"><div className="public-container">
      <div className="grid gap-8 lg:grid-cols-2 lg:gap-20">
        <div><p className="public-eyebrow">Built around the connection</p><h2 id="connection-heading" className="public-heading mt-4">A path to your device.<br />A view of what happens.</h2></div>
        <p className="self-end leading-7 text-muted-foreground">PeerOnQ supports direct connections and configured TURN relay paths. The client negotiates an available route; network conditions and host capabilities still determine quality and responsiveness.</p>
      </div>
      <div className="mt-10 grid gap-8 border-y py-8 md:grid-cols-2 lg:grid-cols-4 lg:gap-10">{[
        { icon: Network, title: "Direct & relay paths", text: "Use an available direct path or a configured relay for remote viewing and control. The client reports the selected connection path." },
        { icon: RefreshCw, title: "Connection recovery", text: "Input pauses when a session is interrupted. The client attempts authenticated recovery and a fresh connection negotiation; reconnection is not guaranteed." },
        { icon: Activity, title: "Useful diagnostics", text: "Inspect the connection path and session diagnostics in the client. Bring reproducible details to a support report without sharing credentials." },
        { icon: Download, title: "Verified updates", text: "The Windows updater checks signed update metadata and package integrity. Invalid or unverifiable updates are rejected; installer signing is a separate release gate." },
      ].map(({ icon: Icon, title, text }) => <div key={title}><Icon className="h-5 w-5 text-primary" aria-hidden="true" /><h3 className="mt-5 font-semibold">{title}</h3><p className="mt-3 text-sm leading-7 text-muted-foreground">{text}</p></div>)}</div>
      <p className="mt-5 max-w-3xl text-sm leading-7 text-muted-foreground">Implementation is documented in public. Real-world NAT coverage, sustained 4K performance, and latency targets require validation on the devices and networks you use.</p>
    </div></section>

    <section className="public-section border-y bg-card" aria-labelledby="portal-heading"><div className="public-container grid gap-12 lg:grid-cols-2 lg:items-center">
      <div><p className="public-eyebrow">Meet your PeerOnQ portal</p><h2 id="portal-heading" className="public-heading mt-4">Your account.<br />A clearer overview.</h2><p className="mt-6 max-w-lg leading-7 text-muted-foreground">Keep account security, organization devices, and session history in one place. Use the desktop app for the connection and the portal for the bigger picture.</p><a href={portalUrl} className="public-button public-button-secondary mt-7">Go to your portal<ArrowRight className="h-4 w-4" aria-hidden="true" /></a></div>
      <div className="overflow-hidden rounded-xl border bg-background"><div className="flex items-center gap-3 border-b px-6 py-5"><LayoutDashboard className="h-5 w-5 text-primary" aria-hidden="true" /><span className="font-semibold">Your account control center</span></div>
        {[
          { icon: Monitor, title: "Devices & Sessions", text: "Review your organization’s managed devices and remote session history." },
          { icon: ShieldCheck, title: "Security & Settings", text: "Manage your profile, MFA, trusted account devices, signed-in sessions, and privacy requests." },
          { icon: Users, title: "Organization & Teams", text: "Manage members, invitations, and teams. Review organization policy and audit history with the permissions assigned to your role." },
        ].map(({ icon: Icon, title, text }) => <div key={title} className="flex gap-4 border-b px-6 py-6 last:border-0"><Icon className="mt-1 h-5 w-5 shrink-0 text-muted-foreground" aria-hidden="true" /><div><h3 className="font-semibold">{title}</h3><p className="mt-1.5 text-sm leading-6 text-muted-foreground">{text}</p></div></div>)}
      </div>
    </div></section>

    <section id="security" className="public-section scroll-mt-24 bg-marketing-hero text-marketing-foreground"><div className="public-container grid gap-12 lg:grid-cols-[0.9fr_1.1fr] lg:gap-20">
      <div><div className="inline-flex h-12 w-12 items-center justify-center rounded-xl border border-marketing-line bg-marketing-panel"><ShieldCheck className="h-6 w-6" aria-hidden="true" /></div><p className="mt-7 text-xs font-semibold uppercase tracking-[.16em] text-marketing-muted">Trust through clear boundaries</p><h2 className="public-heading mt-4">Access is a decision.<br />Keep it yours.</h2><p className="mt-6 leading-7 text-marketing-muted">Security should be visible in how a product works. PeerOnQ combines encrypted session traffic with explicit, revocable access.</p><a href={`${repository}/blob/main/docs/PROTOCOL_COMPLIANCE.md`} className="mt-6 inline-flex min-h-11 items-center gap-2 text-sm font-medium underline decoration-marketing-line underline-offset-4 hover:decoration-marketing-foreground">Read the security implementation<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a></div>
      <div className="divide-y divide-marketing-line border-y border-marketing-line">{[
        { icon: Fingerprint, title: "Know what you are approving", text: "Attended sessions require the remote owner’s approval. View Only and Full Control keep the choice explicit." },
        { icon: LockKeyhole, title: "Encrypted session traffic", text: "The client protects screen and collaboration traffic with authenticated encryption and hybrid key agreement. Production signaling uses TLS. Failed identity or security validation blocks the connection." },
        { icon: KeyRound, title: "Separate permission for unattended access", text: "Unattended Access requires its own setup and trust decisions. Contacts, account sign-in, and saved devices do not grant remote control." },
      ].map(({ icon: Icon, title, text }) => <div key={title} className="flex gap-4 py-7"><Icon className="mt-1 h-5 w-5 shrink-0 text-marketing-muted" aria-hidden="true" /><div><h3 className="text-lg font-semibold">{title}</h3><p className="mt-2 text-sm leading-7 text-marketing-muted">{text}</p></div></div>)}</div>
    </div></section>

    <section id="open-source" className="public-section scroll-mt-24"><span id="strategy" className="scroll-mt-24" /><div className="public-container grid gap-12 lg:grid-cols-[1.15fr_0.85fr] lg:items-center">
      <div><p className="public-eyebrow">Open source, by design</p><h2 className="public-heading mt-4 max-w-xl">See the code.<br />Be part of what’s next.</h2><p className="mt-6 max-w-xl leading-7 text-muted-foreground">PeerOnQ is MIT licensed and developed in public. Inspect how it works, explore the development history, or help make the next version better.</p><p className="mt-4 max-w-xl leading-7 text-muted-foreground">Explore the <a href={`${repository}/blob/main/docs/ARCHITECTURE.md`} className="font-medium text-primary underline underline-offset-4">architecture</a>, contribute a fix, or discuss an idea with the community. For operators, the <a href={`${repository}/blob/main/docs/DEPLOYMENT.md`} className="font-medium text-primary underline underline-offset-4">self-hosting guide</a> documents deployment requirements and the remaining production acceptance checks.</p><div className="mt-7 flex flex-wrap items-center gap-5"><a href={repository} className="public-button public-button-secondary"><Github className="h-4 w-4" aria-hidden="true" />Explore the repository<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a><a href={`${repository}/issues`} className="inline-flex min-h-11 items-center gap-2 text-sm font-semibold text-primary hover:underline">Report an issue<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a></div></div>
      <div className="rounded-xl border bg-card p-7 sm:p-9"><Code2 className="h-8 w-8 text-primary" aria-hidden="true" /><p className="mt-6 font-mono text-sm text-muted-foreground">LICENSE</p><h3 className="mt-2 text-2xl font-semibold tracking-tight">The MIT License</h3><ul className="mt-6 space-y-3 text-sm">{["Read and learn from the source", "Use, modify, and share under MIT terms", "Follow the work and contribute on GitHub"].map(text => <li key={text} className="flex items-start gap-2.5"><Check className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden="true" />{text}</li>)}</ul><p className="mt-6 border-t pt-5 text-xs leading-6 text-muted-foreground">Third-party components retain their own license terms. <a href={`${repository}/blob/main/THIRD_PARTY_NOTICES.md`} className="font-medium underline underline-offset-4">Read the notices</a>.</p></div>
    </div></section>

    <section id="download" className="public-section scroll-mt-24 border-y bg-card"><div className="public-container grid gap-12 lg:grid-cols-2 lg:items-start">
      <div>
        <p className="public-eyebrow">Downloads & platform support</p>
        <h2 className="public-heading mt-4">Native clients.<br />An honest release status.</h2>
        <p className="mt-6 max-w-lg leading-7 text-muted-foreground">Windows is the main host and controller. Other platforms are growing from the same protocol, with their own device testing and release requirements.</p>
        <p className="mt-4 max-w-lg leading-7 text-muted-foreground">The download selector shows the package configured for your device and its release classification when supplied. If no package is published here, it stays unavailable.</p>
        <a href="#client-download" className="public-button public-button-primary mt-7">Find your download<ArrowRight className="h-4 w-4" aria-hidden="true" /></a>
        <a href={portalUrl} className="mt-5 flex min-h-11 items-center gap-2 text-sm font-semibold text-primary hover:underline">Already have the app? Open your portal<ArrowRight className="h-4 w-4" aria-hidden="true" /></a>
      </div>
      <div>
        <h3 className="font-semibold">A clear view of platform support</h3>
        <dl className="mt-6 divide-y border-y">{[
          ["Windows", "Host, view, and control"],
          ["Linux", "Viewer/controller source preview"],
          ["Android", "Viewer/controller source preview"],
          ["macOS", "Mac Catalyst viewer/controller source preview"],
          ["iPhone & iPad", "Viewer/controller source preview"],
        ].map(([name, detail]) => <div key={name} className="grid gap-1 py-4 text-sm sm:grid-cols-[8rem_1fr] sm:gap-5"><dt className="font-medium">{name}</dt><dd className="text-muted-foreground">{detail}</dd></div>)}</dl>
        <p className="mt-5 text-xs leading-6 text-muted-foreground">Source previews are development projects, not published apps. Package availability, signing, and physical-device validation are tracked separately.</p>
        <a href={`${repository}/blob/main/docs/CROSS_PLATFORM_CAPABILITIES.md`} className="mt-4 inline-flex min-h-11 items-center gap-2 text-sm font-medium text-primary hover:underline">Platform details<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a>
      </div>
    </div></section>

    <section id="help" className="public-section scroll-mt-24"><div className="public-container grid gap-10 lg:grid-cols-[0.75fr_1.25fr] lg:gap-20">
      <div><p className="public-eyebrow">A little clarity</p><h2 className="public-heading mt-4">Good questions.<br />Straight answers.</h2><p className="mt-5 leading-7 text-muted-foreground">Understand what runs where and choose the access that fits your task.</p><a href={`${repository}#readme`} className="mt-5 inline-flex min-h-11 items-center gap-2 text-sm font-semibold text-primary hover:underline">Read the project guide<ArrowUpRight className="h-4 w-4" aria-hidden="true" /></a></div>
      <div className="border-y">{questions.map(({ question, answer }) => <details key={question} className="group border-b py-4 last:border-0"><summary className="flex min-h-12 cursor-pointer list-none items-center justify-between gap-5 py-2 font-semibold"><span>{question}</span><ArrowDown className="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-open:rotate-180" aria-hidden="true" /></summary><p className="pb-4 pt-2 text-sm leading-7 text-muted-foreground">{answer}</p></details>)}</div>
    </div></section>
  </div>;
}
