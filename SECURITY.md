# Security policy

## Supported versions

There are no supported SQLoryn releases yet. The source is an unsigned preview. Do not use it against sensitive or production databases.

## Reporting a vulnerability

Use GitHub's **Security → Report a vulnerability** option to submit a private security advisory. Do not post security issues, exploits, credentials, screenshots of production data or confidential logs in public issues or pull requests.

Include the affected version or commit, the impact, how to reproduce it and a safe proof of concept. Reports are triaged on a best-effort basis; there is no formal response time.

## Security practices

- Passwords are kept in memory, or in the macOS Keychain if you choose **Remember password**. They are never written to settings or profile files.
- Encryption is mandatory, and TLS certificate validation is never silently disabled.
- SQL files, database metadata and imported files are treated as untrusted.
- CI never exposes protected tokens to pull requests from forks.

Security fixes may be developed privately and published with coordinated disclosure.
