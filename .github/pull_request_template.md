## Summary

<!-- What changes and why. Link the related issue. -->

## Root cause

<!-- For fixes: what caused the problem. -->

## Validation

<!-- Exact commands and environments. Name skipped or mocked cases; they are not passes. -->

- [ ] `dotnet test PeerOnQ.slnx -c Release`
- [ ] `pnpm run typecheck`, `pnpm run lint`, `pnpm run test`, `pnpm run build`
- [ ] Physical-device or network tests (describe below)

## Compatibility, security and rollback

<!-- Protocol, data or migration impact; permission or data-collection changes; how to roll back. -->

## Checklist

- [ ] Tests added or updated with the production change
- [ ] Documentation and [CHANGELOG](../CHANGELOG.md) updated for user-visible changes
- [ ] No secrets, personal data, generated output or binaries
- [ ] Each commit is signed off (`git commit -s`)
