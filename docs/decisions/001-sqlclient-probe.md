# ADR-001 — direct driver baseline before choosing a shell

Accepted for the experiment, not a final app architecture. Owner: SQLoryn maintainer.
Date: 2026-10-31. [SPEC-001](../specs/001-sqlclient-probe.md), [evidence](../STATE.md).

## Decision and alternatives

At `bdcf928`, the repository had governance/path checks but no database code.
Use .NET 10 and Microsoft.Data.SqlClient 7.1.1 for a reproducible connection/query
probe. A UI choice cannot settle transport or redistribution risks. Do not create
a new TDS implementation or a production service abstraction for an experiment.

| Candidate | Assessment |
|---|---|
| Direct SqlClient | Maintained provider, MIT managed source, cross-platform .NET support. Selected for the smallest transport baseline. 7.x separates the optional Azure auth provider; it is not installed. 7.1 is STS and requires review when a successor ships. 6.1 LTS has a longer window but a broader default auth dependency graph. |
| SQL Tools Service | MIT source; `6.0.20261028.1` offers both Mac architectures on .NET 10. Strong candidate for language services, metadata, scripting and plans. Binary availability does not prove redistribution rights, API stability or tested Mac behaviour. Retain; do not bundle yet. |
| VS Code MSSQL / Code-OSS | MIT source may support a separately audited source build. Marketplace-acquired Microsoft extensions and Marketplace access are restricted to Microsoft's product family. A fork cannot assume access. Host integration/fork maintenance adds no value to this baseline. |
| DacFx / SqlPackage / SMO | Candidates for schema/admin breadth. SqlPackage documents cross-platform availability; inspect exact package/binary terms and feature-level Mac support before importing. |
| AppKit/SwiftUI, Tauri, Electron | All remain candidates. Decide with editor/tree/grid accessibility, resource, IPC and signing evidence; no framework is selected or installed. |
| ODBC / custom TDS | ODBC introduces native-driver packaging; a new TDS client duplicates maintained protocol code. Neither meets a missing requirement in this probe. |

## Security/licensing consequences

The initial restore failed on SqlClient's default `System.Security.Cryptography.Pkcs
9.0.18`, affected by GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx. Pin patched
10.0.14; retain transitive auditing and warnings-as-errors. Pin SDK 10.0.403
(runtime 10.0.14) from Microsoft's release metadata.

MIT does not cover the separately licensed Windows native SNI package. Both
projects exclude all of its assets; restore still obtains it. Its development/
test-use terms were inspected, but this decision approves no binary distribution.
See [exact inventory](../DEPENDENCIES.md). No binaries are committed or published.

Use a new disposable SQL Server, certificate pinning, no retries/pooling, fixed
SQL and sanitised evidence. Linux x64 server evidence cannot establish Mac client
compatibility. Reject Apple Silicon server emulation as unsupported by Microsoft.

## Primary sources reviewed 2026-10-31

- [SqlClient lifecycle](https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-driver-support-lifecycle), [7.1.1 package](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.1.1), [MIT licence](https://github.com/dotnet/SqlClient/blob/v7.1.1/LICENSE).
- [Encryption and pinning](https://learn.microsoft.com/en-us/sql/connect/ado-net/encryption-and-certificate-validation): a pin replaces normal chain/name validation.
- [SQL Tools Service assets](https://github.com/microsoft/sqltoolsservice/releases/tag/6.0.20261028.1), [source licence](https://github.com/microsoft/sqltoolsservice/blob/main/license.txt).
- [MSSQL source licence](https://github.com/microsoft/vscode-mssql/blob/main/LICENSE.txt), [third-party terms](https://github.com/microsoft/vscode-mssql/blob/main/ThirdPartyNotices.txt), [Marketplace restrictions](https://code.visualstudio.com/docs/supporting/faq#_can-i-use-a-microsoft-extension-outside-of-vs-code).
- [SqlPackage platforms](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-download). Do not follow its Gatekeeper-disabling example; this project does not need that action.
- [SQL Server Linux support](https://learn.microsoft.com/sql/linux/install-upgrade/setup), [server TLS](https://learn.microsoft.com/en-us/sql/linux/security/encrypted-connections?view=sql-server-ver16).
- [PKCS advisory](https://github.com/advisories/GHSA-2fmv-rv2c-59x7), [second advisory](https://github.com/advisories/GHSA-846f-f764-76mx), [.NET release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).

Revisit for failed supported-target tests, auth requirements, upstream support or
licence changes, or an audited Tools Service integration that removes substantial
custom code. Supersede this ADR explicitly; do not rewrite it as a desktop decision.
