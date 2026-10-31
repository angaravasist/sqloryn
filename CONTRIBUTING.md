# Contributing to SQLoryn

Please open an issue or proposal describing the target SSMS workflow, affected SQL Server versions and acceptance criteria before substantial implementation.

- Create a feature branch and open a pull request targeting `main`.
- Use synthetic data and disposable test SQL Server instances. Never use production databases.
- Include tests, security/privacy impact, licence considerations and sanitised evidence.
- Run `python3 scripts/repo_preflight.py` before opening a pull request.
- Follow [build/test commands](docs/TESTING.md), including component assertions,
  locked dependency audit and any explicitly approved isolated SQL acceptance run.
- Update the [context map](docs/README.md), relevant spec, feature matrix and
  [evidence/current state](docs/STATE.md) in the same PR as behaviour. Clearly
  distinguish proposed, implemented/unverified and target-specific verified work.
- Keep changes small, reproducible and reviewable.
- Never upload secrets, private certificates, connection strings, database dumps or protected Microsoft assets.
- Follow `AGENTS.md` for agent-generated changes.

Code owners review changes when available. As the project grows, independent approvals may become mandatory.

`repo-safeguards` remains the required aggregate check; do not merge a PR with
failed component checks or claim a skipped SQL suite passed. No release or merge
is authorised merely because tests pass. The assertion executables and test
runner do not accept production endpoints or stored credentials.
