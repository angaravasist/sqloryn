# Security policy

## Supported versions

There are no supported SQLoryn releases yet. The current source is experimental and must not be used against sensitive or production databases.

## Confidential vulnerability reporting

Use GitHub's **Security → Report a vulnerability** option to submit a private security advisory. Do not publish security issues, exploits, credentials, screenshots of production data or confidential logs in public GitHub issues or pull requests.

Include affected version/commit, impact, reproducible conditions and a safe proof of concept. Maintainers will triage responsibly; no formal response SLA is established for this early-stage project.

## Security commitments

Credentials must use system-secure storage. TLS verification must not be silently disabled. SQL inputs, database metadata, imported files and extension messages are untrusted. Releases must be signed and notarised before general distribution. CI must not expose protected tokens to untrusted pull requests.

Security fixes may be developed privately and published with coordinated disclosures where appropriate.
