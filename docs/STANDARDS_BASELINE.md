# PeerOnQ standards baseline

Verified against official sources on 2026-08-17. A reference here is a target/control source, not a
claim of certification or complete conformance.

| Standard | Current reference | PeerOnQ use and R0 status |
|---|---|---|
| NIST SSDF | [SP 800-218 v1.1](https://csrc.nist.gov/pubs/sp/800/218/final) | Change review, dependency scanning, secret scanning, signed releases and evidence gates are implemented; no formal practice-by-practice audit yet (`PARTIAL`). |
| OWASP ASVS | [5.0.0](https://owasp.org/www-project-application-security-verification-standard/) | Baseline for web/API auth, session, validation, logging and crypto reviews; no independent ASVS verification (`PARTIAL`). |
| OWASP MASVS | [2.1.0](https://github.com/OWASP/masvs/releases/tag/v2.1.0) | Required baseline for future Android/iOS storage, auth, network, platform and privacy work. No mobile client exists and no conformance is claimed (`PLANNED`). |
| WCAG | [WCAG 2.2](https://www.w3.org/TR/WCAG22/) | Web surfaces target AA; automated checks exist but keyboard/screen-reader/zoom/manual audits remain (`PARTIAL`). |
| SLSA | [v1.2](https://slsa.dev/spec/v1.2/) | Pinned CI actions, signed artifacts, SBOM attestation and provenance path exist; hosted official release provenance was not produced in R0 (`PARTIAL`). |
| SPDX | [3.0](https://spdx.dev/use/specifications/) | Current release builder emits/validates SPDX 2.2. Upgrade and consumer validation for 3.0 is P1; do not relabel 2.2 output (`PARTIAL`). |
| CycloneDX | [1.7](https://cyclonedx.org/specification/overview/) | Accepted future interoperable BOM format; not currently emitted (`NOT_IMPLEMENTED`). |
| OpenTelemetry | [specification 1.60.0](https://opentelemetry.io/docs/specs/otel/) | .NET services emit traces/metrics/logs through pinned OTel packages; semantic-convention conformance is not independently certified (`PARTIAL`). |
| OpenAPI | [3.2.0 current](https://spec.openapis.org/oas/latest.html) | Repository contract intentionally remains OAS 3.1.0 for current generator compatibility. Evaluate 3.1.2/3.2 separately; never hand-edit generated clients (`PARTIAL`). |
| Semantic Versioning | [2.0.0](https://semver.org/) | Required for public product/API releases. Current component version drift is P1 (`PARTIAL`). |
| Post-quantum KEM | [NIST FIPS 203](https://csrc.nist.gov/pubs/fips/203/final) | No ML-KEM implementation or compliance claim. Track crypto agility and protocol negotiation before adoption (`NOT_IMPLEMENTED`). |

## Real-time protocol references

WebRTC/ICE/TURN work is reviewed against the IETF standards family: [WebRTC overview RFC
8825](https://www.rfc-editor.org/rfc/rfc8825), [ICE RFC 8445](https://www.rfc-editor.org/rfc/rfc8445),
[Trickle ICE RFC 8838](https://www.rfc-editor.org/rfc/rfc8838), [STUN RFC
8489](https://www.rfc-editor.org/rfc/rfc8489), [TURN RFC 8656](https://www.rfc-editor.org/rfc/rfc8656),
[DTLS-SRTP RFC 5764](https://www.rfc-editor.org/rfc/rfc5764), and [WebRTC data channels RFC
8831](https://www.rfc-editor.org/rfc/rfc8831). PeerOnQ uses SIPSorcery and coturn; R0 did not run an
independent protocol-conformance laboratory.

## Release rule

Before a production release, owners must map changed controls, record objective evidence, name
unmet controls, and distinguish a project target from a third-party certification. Standards
versions are rechecked from official sources at each release gate.
