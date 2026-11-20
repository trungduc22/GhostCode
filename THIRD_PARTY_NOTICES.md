# Third-Party Notices

GhostCode itself — including its C#/.NET source code, WinUI 3 desktop UI, browser extensions (`browser-extension/`, `browser-extension-firefox/`), tests, and packaging scripts — is licensed under the **MIT License** (see [LICENSE](LICENSE)).

Distributed builds bundle or depend on third-party libraries and runtimes governed by their own respective licenses. Nothing in GhostCode's MIT license alters or overrides the license terms of those components.

## Application Runtime & Library Dependencies

| Component | Version | Project | License |
|---|---|---|---|
| `Microsoft.WindowsAppSDK` | `2.6.5` | `GhostCode.App` | Microsoft Software License Terms (Distributable Code, Section 3) |
| .NET 10 Desktop & Windows Runtime (self-contained) | `10.0.x` | `GhostCode.App` | MIT License (dotnet/runtime) / Microsoft Redistributable Terms |
| `CommunityToolkit.Mvvm` | `8.4.0` | `GhostCode.App` | MIT License |
| `CommunityToolkit.WinUI.Controls.SettingsControls` | `8.2.251219` | `GhostCode.App` | MIT License |
| `NativeShellKit.Hosting`, `NativeShellKit.Diagnostics`, `NativeShellKit.Communication`, `NativeShellKit.WinUI`, `NativeShellKit.Tray` and transitive NativeShellKit packages | `0.2.0-alpha.3` | `GhostCode.App` | MIT License, Copyright (c) 2026 trungduc22. The MIT terms are included in [LICENSE](LICENSE). |
| `Microsoft.Extensions.DependencyInjection` | `11.0.0` | `GhostCode.App`, `GhostCode.Core` | MIT License |
| `Microsoft.Extensions.Logging` | `11.0.0` | `GhostCode.Core` | MIT License |
| `Microsoft.Data.Sqlite` | `11.0.0` | `GhostCode.Core` | MIT License |
| `SQLitePCLRaw.bundle_e_sqlite3` *(transitive via `Microsoft.Data.Sqlite`)* | `3.0.5` | `GhostCode.Core` | Apache License 2.0 (bundles SQLite engine, Public Domain) |
| `System.Security.Cryptography.ProtectedData` | `11.0.0` | `GhostCode.Core` | MIT License |

## Test & Verification Dependencies *(Not Distributed in Release Binaries)*

| Component | Version | Project | License |
|---|---|---|---|
| `Microsoft.NET.Test.Sdk` | `17.14.1` | `GhostCode.Tests` | MIT License |
| `xunit` | `2.9.3` | `GhostCode.Tests` | Apache License 2.0 |
| `xunit.runner.visualstudio` | `3.1.4` | `GhostCode.Tests` | Apache License 2.0 |
| `coverlet.collector` | `6.0.4` | `GhostCode.Tests` | MIT License |

## Installer & Build Tooling Notices

- **Inno Setup 6** (used optionally to build `GhostCode-Setup-x64.exe` under the Inno Setup License).
- **Google Drive REST v3 API** & **Microsoft Graph OneDrive API**: accessed over HTTPS using the end-user's own OAuth Client ID and PKCE consent tokens; API usage remains subject to Google API Terms of Service and Microsoft Graph Terms of Use respectively.
