# Windows portability and path policy

Copyright (C) 2026 RUSSELL PHILIP SMITHSON

## Installation

Extract the **contents** of the Windows release ZIP into a short, writable local folder such as `C:\LKVS` or `D:\LKVS`. That folder should contain `BOOT.cmd`, `bin`, `runtime` and the application directories directly. Run the extracted `BOOT.cmd`; running a file inside an archive is unsupported.

The application targets Windows 11 x64 with the required OS CLR/WPF components. Its complete Fixed Version WebView2 distribution is app-local. Development tools are needed to build source, not to start an assembled package. Current acceptance limits are recorded in [STATUS.md](STATUS.md).

## Supported path envelope

The current source deliberately uses a conservative common envelope for the Go broker, legacy .NET Framework presenter, browser components and Windows archive tools:

| Boundary | Maximum, excluding terminating NUL |
| --- | --- |
| Absolute installation root, including drive and separators | 100 UTF-16 code units |
| Absolute ordinary file path | 259 UTF-16 code units |
| Absolute directory path used for creation | 247 UTF-16 code units |
| Individual path component | 255 UTF-16 code units |

A non-BMP character such as an emoji occupies two UTF-16 units. Spaces and ordinary Unicode names do not consume a special exemption from these limits. The installation root must be a local absolute drive path; UNC, device/extended-length paths, network drives and reparse-point installations remain unsupported.

This policy does not assert that every Windows component supports arbitrary long paths. Microsoft's [path limitation documentation](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation) explains the legacy envelope, the shell/API distinction and the separate application and OS requirements for long-path opt-in. The project does not require registry edits, Group Policy changes, globally enabled long paths, disabled SmartScreen or reduced antivirus protection.

The source guards reject invalid characters, alternate data streams, Windows device aliases and trailing dot/space names. The package auditor also rejects traversal, absolute ZIP members, case collisions, file/directory collisions and links. Actual local identity checks continue to apply in the broker.

## Corrections in the current source

- The launcher checks the installation-root budget before starting native dependencies. The broker and presenter enforce the same root policy.
- Native and managed ordinary filesystem paths are checked before operations that depend on legacy Windows APIs.
- Qualification directories use short exclusive random run names, with short application and fixture subdirectories. Full UTC timestamps remain in report fields.
- Managed atomic writes use an independent short temporary filename in the destination directory. A 64-character content hash is no longer followed by another GUID in a temporary basename.
- Native atomic writes reserve room for their temporary filename before starting an operation.
- The build emits a receipt tying compiled source and the six native/adapter artifacts to their SHA-256 identities. Packaging rejects missing, changed or incomplete receipts.

These are source changes until an executable built from them passes the recorded checks. The legacy 3.0.0 reference download does not contain the new broker/presenter guards.

## Package auditing

From a developer PowerShell session:

```powershell
.\scripts\Test-WindowsPaths.ps1 -SelfTest
.\scripts\Test-WindowsPaths.ps1 -Archive 'D:\Releases\LKVS.zip' -InstallationRoot 'C:\LKVS'
```

The package workflow audits both the staging directory and final ZIP at the maximum supported 100-unit root. The report records the longest relative file and directory, installed lengths, file count and static root budget. It does not extract unsafe member names to test rejection.

The older 372,186,648-byte JP300 ZIP has a longest installed-relative file tail of 85 units after removing its outer `JP300/` folder. At a 100-unit root, the corresponding static full path is 186 units. The size discrepancy was not caused by corrupted or truncated files: all 409 ZIP members passed independent CRC/length checks and the archive SHA-256 matched its prior receipt.

Static archive checks do not prove all browser-generated profile names fit the envelope. Full native/window/Worker workloads and a genuine clean Windows 11 x64 test remain necessary. An excessively deep user-supplied report, export or VCMD source path is rejected rather than silently truncated or redirected.

## Packaging

Build the exact reviewed source, then run:

```powershell
.\scripts\Package-Windows.ps1 -OutputDirectory 'D:\Codex\Releases'
```

The output ZIP has `BOOT.cmd` at its root. It includes the compiled programs, three WebView2 adapters, complete Fixed Version runtime, source, fixtures and notices. It excludes user workspaces, credentials, active network configuration, certificates, developer caches, prior ZIPs and private execution logs. Packaging refreshes inventories in an isolated stage and preserves that stage for investigation if a step fails.

See [RELEASES.md](RELEASES.md) for the difference between runnable release assets and GitHub's automatically generated source archives.
