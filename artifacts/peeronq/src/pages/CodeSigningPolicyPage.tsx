import { Link } from "wouter";
import { ArrowLeft, FileCheck2, GitBranch, ShieldCheck, UserCheck } from "lucide-react";
import { PublicPageHero } from "@/components/PublicMarketing";

const repositoryUrl = "https://github.com/pasha555/PeerOnQ";

const sections = [
  {
    title: "1. Signing provider",
    body: "Free code signing provided by SignPath.io, certificate by SignPath Foundation. The signing pipeline is ready; until the SignPath Foundation application is accepted, Windows installers are published unsigned and labeled as such on the download section.",
  },
  {
    title: "2. What is signed",
    body: "The Windows installers for x64 and ARM64 and, inside them, PeerOnQ's own executables and assemblies (PeerOnQ.exe, PeerOnQ.dll, PeerOnQ.*.dll and the session broker). Third-party files keep their vendors' signatures and are not re-signed; the component list and licenses ship in every installer.",
  },
  {
    title: "3. How a signed release is produced",
    body: "A maintainer pushes a version tag. GitHub Actions builds both installers from that tagged commit on GitHub-hosted runners, from the repository's source and pinned package feeds only, runs the unit tests, and submits the unsigned installers to SignPath. Release signing requires manual approval in SignPath. The workflow then verifies every signature, publisher, thumbprint and timestamp and attaches the signed installers with SHA-256 checksums to the GitHub release.",
  },
  {
    title: "4. Team roles",
    body: "Committers and reviewers, who may merge and push release tags: pasha555. Approvers, who approve each release signing request in SignPath: pasha555. Changes to these roles are made by pull request to the repository's CODE_SIGNING_POLICY.md.",
  },
  {
    title: "5. Privacy",
    body: "PeerOnQ uses the network only for signaling (device registration and session setup over an encrypted connection), its cloud service (installation presence and session lifecycle events without screen, input, clipboard, file or voice content), verified updates when enabled, and diagnostics that a user explicitly requests. Session content travels end-to-end encrypted between the two devices. No other data is collected.",
  },
];

export function CodeSigningPolicyPage() {
  return (
    <div>
      <PublicPageHero
        eyebrow="Code signing"
        title={<>Signed releases you can <span className="text-primary">trace.</span></>}
        description="How PeerOnQ's Windows installers are built, signed and verified, and who approves each release."
      />

      <section className="border-b bg-card px-5 py-8 sm:px-8 lg:px-10">
        <div className="mx-auto grid max-w-7xl gap-6 sm:grid-cols-3">
          {[
            [GitBranch, "Built from a tag", "GitHub Actions builds each release from a tagged commit."],
            [UserCheck, "Approved by a person", "Release signing waits for manual approval in SignPath."],
            [FileCheck2, "Verified before publishing", "Signatures and checksums are checked in the workflow."],
          ].map(([Icon, title, text]) => {
            const PolicyIcon = Icon as typeof GitBranch;
            return <div key={title as string} className="flex gap-3"><PolicyIcon className="mt-0.5 h-5 w-5 shrink-0 text-primary" aria-hidden="true" /><div><p className="font-semibold">{title as string}</p><p className="mt-1 text-sm leading-6 text-muted-foreground">{text as string}</p></div></div>;
          })}
        </div>
      </section>

      <section className="px-5 py-16 sm:px-8 sm:py-20 lg:px-10">
        <div className="mx-auto grid max-w-6xl gap-10 lg:grid-cols-[15rem_1fr]">
          <aside className="lg:sticky lg:top-28 lg:self-start">
            <ShieldCheck className="h-6 w-6 text-primary" aria-hidden="true" />
            <p className="mt-4 font-semibold">Code signing policy</p>
            <p className="mt-2 text-sm text-muted-foreground">Last updated: October 25, 2026</p>
            <a href={`${repositoryUrl}/blob/main/CODE_SIGNING_POLICY.md`} className="mt-4 block text-sm font-medium text-primary hover:underline">Policy in the repository</a>
            <Link href="/privacy" className="mt-2 block text-sm font-medium text-primary hover:underline">Privacy overview</Link>
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
