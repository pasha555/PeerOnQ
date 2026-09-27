# Security reference

Read for auth, permissions, secrets, network exposure, user data, cryptography, remote-control capabilities, or security-sensitive infrastructure.

## Non-negotiable rules
- Never commit or log secrets, credentials, access tokens, private keys, recovery codes, production connection strings, or full sensitive request bodies.
- Use environment/config secret injection already established by the project.
- Validate authorization server-side; UI visibility is not authorization.
- Apply least privilege to services, files, database roles, tokens, and session capabilities.
- Fail closed for authentication/authorization uncertainty.
- Use mature platform cryptography/TLS; do not invent encryption protocols or custom ciphers.
- Use cryptographically secure randomness for security tokens/identifiers where unpredictability matters.
- Bound request/body/file sizes and validate untrusted input.
- Protect state-changing web actions with the framework-appropriate anti-CSRF/session controls when relevant.
- Use safe parameterized DB access; never concatenate untrusted input into queries or shell commands.
- Avoid command injection, path traversal, SSRF, unsafe deserialization, and open redirects.
- Rate-limit or otherwise protect authentication, pairing, reset, invitation, and expensive public endpoints.
- Security-sensitive logs should support auditability without containing secrets.

## Dependency changes
Before adding a security-critical dependency, prefer an established dependency already in the repo. If adding one is necessary, justify scope and avoid abandoned/unmaintained packages.

## Completion
A security-sensitive feature is not complete because the happy path works. Verify denied access, expiry/revocation, malformed input, restart/reconnect behavior, and relevant audit events.
