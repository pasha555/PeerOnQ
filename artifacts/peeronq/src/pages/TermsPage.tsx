import { Link } from "wouter";
import { ArrowLeft, FileText, Scale, ShieldAlert } from "lucide-react";
import { PublicPageHero } from "@/components/PublicMarketing";

const sections = [
  ["1. Software status", "PeerOnQ packages may be classified as development, pilot, or signed release builds. Use only the package explicitly published for your environment, review its classification, and verify the available integrity evidence before installation."],
  ["2. Authorized use", "Use PeerOnQ only on devices you own or are explicitly authorized to access. Hidden monitoring, credential theft, unauthorized control, evasion of security controls, and malicious file transfer are prohibited."],
  ["3. Consent and responsibility", "The operator requesting a connection is responsible for choosing an appropriate capability scope. The person or organization controlling the remote device is responsible for approving only requests they understand and expect."],
  ["4. Open-source license", "PeerOnQ source code is provided under the MIT License. That license governs permission to use, copy, modify, merge, publish, distribute, sublicense, and sell copies of the covered software, subject to its notice requirements."],
  ["5. No warranty", "Unless a separate written agreement says otherwise, the software is provided as is, without warranty of any kind. Do not use a development or pilot build as the sole control for a safety-critical or business-critical system."],
  ["6. Third-party and deployment terms", "Operating systems, network providers, app stores, self-hosted infrastructure, and deployment operators may impose their own terms. Operators are responsible for their configuration, user notice, legal basis, retention, and support obligations."],
  ["7. Changes", "Product behavior and this repository notice may evolve as releases mature. Material changes should be reviewed with the release documentation before deploying an updated build."],
];

export function TermsPage() {
  return (
    <div>
      <PublicPageHero eyebrow="Terms" title={<>Use powerful access <span className="text-primary">responsibly.</span></>} description="Plain-language conditions for development and release builds of the PeerOnQ remote-access software." />

      <section className="border-b bg-warning/10 px-5 py-5 sm:px-8 lg:px-10">
        <div className="mx-auto flex max-w-7xl items-start gap-3 text-sm">
          <ShieldAlert className="mt-0.5 h-5 w-5 shrink-0 text-warning" aria-hidden="true" />
          <p className="leading-6"><span className="font-semibold">Important:</span> Remote access is authorized only when you own the device or have explicit permission from the person or organization responsible for it.</p>
        </div>
      </section>

      <section className="px-5 py-16 sm:px-8 sm:py-20 lg:px-10">
        <div className="mx-auto grid max-w-6xl gap-10 lg:grid-cols-[15rem_1fr]">
          <aside className="lg:sticky lg:top-28 lg:self-start">
            <Scale className="h-6 w-6 text-primary" aria-hidden="true" />
            <p className="mt-4 font-semibold">Terms of use</p>
            <p className="mt-2 text-sm text-muted-foreground">Last updated: August 22, 2026</p>
            <Link href="/" className="mt-6 inline-flex items-center text-sm font-medium text-primary hover:underline"><ArrowLeft className="mr-2 h-4 w-4" aria-hidden="true" />Back to home</Link>
          </aside>
          <div className="overflow-hidden rounded-2xl border bg-card">
            <div className="flex items-center gap-3 border-b bg-secondary/45 p-6 sm:p-8">
              <FileText className="h-6 w-6 text-primary" aria-hidden="true" />
              <p className="font-semibold">PeerOnQ software and website terms</p>
            </div>
            {sections.map(([title, body]) => (
              <section key={title} className="border-b p-6 last:border-b-0 sm:p-8">
                <h2 className="text-xl font-semibold">{title}</h2>
                <p className="mt-4 max-w-3xl leading-8 text-muted-foreground">{body}</p>
              </section>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
