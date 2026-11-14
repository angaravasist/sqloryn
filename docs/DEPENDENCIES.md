# Dependencies and licences

Exact versions and content hashes are in the `packages.lock.json` files. Licences are as declared in each package's NuGet metadata.

## Direct packages

| Package | Version | Licence | Notes |
|---|---|---|---|
| Microsoft.Data.SqlClient | 7.1.2 | MIT | SQL Server driver |
| Microsoft.Data.SqlClient.SNI.runtime | 7.1.2 | Microsoft SNI licence | Windows native library. All assets are excluded (`ExcludeAssets="all"`), so it is downloaded during restore but never shipped |
| System.Security.Cryptography.Pkcs | 10.0.15 | MIT | Pinned above SqlClient's minimum for GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx. 11.x (.NET 11 line) is not used |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.6 | MIT | Must be kept on the same version |
| Avalonia.Controls.DataGrid | 12.1.2 | MIT | Result grids |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | SQL editor |
| AvaloniaUI.DiagnosticsSupport | 2.2.3 | None declared | Debug builds only. Release builds exclude its assets |

## Transitive packages

| Package | Version | Licence |
|---|---|---|
| Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.HarfBuzz, Avalonia.Native, Avalonia.Remote.Protocol, Avalonia.Skia, Avalonia.Win32, Avalonia.X11 | 12.1.6 | MIT |
| Avalonia.BuildServices | 11.3.2 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD-3-Clause (ANGLE); Windows only |
| SkiaSharp and SkiaSharp.NativeAssets.* | 3.119.4 | MIT |
| HarfBuzzSharp and HarfBuzzSharp.NativeAssets.* | 8.3.1.3 | MIT |
| MicroCom.Runtime | 0.11.6 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| Microsoft.Data.SqlClient.Extensions.Abstractions, Microsoft.Data.SqlClient.Internal.Logging | 7.1.2 | MIT |
| Microsoft.Bcl.Cryptography | 9.0.21 | MIT |
| Microsoft.Extensions.Caching.Abstractions, Microsoft.Extensions.Caching.Memory, Microsoft.Extensions.Options, Microsoft.Extensions.Primitives | 9.0.21 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT |
| Microsoft.IdentityModel.* (Abstractions, JsonWebTokens, Logging, Protocols, Protocols.OpenIdConnect, Tokens), System.IdentityModel.Tokens.Jwt | 8.16.0 | MIT |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT |
| Microsoft.SqlServer.Server | 1.0.0 | MIT |
| System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, System.Threading.RateLimiting | 9.0.21 | MIT |

## Tools and test prerequisites (not shipped)

| Item | Version | Licence |
|---|---|---|
| .NET SDK / runtime | 10.0.403 / 10.0.14 (`global.json`) | MIT |
| actions/checkout, actions/setup-dotnet | Pinned to full commit SHAs in the workflow | MIT |
| SQL Server 2022 Developer container image | Pinned in `scripts/run_sql_probe.py` | Microsoft SQL Server Developer licence (test use; accepted explicitly per run) |

The app icon in `assets/icon` is original artwork under the project's MIT licence. Before distributing a build, check the publish output and include the third-party notices it requires.
