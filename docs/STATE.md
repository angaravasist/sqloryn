# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-10-31. Experimental; no release support.

## Work under review

Branch `codex/sqlclient-connection-probe`, based on `bdcf928`.
[PR #3](https://github.com/angaravasist/sqloryn/pull/3). [ADR-001](decisions/001-sqlclient-probe.md)
selects a direct SqlClient experiment, not the desktop stack. [SPEC-001](specs/001-sqlclient-probe.md)
defines PROBE-01–10. Source, test runner, locked dependency graph and AI-first
context are implemented; the bounded Linux SQL Server acceptance suite passes.

## Evidence actually obtained

- All 32 reference-pack entries read; all manifest hashes verified.
- Eleven Python safety tests passed on local macOS 27.0.1 arm64; the licence-free
  invocation exited 2 before Docker work, as intended.
- Initial NuGet restore failed on two PKCS advisories; fixed with 10.0.14 and
  restored successfully with SDK 10.0.403. No warning suppression.
- Official local SDK archive checked against published SHA-512 before extraction
  to a temporary directory; system SDK installation unchanged.
- Release build passed with zero warnings/errors; 35 C# component assertions
  passed with .NET 10.0.14 on local macOS arm64. Locked restore/audit, repository
  path checks and whitespace checks passed. Build servers were disabled locally
  after a sandboxed shared-build process stalled; no compiler warning was ignored.
- Windows native SNI files were absent from both built outputs.
- [Hosted run 41188623012](https://github.com/angaravasist/sqloryn/actions/runs/41188623012)
  passed all component, SQL integration and required aggregate checks at commit
  `5ef7c38051b9a43d53ad2ccc2690f708ae15d97e` on 2026-10-31.
  Each hosted Mac arm64/x64 and Ubuntu x64 job passed 35 C# assertions and 11
  Python tests, Release build and locked restore/audit.
- E2: Ubuntu 24.04.5 x64 client, SDK 10.0.403/runtime 10.0.14, SqlClient 7.1.1,
  SQL Server 2022 Developer 16.0.4305.5 on the pinned Ubuntu 22.04 image.
  SQL auth used a generated disposable admin credential; TLS used exact pinning.
  PROBE-02–10 passed, including invalid pin/login, result fidelity, late errors,
  timeout, observed cancellation/session reuse, 10,000 rows and resource cleanup.
  Image SHA-256: `cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718`.
- Earlier failed runs remain visible: Docker internal-only port publishing
  ([41188114994](https://github.com/angaravasist/sqloryn/actions/runs/41188114994)),
  XACT_STATE smallint handling
  ([41188319700](https://github.com/angaravasist/sqloryn/actions/runs/41188319700)),
  and transaction-state sampling inside SELECT
  ([41188500385](https://github.com/angaravasist/sqloryn/actions/runs/41188500385)).
  The final check samples state before SELECT and retains all assertions.
  No failing acceptance check was disabled.
- CI warns that baseline checkout v4 targets deprecated Node 20; GitHub runs it
  on Node 24. A reviewed checkout update remains maintenance work.
- Native ARM Docker host detected; no local SQL Server emulation was attempted.
- Live repository protections verified read-only; see [security baseline](SECURITY_BASELINE.md).

The maintainer explicitly approved this PR's disposable GitHub-hosted Linux x64
SQL Server Developer test run, its test-use terms and temporary credentials.
This does not authorise production access, retained credentials, merge or release.

## Next bounded outcome and release gates

The probe milestone is complete for the recorded Linux target. Next, test a real
Mac client against a supported isolated x64 server. Keep UI expansion behind that
gate. Assess SQL Tools Service's exact artifacts/licences and a minimal metadata/
language-service integration before choosing a desktop shell. Test ordinary CA
trust/name/expiry rejection, restricted logins and interruption of transactions.

No GUI, Keychain, Entra, saved profiles, user-script/GO parser, administration,
installer, signing/notarisation or full-parity claim exists. Large-cell memory,
network partition, accessibility and packaged multi-architecture evidence are
absent. [Threat model](THREAT_MODEL.md) and [dependency assessment](DEPENDENCIES.md)
track unresolved boundaries. Independent review and maintainer release approval
remain necessary before distributing an application.
