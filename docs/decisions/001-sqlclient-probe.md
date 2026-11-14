# ADR-001: Use Microsoft.Data.SqlClient directly

Date: 2026-10-31.

## Decision

Use .NET 10 and Microsoft.Data.SqlClient, in process, for all SQL Server access. Do not write a TDS implementation or add a separate service layer.

| Option | Assessment |
|---|---|
| Microsoft.Data.SqlClient | Maintained provider with MIT-licensed managed code and cross-platform .NET support. Version 7.x moves the Azure authentication provider into a separate package, which SQLoryn does not install. **Chosen.** |
| SQL Tools Service | MIT source and builds for both Mac architectures. It is useful for language services, scripting and plans, but would add a separate process and a redistribution review. Not bundled. |
| VS Code MSSQL / Code-OSS | Marketplace extensions are restricted to Microsoft products, and a fork adds maintenance without improving the driver. |
| DacFx / SqlPackage / SMO | Possible later for schema and admin features, after checking package terms and Mac support. |
| ODBC / custom TDS | ODBC adds native-driver packaging. A custom TDS client would duplicate maintained protocol code. |

## Security and licensing

- SqlClient's default `System.Security.Cryptography.Pkcs` 9.0.18 is affected by GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx. A patched 10.0.x version is pinned directly, and transitive vulnerability auditing stays on with warnings as errors.
- The Windows native SNI package is separately licensed and not MIT. Every project excludes all of its assets, so it is never shipped.
- The SDK version is pinned in `global.json` from Microsoft's release metadata.
- Live tests use a new disposable SQL Server, certificate pinning, no retries or pooling, and synthetic data. Linux x64 server results do not prove Mac client behaviour. SQL Server emulation on Apple Silicon is unsupported by Microsoft and is not used.

## Sources

- [SqlClient support lifecycle](https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-driver-support-lifecycle), [SqlClient licence](https://github.com/dotnet/SqlClient/blob/main/LICENSE)
- [Encryption and certificate validation](https://learn.microsoft.com/en-us/sql/connect/ado-net/encryption-and-certificate-validation): a pinned certificate replaces normal chain and name validation
- [SQL Tools Service](https://github.com/microsoft/sqltoolsservice), [MSSQL extension licence](https://github.com/microsoft/vscode-mssql/blob/main/LICENSE.txt), [Marketplace restrictions](https://code.visualstudio.com/docs/supporting/faq#_can-i-use-a-microsoft-extension-outside-of-vs-code)
- [SqlPackage platforms](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-download)
- [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7), [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx), [.NET release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
