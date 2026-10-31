# Current engineering state

Owner: SQLoryn maintainer. Reviewed: 2026-10-31. Experimental; no release support.

## Work under review

Branch `codex/sqlclient-connection-probe`, based on `bdcf928`.
PR/evidence links will be recorded when checks complete. [ADR-001](decisions/001-sqlclient-probe.md)
selects a direct SqlClient experiment, not the desktop stack. [SPEC-001](specs/001-sqlclient-probe.md)
defines PROBE-01–10. Source, test runner, locked dependency graph and AI-first
context are implemented; real-server acceptance is not yet established.

## Evidence actually obtained

- All 32 reference-pack entries read; all manifest hashes verified.
- Ten Python safety tests passed on local macOS 27.0.1 arm64; the licence-free
  invocation exited 2 before Docker work, as intended.
- Initial NuGet restore failed on two PKCS advisories; fixed with 10.0.14 and
  restored successfully with SDK 10.0.403. No warning suppression.
- Official local SDK archive checked against published SHA-512 before extraction
  to a temporary directory; system SDK installation unchanged.
- Release build passed with zero warnings/errors; 35 C# component assertions
  passed with .NET 10.0.14 on local macOS arm64. Locked restore/audit, repository
  path checks and whitespace checks passed. Build servers were disabled locally
  after a sandboxed shared-build process stalled; no compiler warning was ignored.
- Windows native SNI files were absent from both built outputs. SQL acceptance
  and hosted Mac/Linux results are pending, not PASS.
- Native ARM Docker host detected; no local SQL Server emulation was attempted.
- Live repository protections verified read-only; see [security baseline](SECURITY_BASELINE.md).

The maintainer explicitly approved this PR's disposable GitHub-hosted Linux x64
SQL Server Developer test run, its test-use terms and temporary credentials.
This does not authorise production access, retained credentials, merge or release.

## Next bounded outcome and release gates

Complete the probe and record exact CI/client/server evidence, then test a real
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
