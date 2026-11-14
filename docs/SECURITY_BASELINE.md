# Repository security settings

## Repository

- `main` is protected: pull requests required, the `repo-safeguards` check required, conversations resolved before merging, no force pushes or deletion.
- Squash merging only; merged branches are deleted automatically.
- Private vulnerability reporting, the dependency graph, Dependabot alerts and security updates, secret scanning and push protection are enabled.
- CodeQL default setup scans C#, Python and GitHub Actions.

## CI

- Workflows run on `pull_request`, never `pull_request_target`, so untrusted code never gets secrets or a write token.
- The default workflow token is read-only, and workflows cannot approve pull requests.
- External actions are pinned to full commit SHAs.
- Workflows from external contributors need maintainer approval before they run.
- CI has no signing keys, persistent credentials, self-hosted runners or production databases. The SQL Server job creates temporary credentials for a disposable container and deletes it afterwards.
