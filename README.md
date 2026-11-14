# SQLoryn

SQLoryn is an independent, open-source SQL Server management app for macOS, written in C# with .NET 10 and Avalonia. It is not affiliated with Microsoft or SQL Server Management Studio.

> **Status: unsigned preview.** The app builds and runs from source on Apple Silicon and Intel Macs. It is not signed or notarised, and it has not been tested against production servers. Use it only with test databases.

## Features

- **Connections:** SQL authentication with mandatory encryption. Certificate trust is chosen per connection. Saved connection profiles keep no passwords. A password can optionally be remembered in the macOS Keychain.
- **Object Explorer:** databases, schemas, tables, views, keys, indexes, procedures, functions, sequences, synonyms, user-defined types, columns, constraints and triggers. Items load on demand, can be filtered and refreshed, and have a read-only Properties view. **Script SELECT** opens a new tab.
- **Editor:** multiple query tabs with T-SQL colouring, line numbers, find/replace and go to line. Opening and saving `.sql` files is supported.
- **Execution:** run the selection, the current `GO` batch or the whole script. Cancel a running query. Show the estimated plan, which never runs the query.
- **Results:** one data grid per result set, with typed values (including date, money, time, uniqueidentifier and binary as hex). Rows can be sorted and filtered. Messages are labelled by severity. A viewer shows XML/JSON cells. Results export to CSV or JSON.
- **Schema and data:** New Table and Design Table, and editing of the top 200 rows. Every change shows the generated SQL and needs confirmation. It is then applied in a single transaction.
- **Import:** delimited-file import with preview, column mapping, validation and Create/Append/Replace.
- **Administration:**
  - Security: view logins, users, roles and permissions; preview, confirm and apply GRANT/DENY/REVOKE.
  - Maintenance: backup, restore and DBCC CHECKDB, each with a preview and confirmation.
  - SQL Server Agent: view jobs, schedules and history.

High availability, replication and BI/cloud tooling are not implemented. Their menu entries open a page that says so.

## Build and run

You need macOS and the .NET SDK version pinned in [`global.json`](global.json) (10.0.403).

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet restore SQLoryn.slnx --locked-mode --configfile NuGet.Config
dotnet build SQLoryn.slnx --no-restore --configuration Release
bash scripts/build_macos_app.sh
open artifacts/macos/osx-arm64/SQLoryn.app   # osx-x64 on Intel Macs
```

The app bundle is framework-dependent, so the .NET 10 runtime must be installed. Tests are described in [docs/TESTING.md](docs/TESTING.md).

## Privacy

SQLoryn has no telemetry, analytics or cloud services. Its only network connections are to the SQL Servers you connect to.

It stores the following on your Mac, under `~/Library/Application Support/SQLoryn`. Files are created with owner-only permissions.

| What | Where | How to turn off or remove |
|---|---|---|
| Preferences and window layout (no passwords, no query text) | `settings.json` | Delete the file |
| Connection profiles: server, port, login name, database, TLS choice (no passwords) | `profiles.json` | Delete the file |
| Remembered passwords (only if you tick **Remember password**) | macOS Keychain, service `org.sqloryn.app` | Untick **Remember password** and connect, or delete the entry in Keychain Access |
| Copies of unsaved query tabs, so they can be restored after a crash | `recovery/unsaved-documents.json` | **Settings › Privacy**: turn off, or **Clear History and Unsaved Copies** |
| Recent query text, outcome and elapsed time (no result rows or server names) | `history/batches.json` | Same as above |

Unsaved copies and history are on by default and kept for 30 days. You can change the retention period in **Settings › Privacy**. Turning either off deletes what it has stored. Password-like assignments are removed from SQL text before it is stored, but other sensitive values in your scripts are not. Turn these features off if your scripts contain confidential data.

## Licence

SQLoryn is MIT-licensed ([LICENSE](LICENSE)). Third-party package licences are listed in [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md). Contributions: [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).
