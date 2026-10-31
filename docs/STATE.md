# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-10-31. Experimental; no release support.

## Work under review

Branch `codex/macos-sqlclient-qualification`, based on `d2eebbc`.
Draft [PR #5](https://github.com/angaravasist/sqloryn/pull/5) for Mac SSH/loopback
orchestration ([ADR-002](decisions/002-macos-ssh-sql-probe.md),
[SPEC-001](specs/001-sqlclient-probe.md) PROBE-11). Review corrections address
prepare→provision lifecycle, effective SSH `-L` forwarding, local-only Docker
binding, exception-safe tunnel/cleanup ownership and safe recovery without
helper overwrite. No approved remote host is configured; **native Mac arm64 and
Mac x64 SQL integration remain unverified**. Do not mark this milestone complete
until those runs pass.

## Merged baseline (main)

- [PR #3](https://github.com/angaravasist/sqloryn/pull/3) established the
  SqlClient experiment ([ADR-001](decisions/001-sqlclient-probe.md)).
- [PR #2](https://github.com/angaravasist/sqloryn/pull/2) installed
  `actions/checkout` v7.0.1; [PR #4](https://github.com/angaravasist/sqloryn/pull/4)
  installed `actions/setup-dotnet` v6.0.0. Older workflow runs may still show
  prior pins; current workflow source is authoritative. The checkout Node 20
  maintenance item from the probe PR is **resolved** in source.
- `origin/main` at technical assessment start for this branch: `d2eebbc`.

## Evidence actually obtained

- All 32 reference-pack entries read; all manifest hashes verified.
- Python safety tests and C# component assertions pass on Mac component CI and
  local development hosts for the probe baseline.
- [Hosted run 41192800910](https://github.com/angaravasist/sqloryn/actions/runs/41192800910)
  at original PR #5 head `964427a`: 35 C# assertions and 24 Python tests on Linux
  and both Mac architectures (pre-correction; retained for history).
- [Hosted run 41193677357](https://github.com/angaravasist/sqloryn/actions/runs/41193677357)
  at correction head `e11242a`: 35 C# assertions and 37 Python tests on Ubuntu
  24.04, macos-15 and macos-15-intel; `repo-safeguards` passed; SQL integration
  skipped (not requested). [CodeQL run 41193673892](https://github.com/angaravasist/sqloryn/actions/runs/41193673892)
  succeeded for C#, Python and Actions on the same head.
- [Hosted run 41194422292](https://github.com/angaravasist/sqloryn/actions/runs/41194422292)
  workflow_dispatch SQL integration **passed** at PR #5 head `8285892` (Linux
  local-runner regression of the refactored path on GitHub-hosted Ubuntu x64).
  This re-qualifies Linux PROBE-10 for this branch; it does **not** establish
  Mac-to-server evidence.
- [Hosted run 41188623012](https://github.com/angaravasist/sqloryn/actions/runs/41188623012)
  remains historical Linux E2 at `5ef7c380` (pre-PROBE-11 refactor).
- Local Mac (Apple M3, native arm64) prep on 2026-10-31: SDK 10.0.403 in
  `~/.dotnet`, Release build, 35 C# assertions, 37 Python tests, probe DLL ready.
  Docker Desktop here is Linux **aarch64** — unsuitable for SQL Server evidence.
  No maintainer-approved Linux x64 SSH host is configured for PROBE-11.
- Native Mac arm64 → SQL Server: **unverified** (no approved host).
- Native Mac x64 → SQL Server: **unverified** (requires an Intel Mac client plus
  approved host; this machine cannot supply native x64 client evidence).
- Earlier failed probe runs remain visible: Docker internal-only port publishing
  ([41188114994](https://github.com/angaravasist/sqloryn/actions/runs/41188114994)),
  XACT_STATE smallint handling
  ([41188319700](https://github.com/angaravasist/sqloryn/actions/runs/41188319700)),
  and transaction-state sampling inside SELECT
  ([41188500385](https://github.com/angaravasist/sqloryn/actions/runs/41188500385)).
- [CodeQL run 41190629514](https://github.com/angaravasist/sqloryn/actions/runs/41190629514)
  includes C#, Python and Actions (baseline). Re-check CodeQL on the revised PR
  head after push. C# uses build mode `none`. See
  [security baseline](SECURITY_BASELINE.md). No security settings were changed
  for this note.
- Live repository protections verified read-only; see [security baseline](SECURITY_BASELINE.md).

The maintainer approved PR #3's disposable GitHub-hosted Linux x64 SQL Server
Developer test run, its test-use terms and temporary credentials. That approval
does **not** authorise a new remote host, production access, retained credentials,
merge or release.

## Next bounded outcome and release gates

Implement and land PROBE-11 orchestration behind an approved Linux x64 SSH host
and known_hosts material, then record native Mac arm64 and Mac x64 SQL evidence.
Keep UI expansion behind that gate. Assess SQL Tools Service's exact
artifacts/licences and a minimal metadata/language-service integration before
choosing a desktop shell. Test ordinary CA trust/name/expiry rejection,
restricted logins and interruption of transactions.

No GUI, Keychain, Entra, saved profiles, user-script/GO parser, administration,
installer, signing/notarisation or full-parity claim exists. Large-cell memory,
network partition, accessibility and packaged multi-architecture evidence are
absent. [Threat model](THREAT_MODEL.md) and [dependency assessment](DEPENDENCIES.md)
track unresolved boundaries. Independent review and maintainer release approval
remain necessary before distributing an application.
