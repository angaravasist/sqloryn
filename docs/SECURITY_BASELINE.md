# GitHub security baseline

## Repository settings — verify in GitHub UI

- Public repository with active `Protect main` ruleset.
- Pull requests required; block default branch deletion and force pushing.
- Squash-only merging, resolve conversations before merging, auto-delete merged branches.
- Single-maintainer exception: zero mandatory reviews until an independent trusted reviewer is available.
- GitHub Actions default token permissions read-only; workflows cannot approve PRs.
- GitHub Actions pinned to full commit SHAs; allowlisted external actions only.
- Approval required before executing workflows from external contributor PRs.
- Private vulnerability reporting, dependency graph, Dependabot alerts/security updates, secret scanning and push protection enabled.
- CodeQL default setup enabled (scans begin when supported application code exists).

## CI security

- Use `pull_request`, not `pull_request_target` for untrusted code.
- No secrets, signing keys, write tokens, self-hosted privileged runners or production databases in PR workflows.
- Require successful CI status checks in the branch ruleset **after the check has successfully run at least once**.
- Subsequent app CI should include TypeScript, Rust and .NET lint/build/test/security checks as implementation requires.

## Release security (future)

- Use separate protected publishing environments, scoped credentials and manual approvals.
- Sign and notarise releases using Apple tooling. Publish checksums/provenance and a third-party notices file.
- Verify updater signatures and defend against downgrade attacks.
- Never place notarisation credentials in application source, Actions logs or forks.

## Known limitation

This repository is not automatically secure merely because this document exists. Confirm actual settings and ruleset enforcement in GitHub, validate CI results, and independently review security-sensitive code changes.

## Read-only verification — 2026-10-31

Live API checks confirmed active ruleset `Protect main` (26247104), default-branch
PRs, strict required `repo-safeguards`, conversation resolution, no force pushes
or deletion, zero mandatory approvals and no bypass actors. Squash-only merge,
auto-delete merged branches, read-only workflow tokens and disabled Actions PR
approval were confirmed. Actions permit GitHub-owned actions; verified/pattern
allowlists are empty. Workflow SHA pins are enforced in source; global SHA-only
enforcement was not independently verified.

Private vulnerability reporting, Dependabot security updates, secret scanning
and push protection are enabled. Non-provider secret patterns and validity
checks are disabled. [CodeQL run 41190629514](https://github.com/angaravasist/sqloryn/actions/runs/41190629514)
includes **C#, Python and Actions**. C# analysis uses build mode `none`. Treat
that run as the current default-setup evidence; do not infer additional
security-setting changes from documentation updates alone. No security settings
were changed for this note. External-contributor approval policy and
dependency-alert triage were not independently verified in this inspection.

The proposed workflow preserves the required check name and read-only token.
It adds Mac/Linux component gates and a manually authorised disposable SQL job.
Neither signing credentials nor persistent test/database secrets are configured.
