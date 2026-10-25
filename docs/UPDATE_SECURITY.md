# Update security design

## Signed objects

The manifest is a JSON envelope containing base64 of the exact UTF-8 payload and an ECDSA P-256 DER
signature. The compiled client trust anchor is the signing key ID plus SPKI public key. The payload
binds product ID, version, minimum supported version, stable/beta channel, validity window, rollout
percentage/seed, emergency flag, and an architecture-specific HTTPS MSI URL, byte length and SHA-256.

The client rejects unknown fields/schema, malformed or oversized envelopes, untrusted key/signature,
future/expired/overlong validity, wrong product/channel/architecture, downgrade, security-floor
violation, invalid package descriptor, HTTP or cross-host redirect, length/hash mismatch, failed
WinVerifyTrust chain/revocation policy, and a publisher fingerprint outside the compiled allowlist.

## Download and installation

Downloads are streaming and bounded to the signed length and a 2 GiB client cap. SHA-256 is computed
incrementally into a private `.partial` file. The final response must remain at the exact signed HTTPS
URL; even a same-host redirect is rejected. Only after hash, Authenticode chain, and publisher checks
pass is the file atomically renamed. Installation requires a visible confirmation and no active
session; signed size, SHA-256, Authenticode chain, and publisher are all reverified immediately before
launching system `msiexec.exe`.

MSI major upgrade is transactional. A failed install lets Windows Installer restore the prior files.
An intentionally bad installed release is recovered by publishing a higher signed corrective version,
not by allowing an unsigned or below-floor downgrade. Deferred restart remains user controlled except
where an emergency manifest makes the update required; even then installation is not hidden.

## Rollout and channels

Stable and beta are separate signed scopes. A SHA-256 bucket of the local rollout ID and manifest seed
produces deterministic staged allocation. Clients below the minimum supported version and security
emergency manifests are classified required. Reusing a seed keeps the ring stable; changing it must be
an explicit release decision.

## Key and certificate operations

- Keep the ECDSA manifest private key offline or in an HSM-backed CI signing service. CI receives it
  only through a protected release environment and deletes ephemeral files on every outcome.
- Keep Authenticode PFX/password in the CI secret store; production should migrate to key-vault/HSM
  signing so private key material is non-exportable.
- Rotate the manifest key by shipping clients that trust the new key before signing exclusively with
  it. The current single-key envelope requires an overlap release; never replace a key out of band.
- Rotate publisher certificates by compiling an overlap allowlist, dual-period signing/testing, then
  removing the old fingerprint in a later floor-raising release.
- On compromise, stop publication, protect the update origin, revoke the Authenticode certificate,
  rotate the manifest key via the incident plan, and distribute a higher clean version through a
  separately verified channel. Do not lower certificate verification or version floors.

Release commands are in `scripts/windows/build-phase5-release.ps1`. Official builds always compile
the complete update trust set. Unsigned development builds remain update-disabled by default, but a
controlled development/public-pilot client may compile the same public manifest key, key ID,
publisher fingerprint, channel, and HTTPS update origin. That bootstrap can upgrade only to an MSI
whose signed manifest, hash, Authenticode chain, and publisher all pass the normal production checks;
no private signing material is embedded and Production ignores runtime trust overrides.
