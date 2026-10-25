import { Link } from "wouter";
import { ArrowLeft, Database, EyeOff, HardDrive, ShieldCheck } from "lucide-react";
import { PublicPageHero } from "@/components/PublicMarketing";

const sections = [
  {
    title: "1. Public website",
    body: "This frontend does not make background API requests. Theme preference and preview-only settings are stored in your browser. Choosing an external account-portal link or an explicitly configured download link navigates to that destination as a user action.",
  },
  {
    title: "2. Desktop client",
    body: "PeerOnQ processes the display, input, clipboard, and file data needed for an approved remote session. Session content is not stored in the public website. Local client settings, trusted-device state, collaboration records, and security history use protected or bounded local storage according to their sensitivity.",
  },
  {
    title: "3. Diagnostics and telemetry",
    body: "Crash reporting is disabled by default. Diagnostic uploads require an explicit request and consent, and are designed to exclude credentials, session content, clipboard content, file content, full device identifiers, and private paths.",
  },
  {
    title: "4. Account and organization data",
    body: "Optional account and organization workflows run in a separately deployed portal. They use their own authenticated session, security, export, and deletion controls rather than the browser storage used by this public frontend.",
  },
  {
    title: "5. Remote-session visibility",
    body: "Incoming access requires visible approval by default. Active screen sharing and remote input remain visible in the installed client, and trusted or unattended access can be revoked through their dedicated controls.",
  },
  {
    title: "6. Scope of this notice",
    body: "This page describes the current repository build and its privacy design. Deployment operators remain responsible for publishing the legal notice, contact information, retention schedule, and jurisdiction-specific terms required for their environment.",
  },
];

export function PrivacyPage() {
  return (
    <div>
      <PublicPageHero eyebrow="Privacy" title={<>Data boundaries you can <span className="text-primary">understand.</span></>} description="A plain-language overview of what the public website, installed client, diagnostics, and optional account portal are designed to handle." />

      <section className="border-b bg-card px-5 py-8 sm:px-8 lg:px-10">
        <div className="mx-auto grid max-w-7xl gap-6 sm:grid-cols-3">
          {[
            [EyeOff, "No hidden browser control", "This site does not capture or control a device."],
            [HardDrive, "Local-first preferences", "Frontend preferences stay in browser storage."],
            [ShieldCheck, "Consent for diagnostics", "Diagnostic upload is explicit and sanitized."],
          ].map(([Icon, title, text]) => {
            const PrivacyIcon = Icon as typeof EyeOff;
            return <div key={title as string} className="flex gap-3"><PrivacyIcon className="mt-0.5 h-5 w-5 shrink-0 text-primary" aria-hidden="true" /><div><p className="font-semibold">{title as string}</p><p className="mt-1 text-sm leading-6 text-muted-foreground">{text as string}</p></div></div>;
          })}
        </div>
      </section>

      <section className="px-5 py-16 sm:px-8 sm:py-20 lg:px-10">
        <div className="mx-auto grid max-w-6xl gap-10 lg:grid-cols-[15rem_1fr]">
          <aside className="lg:sticky lg:top-28 lg:self-start">
            <Database className="h-6 w-6 text-primary" aria-hidden="true" />
            <p className="mt-4 font-semibold">Privacy overview</p>
            <p className="mt-2 text-sm text-muted-foreground">Last updated: August 22, 2026</p>
            <Link href="/" className="mt-6 inline-flex items-center text-sm font-medium text-primary hover:underline"><ArrowLeft className="mr-2 h-4 w-4" aria-hidden="true" />Back to home</Link>
          </aside>
          <div className="overflow-hidden rounded-2xl border bg-card">
            {sections.map((section, index) => (
              <section key={section.title} className={`p-6 sm:p-8 ${index > 0 ? "border-t" : ""}`}>
                <h2 className="text-xl font-semibold">{section.title}</h2>
                <p className="mt-4 max-w-3xl leading-8 text-muted-foreground">{section.body}</p>
              </section>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
