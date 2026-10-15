# Windows downloads and release assembly

Copyright (C) 2026 RUSSELL PHILIP SMITHSON

## Select the right download

Use the named **Windows x64 ZIP** attached under [GitHub Releases](https://github.com/LKVexa/LK-Vexa-Data-System/releases) when you need the application. GitHub's automatic **Source code (zip)** and the repository's **Code → Download ZIP** contain the repository's source tree. They do not add ignored executables or browser dependencies.

A complete executable package contains:

| Component | Required content |
| --- | --- |
| Entry | `BOOT.cmd` and matching operating instructions |
| Compiled application | `bin/JPBroker.exe`, `bin/JPConsole.exe`, `bin/JPTests.exe` |
| App-local adapters | `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`, `WebView2Loader.dll` |
| Browser | Entire `runtime/webview2-fixed/` distribution, not just its EXE |
| Application | Browser command runtime/UI, engine metadata, JA admission source, Photon configuration and deterministic fixtures |
| Integrity | Matching inner presenter seal, outer SHA-256 inventory and build receipt for newly built candidates |
| Notices/source | GPL text and project attribution; corresponding project source; applicable vendor terms, privacy and third-party notices |

The target OS supplies Windows/.NET Framework/WPF. No Go, Python, Node or source compilation is required for ordinary startup of the assembled package.

## Why the ZIP sizes differed

The original JP300 ZIP is **372,186,648 bytes** and expands to **778,294,199 bytes**. ZIP central-directory accounting shows:

| Original group | Compressed payload bytes | Expanded bytes |
| --- | ---: | ---: |
| Runtime dependencies | 311,814,063 | 706,477,147 |
| Historical nested JP250 archive | 50,155,385 | 50,169,128 |
| Compiled bin directory | 9,703,597 | 19,837,472 |

ZIP metadata and the remaining source/documentation account for the balance. The initial GitHub publication deliberately included 145 source/documentation files totaling 1,809,224 uncompressed bytes. Its much smaller compressed size is expected. Source archives cannot substitute for a Windows distribution.

The historical archive is not required for startup. It also contains further nested archives, so it is excluded from new public packages along with private execution evidence and mutable state.

## Version boundaries

The **JP300 3.0.0 reference package** preserves its historical compiled binaries and corresponding source. It receives current distribution notices and a conservative launcher path preflight, with private evidence and nested history removed. It is identified as a legacy reference, not a new 3.0.1 build or a clean-machine certification. Historical launchers are retained where that binary version requires them.

The **3.0.1 candidate** on the main branch contains the consolidated native BOOT dispatch, qualification corrections and new path guards. Its executable artifacts must be rebuilt from the exact source. The packaging script refuses to combine those sources with stale executables from the reference package.

A manually dispatched Windows build workflow is intended to compile and test the current source on a fresh hosted runner, using the exact vendor payload from the complete reference application distribution. Publication is gated on successful build, path checks and native/JavaScript regressions. Hosted Windows Server build evidence does not establish clean Windows 11 desktop provenance or replace the actual WPF/Worker acceptance run.

## Local build and package

Acquire/stage the dependencies described in [DEPENDENCIES.md](DEPENDENCIES.md), then:

```powershell
.\scripts\Build-Windows.ps1
.\scripts\Test-WindowsPaths.ps1 -SelfTest
.\scripts\Package-Windows.ps1 -OutputDirectory 'D:\Codex\Releases'
```

The package script reads `bin/BUILD-RECEIPT.json`, validates its source and binary identities, stages an explicit allowlist outside the checkout, refreshes inventories and audits the final archive. Generated files include the Windows ZIP, SHA-256 sidecar and path/assembly report. The ZIP starts directly with `BOOT.cmd`, allowing short extraction folders.

The build receipt and hashes describe bytes and provenance records; they are not code signatures, publisher authentication or evidence that a compromised build host is trustworthy. Review the actual workflow result and [STATUS.md](STATUS.md) before promoting a candidate.

## Before calling a candidate accepted

Complete the acceptance gates in STATUS: exact-source build, matching inventories, full regressions, actual native window and Worker workload, normal BOOT behavior, post-run immutable verification, final archive verification and independently recorded clean Windows 11 x64 execution. Distinguish failures and skipped tests from passed assertions.

Extract into `C:\LKVS` or `D:\LKVS`; see [WINDOWS_PORTABILITY.md](WINDOWS_PORTABILITY.md). Retain the supplied Microsoft terms and notices. The bundled runtime remains Microsoft software and is not relicensed by the project GPL.
