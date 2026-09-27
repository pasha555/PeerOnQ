# Contributing to PeerOnQ

Thank you for helping improve PeerOnQ. By contributing, you agree that your contribution is provided
under the repository's MIT license and certify it with a Developer Certificate of Origin sign-off.

## Before changing code

1. Read `AGENTS.md`, `PROJECT_MAP.md`, `ROUTES_MAP.md`, `AI_CHANGELOG.md`, and the nearest architecture,
   security, accessibility, or integration guide.
2. Open an issue for security-sensitive behavior, schema/protocol changes, new dependencies, or
   cross-cutting architecture work. Security reports follow `SECURITY.md`, not a public issue.
3. Keep the patch focused. Do not edit generated clients, vendored UI primitives, or the mockup
   sandbox. Do not add network calls to the offline web product.

## Setup and validation

Follow `docs/LOCAL_DEVELOPMENT.md`. Use `pnpm`, never npm or Yarn. At minimum run the narrow checks
for your change and report anything you could not run. Typical gate:

```powershell
dotnet build PeerOnQ.slnx -c Release
dotnet test PeerOnQ.slnx -c Release --no-build
pnpm run typecheck
pnpm run lint
pnpm run test
pnpm run build
./scripts/quality/test-brand-purity.ps1
./scripts/security/scan-repository-secrets.ps1
```

Physical/public tests must name the real environment. A skipped or mocked case is not a pass.

## Pull requests

- Explain behavior, root cause, compatibility/security impact, exact validation, limitations and
  rollback.
- Add/update tests with production code. Preserve existing data migration and opaque protocol IDs.
- Update repository maps and `AI_CHANGELOG.md` for non-trivial changes.
- Add no secrets, personal data, generated output, binaries, or production connection strings.
- Add a `Signed-off-by: Name <email>` line to each commit (`git commit -s`).

Maintainers may request threat-model, performance, accessibility, migration, SBOM/license, or
physical-device evidence proportional to the change.
