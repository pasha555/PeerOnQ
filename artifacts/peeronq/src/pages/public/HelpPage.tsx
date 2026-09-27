import { Link } from "wouter";
import { ArrowRight, CheckCircle2, Download, ExternalLink, HelpCircle, MonitorCheck, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { PublicPageHero, SectionHeading } from "@/components/PublicMarketing";
import { getAccountPortalUrl } from "@/lib/accountPortal";

const steps = [
  ["01", "Choose a verified build", "Open the downloads directory and select a package only when it is marked available for your platform and architecture."],
  ["02", "Install and launch", "Complete the operating-system installation flow, then open PeerOnQ from your applications or Start menu."],
  ["03", "Connect deliberately", "Enter the remote device ID, choose an access mode, and let the remote side review the exact request."],
];

const faqs = [
  ["Can I start remote control in the browser?", "No. The public website and account portal do not capture screens or start control sessions. Remote access runs in the installed PeerOnQ client."],
  ["Why is a platform shown without a download?", "PeerOnQ shows the complete platform roadmap, but only exposes a link after a real package is published through the active release configuration."],
  ["Why did the peeronq:// link not open?", "Confirm the desktop client is installed and allow your browser to open PeerOnQ when it displays the external-application prompt."],
  ["Does saving a device make it trusted?", "No. Address-book entries and trusted-device approval are separate. Saving contact details never silently grants access."],
];

export function HelpPage() {
  return (
    <div>
      <PublicPageHero
        eyebrow="Help center"
        title={<>Get from download to <span className="text-primary">your first session.</span></>}
        description="Clear installation guidance, honest platform status, and the essential answers for opening and using the PeerOnQ desktop client."
        actions={
          <>
            <Button asChild size="lg" className="h-12 px-6 text-base"><a href="/downloads"><Download className="mr-2 h-4 w-4" aria-hidden="true" />View downloads</a></Button>
            <Button asChild size="lg" variant="outline" className="h-12 border-marketing-line bg-marketing-panel/60 px-6 text-base text-marketing-foreground hover:bg-marketing-panel hover:text-marketing-foreground">
              <a href={getAccountPortalUrl()}>Open account portal<ExternalLink className="ml-2 h-4 w-4" aria-hidden="true" /></a>
            </Button>
          </>
        }
      >
        <div className="rounded-[1.75rem] border border-marketing-line bg-marketing-panel/85 p-6 backdrop-blur sm:p-7">
          <HelpCircle className="h-7 w-7 text-primary" aria-hidden="true" />
          <h2 className="mt-5 text-xl font-semibold text-marketing-foreground">Before you connect</h2>
          <ul className="mt-5 grid gap-3 text-sm text-marketing-muted">
            {["Install PeerOnQ on the required device", "Have the remote device ID ready", "Confirm someone can approve the request"].map((item) => (
              <li key={item} className="flex items-start gap-3"><CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden="true" />{item}</li>
            ))}
          </ul>
        </div>
      </PublicPageHero>

      <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto max-w-7xl">
          <SectionHeading centered eyebrow="Quick start" title="Three steps, with approval built in" description="The browser helps you find the product; the desktop client owns the remote session." />
          <div className="mt-12 grid gap-5 md:grid-cols-3">
            {steps.map(([number, title, text]) => (
              <article key={number} className="marketing-card rounded-2xl p-7">
                <span className="font-mono text-sm font-bold text-primary">{number}</span>
                <h3 className="mt-8 text-xl font-semibold">{title}</h3>
                <p className="mt-3 leading-7 text-muted-foreground">{text}</p>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="border-y bg-secondary/45 px-5 py-20 sm:px-8 sm:py-24 lg:px-10">
        <div className="mx-auto grid max-w-7xl gap-8 md:grid-cols-2">
          <article className="rounded-2xl border bg-card p-7">
            <MonitorCheck className="h-6 w-6 text-primary" aria-hidden="true" />
            <h2 className="mt-5 text-xl font-semibold">The app does not open</h2>
            <p className="mt-3 leading-7 text-muted-foreground">Confirm PeerOnQ is installed and that your browser is allowed to open the <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-sm text-foreground">peeronq://</span> application link.</p>
            <Button asChild variant="outline" className="mt-6"><a href="/downloads">Revisit downloads<ArrowRight className="ml-2 h-4 w-4" aria-hidden="true" /></a></Button>
          </article>
          <article className="rounded-2xl border bg-card p-7">
            <ShieldCheck className="h-6 w-6 text-primary" aria-hidden="true" />
            <h2 className="mt-5 text-xl font-semibold">The session request is declined</h2>
            <p className="mt-3 leading-7 text-muted-foreground">Check the remote ID, choose only the capabilities you need, and ask the person at the remote device to review the visible approval prompt.</p>
            <Button asChild variant="outline" className="mt-6"><Link href="/security">Review connection security<ArrowRight className="ml-2 h-4 w-4" aria-hidden="true" /></Link></Button>
          </article>
        </div>
      </section>

      <section className="px-5 py-20 sm:px-8 sm:py-24 lg:px-10 lg:py-28">
        <div className="mx-auto grid max-w-7xl gap-12 lg:grid-cols-[0.7fr_1.3fr]">
          <SectionHeading eyebrow="Common questions" title="Direct answers before you troubleshoot" />
          <div className="overflow-hidden rounded-2xl border bg-card">
            {faqs.map(([question, answer], index) => (
              <details key={question} className={`group p-5 sm:p-6 ${index > 0 ? "border-t" : ""}`}>
                <summary className="flex cursor-pointer list-none items-center justify-between gap-4 font-semibold focus-visible:rounded-md">
                  {question}<span className="text-primary transition-transform duration-200 group-open:rotate-45" aria-hidden="true">+</span>
                </summary>
                <p className="mt-4 max-w-3xl leading-7 text-muted-foreground">{answer}</p>
              </details>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
