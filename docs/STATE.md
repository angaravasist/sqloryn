# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-10-31. Experimental; no release support.

## Work under review

Branch `codex/macos-sqlclient-qualification`, based on `d2eebbc`.
Draft PR for Mac SSH/loopback orchestration ([ADR-002](decisions/002-macos-ssh-sql-probe.md),
[SPEC-001](specs/001-sqlclient-probe.md) PROBE-11). No approved remote host is
configured; **native Mac arm64 and Mac x64 SQL integration remain unverified**.
Do not mark this milestone complete until those runs pass.

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
- [Hosted run 41188623012](https://github.com/angaravasist/sqloryn/actions/runs/41188623012)
  passed all component, SQL integration and required aggregate checks at commit
  `5ef7c38051b9a43d53ad2ccc2690f708ae15d97e` on 2026-10-31.
  Each hosted Mac arm64/x64 and Ubuntu x64 job passed 35 C# assertions and 11
  Python tests, Release build and locked restore/audit. (Python safety coverage
  grows with PROBE-11 unit tests; count the live job output.)
- E2 (local Linux client): Ubuntu 24.04.5 x64 client, SDK 10.0.403/runtime 10.0.14,
  SqlClient 7.1.1, SQL Server 2022 Developer 16.0.4305.5 on the pinned Ubuntu 22.04
  image. SQL auth used a generated disposable admin credential; TLS used exact
  pinning. PROBE-02–10 passed. Image SHA-256:
  `cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`.
- Native Mac arm64 → SQL Server: **unverified** (no approved host).
- Native Mac x64 → SQL Server: **unverified** (no approved host).
- Earlier failed probe runs remain visible: Docker internal-only port publishing
  ([41188114994](https://github.com/angaravasist/sqloryn/actions/runs/41188114994)),
  XACT_STATE smallint handling
  ([41188319700](https://github.com/angaravasist/sqloryn/actions/runs/41188319700)),
  and transaction-state sampling inside SELECT
  ([41188500385](https://github.com/angaravasist/sqloryn/actions/runs/41188500385)).
- [CodeQL run 41190629514](https://github.com/angaravasist/sqloryn/actions/runs/41190629514)
  includes C#, Python and Actions. C# uses build mode `none`. See
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
