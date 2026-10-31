# Instructions for Codex and other coding agents

Read README.md, SECURITY.md, docs/ARCHITECTURE.md, docs/FEATURE_MATRIX.md and docs/SECURITY_BASELINE.md before making changes.

Use docs/README.md as the context map, docs/STATE.md for current evidence and
pending work, and docs/TESTING.md for exact commands. Update relevant decisions,
specs, dependency inventory and evidence with code; prior chats are not a source
of implementation truth. Candidate capabilities are not supported features.

## Non-negotiable safeguards

- No claim of full SSMS parity without evidence from tests against an actual supported SQL Server target.
- Do not copy proprietary SSMS code, artwork, assets, binaries, documentation text or trademarks. Audit licence and redistribution rights before importing dependencies.
- No credentials, API keys, client certificates, connection strings or database contents in the repository, fixtures, logs, CI or issue comments.
- Do not introduce AI queries, telemetry or external cloud services without an explicit opt-in privacy design and a security review.
- Do not run tests or migrations against production databases. Use disposable isolated SQL Server test environments populated with synthetic data.
- Never silently execute destructive GUI operations; require clear preview/confirmation and handle failures safely.
- All user-controlled inputs, SQL files, extension outputs and SQL Server metadata are untrusted.
- Keep GitHub Actions permissions read-only by default. Never use pull_request_target to execute untrusted PR code; do not bypass required checks.
- Pin external Actions to full commit SHAs. Keep secrets inaccessible to fork PR checks.
- Never commit Apple signing keys or notarisation credentials.
- Work on feature branches and submit PRs. Do not merge or bypass protections without explicit maintainer approval.

## Development sequence

1. Specify a bounded feature and acceptance tests; update the feature matrix.
2. Investigate platform support, licences, security boundaries and upstream dependencies.
3. Implement minimal behaviour with tests.
4. Test on isolated SQL Server instances and, where applicable, on macOS arm64/x64.
5. Document evidence and limitations in the PR; request review.

Initial gate: validate the SQL tooling architecture and real connection/query behaviour before expanding into interface and server management features.
