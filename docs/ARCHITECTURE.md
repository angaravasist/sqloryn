# Architecture discovery

**Decision status: pending technical proof of concept.**

## Product direction

A standalone macOS-first desktop SQL Server management tool, with familiar SSMS workflows, security-focused local storage and explicitly tested compatibility.

## Candidates to evaluate

1. Tauri 2 + React / TypeScript / Monaco and a .NET sidecar hosting compatible SQL tooling.
2. A Code-OSS-based desktop shell with reusable Microsoft SQL tooling, if technically and legally viable.

### Technical discovery questions

- Can Microsoft SQL Tools Service be packaged and run on macOS arm64 and x64?
- Which vscode-mssql components can be embedded or reused outside VS Code, and under what licences?
- Which SqlClient, SMO, DacFx and other platform-specific dependencies are redistributable?
- Can SQL and supported Entra authentication, proper TLS validation, cancellation and multiple result sets be demonstrated?
- Can large query outputs be streamed without UI freezes or memory exhaustion?
- How will secure updates, signed sidecars and notarisation work?

## Trust boundaries

Database credentials use Keychain and never appear in source, logs or analytics. Opened SQL files, query outputs and server-provided metadata are untrusted. The application does not require hosted accounts or a server-side intermediary for core use.

No hosted telemetry, cloud AI access or plugin marketplace is part of the baseline product. Any such addition requires a separate design and privacy review.
