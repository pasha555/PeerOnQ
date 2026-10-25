# Incident response plan

## Severity and containment

Treat release-key/certificate theft, authentication bypass, unauthorized screen/control/content
access, hidden persistence, remote-code execution, and update-origin compromise as critical. Suspend
publishing and staged rollout, preserve immutable CI/deployment/audit evidence, restrict affected
credentials, and assign an incident commander. Do not erase endpoints or logs before evidence is
preserved and legal/privacy obligations are assessed.

For signaling/TURN abuse, remove the instance from readiness, rotate the affected TURN pool/secret,
expire resume state, and require reauthentication. For update compromise, revoke the certificate when
appropriate, rotate the manifest key/update origin, raise the security floor in a higher clean
release, and publish verification instructions through an independently controlled channel.

## Investigation

Record UTC timeline, affected versions/devices/regions, entry vector, permissions/content potentially
exposed, signing/CI/deployment changes, indicators, and confidence. Use sanitized audit/diagnostics;
never ask users to send screen frames, clipboard contents, secrets, tokens, device private keys, or
unnecessary documents.

## Recovery

Restore from reviewed source and pinned dependencies, rerun the complete quality/security/release
matrix, obtain an independent review for critical incidents, deploy to canary, and monitor explicit
success/failure thresholds. Resume rollout only after owners approve containment, eradication, and
customer communication.

## Post-incident

Publish a factual impact/remediation report appropriate to the audience, rotate remaining related
credentials, track root-cause actions with owners/dates, update this threat model and tests, and review
notification/regulatory duties. Evidence retention and access must follow the incident legal hold,
not ordinary application-log retention.
