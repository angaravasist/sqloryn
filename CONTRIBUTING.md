# Contributing to SQLoryn

Please open an issue or proposal describing the target SSMS workflow, affected SQL Server versions and acceptance criteria before substantial implementation.

- Create a feature branch and open a pull request targeting `main`.
- Use synthetic data and disposable test SQL Server instances. Never use production databases.
- Include tests, security/privacy impact, licence considerations and sanitised evidence.
- Run `python3 scripts/repo_preflight.py` before opening a pull request.
- Keep changes small, reproducible and reviewable.
- Never upload secrets, private certificates, connection strings, database dumps or protected Microsoft assets.
- Follow `AGENTS.md` for agent-generated changes.

Code owners review changes when available. As the project grows, independent approvals may become mandatory.
