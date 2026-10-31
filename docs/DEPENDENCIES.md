# Dependency and redistribution assessment

Experiment only. Owner: SQLoryn maintainer. Reviewed: 2026-10-31.

The lockfiles are authoritative for versions and NuGet content hashes. The table
records the exact restored package metadata, not a blanket distribution approval.
All selected managed packages declare MIT. SqlClient includes a separately
licensed Windows native SNI dependency: both projects explicitly exclude all its
assets and do not support Windows. Restore still downloads that package. Its
licence permits development/test use but imposes additional distribution terms
including downstream protection and indemnity obligations. Do not ship it under
the project MIT licence. Inspect actual artifacts and notices before any release.

| Package | Version | Declared licence / disposition |
|---|---|---|
| [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.1.1) | 7.1.1 | MIT |
| [Microsoft.Data.SqlClient.SNI.runtime](https://www.nuget.org/packages/Microsoft.Data.SqlClient.SNI.runtime/7.1.0) | 7.1.0 | Microsoft SNI terms; all assets excluded |
| [System.Security.Cryptography.Pkcs](https://www.nuget.org/packages/System.Security.Cryptography.Pkcs/10.0.14) | 10.0.14 | MIT |
| [Microsoft.Bcl.Cryptography](https://www.nuget.org/packages/Microsoft.Bcl.Cryptography/9.0.18) | 9.0.18 | MIT |
| [Microsoft.Data.SqlClient.Extensions.Abstractions](https://www.nuget.org/packages/Microsoft.Data.SqlClient.Extensions.Abstractions/7.1.1) | 7.1.1 | MIT |
| [Microsoft.Data.SqlClient.Internal.Logging](https://www.nuget.org/packages/Microsoft.Data.SqlClient.Internal.Logging/7.1.1) | 7.1.1 | MIT |
| [Microsoft.Extensions.Caching.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Abstractions/9.0.18) | 9.0.18 | MIT |
| [Microsoft.Extensions.Caching.Memory](https://www.nuget.org/packages/Microsoft.Extensions.Caching.Memory/9.0.18) | 9.0.18 | MIT |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.0) | 10.0.0 | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/10.0.0) | 10.0.0 | MIT |
| [Microsoft.Extensions.Options](https://www.nuget.org/packages/Microsoft.Extensions.Options/9.0.18) | 9.0.18 | MIT |
| [Microsoft.Extensions.Primitives](https://www.nuget.org/packages/Microsoft.Extensions.Primitives/9.0.18) | 9.0.18 | MIT |
| [Microsoft.IdentityModel.Abstractions](https://www.nuget.org/packages/Microsoft.IdentityModel.Abstractions/8.16.0) | 8.16.0 | MIT |
| [Microsoft.IdentityModel.JsonWebTokens](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens/8.16.0) | 8.16.0 | MIT |
| [Microsoft.IdentityModel.Logging](https://www.nuget.org/packages/Microsoft.IdentityModel.Logging/8.16.0) | 8.16.0 | MIT |
| [Microsoft.IdentityModel.Protocols](https://www.nuget.org/packages/Microsoft.IdentityModel.Protocols/8.16.0) | 8.16.0 | MIT |
| [Microsoft.IdentityModel.Protocols.OpenIdConnect](https://www.nuget.org/packages/Microsoft.IdentityModel.Protocols.OpenIdConnect/8.16.0) | 8.16.0 | MIT |
| [Microsoft.IdentityModel.Tokens](https://www.nuget.org/packages/Microsoft.IdentityModel.Tokens/8.16.0) | 8.16.0 | MIT |
| [Microsoft.SqlServer.Server](https://www.nuget.org/packages/Microsoft.SqlServer.Server/1.0.0) | 1.0.0 | MIT |
| [System.Configuration.ConfigurationManager](https://www.nuget.org/packages/System.Configuration.ConfigurationManager/9.0.18) | 9.0.18 | MIT |
| [System.Diagnostics.EventLog](https://www.nuget.org/packages/System.Diagnostics.EventLog/9.0.18) | 9.0.18 | MIT |
| [System.IdentityModel.Tokens.Jwt](https://www.nuget.org/packages/System.IdentityModel.Tokens.Jwt/8.16.0) | 8.16.0 | MIT |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/9.0.18) | 9.0.18 | MIT |
| [System.Threading.RateLimiting](https://www.nuget.org/packages/System.Threading.RateLimiting/9.0.18) | 9.0.18 | MIT |

## Acquisition and known constraints

- SDK 10.0.403 / runtime 10.0.14: official Microsoft release metadata; local SDK
  archive SHA-512 checked before extraction. The SDK/runtime are developer
  prerequisites, not committed or distributed application artifacts.
- PKCS 10.0.14 is a direct security override: the initial transitive 9.0.18
  failed on GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx. Keep the override until
  the driver supplies a patched minimum; do not suppress audit warnings.
- SqlClient 7.1 is STS. Dependabot checks NuGet/Actions weekly; updates require
  review, an inventory/lockfile refresh and regression checks.
- SQL Server 2022 Developer CU27 GDR1 is a proprietary test prerequisite, not
  an app dependency or redistributed binary. Its image is pinned in the runner;
  execution requires explicit test-use licence acceptance.
- actions/checkout and actions/setup-dotnet have MIT source licences, pinned to
  full commits. Hosted OS images evolve; record their actual run versions.
- OpenSSL, Docker, Python and .NET are host prerequisites. OpenSSH client/scp are
  additional prerequisites for optional Mac SSH qualification (PROBE-11). Their
  redistribution has not been assessed because no installer bundles them.
- No SSMS, DacFx, SMO, Tools Service, Marketplace extension, image, font or icon
  is imported. Their exact binary/package terms remain separate investigations.

The package cache includes upstream licences; retain applicable copyright and
third-party notices in any future artifact. Before distribution, inspect the
actual publish output, generate a complete SBOM/notices inventory (including
runtime/native components), and obtain review of proprietary terms. Source-only
experimental PR approval is not release approval.
