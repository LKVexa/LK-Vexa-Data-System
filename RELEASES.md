# Windows downloads and release assembly

Copyright (C) 2026 RUSSELL PHILIP SMITHSON

## Select the right download

**Current complete candidate:** [LKVS-3.0.1-Windows-x64-candidate.zip](https://github.com/LKVexa/LK-Vexa-Data-System/releases/download/v3.0.1-candidate.3/LKVS-3.0.1-Windows-x64-candidate.zip), **331,597,148 bytes**, SHA-256 `e63e1e0666ef99a79e5cbc6b785932f11c96abac7c39d6a5282fb4a4218f2981`. Its source is commit `629d44dc5c975ee5cb9d49576201d1b9aa5b4464`. Subsequent documentation or evidence-workflow commits on main do not change these published binary bytes.

Use the named **Windows x64 ZIP** attached under [GitHub Releases](https://github.com/LKVexa/LK-Vexa-Data-System/releases) when you need the application. GitHub's automatic **Source code (zip)** and the repository's **Code → Download ZIP** contain the repository's source tree. They do not add ignored executables or browser dependencies.

A complete executable package contains:

| Component | Required content |
| --- | --- |
| Entry | `BOOT.cmd` and matching operating instructions |
| Compiled application | `bin/JPBroker.exe`, `bin/JPConsole.exe`, `bin/JPTests.exe` |
| App-local adapters | `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`, `WebView2Loader.dll` |
| SDK staging | The same three SDK/loader DLLs under `runtime/webview2-sdk/1.0.4191.47/`; current readiness checks also require this directory |
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

The **JP300 3.0.0 reference package** preserves its historical compiled binaries and corresponding source. It receives current distribution notices and a conservative launcher path preflight, with private evidence and nested history removed. It is identified as a legacy reference, not a new 3.0.1 build or a clean-machine certification. Historical launchers are retained where that binary version requires them. This reference ZIP retains a top-level `JP300/` folder: after extraction, run `JP300/BOOT.cmd`. The 100-unit root budget applies to the folder containing BOOT.cmd. New 3.0.1 packages assembled by Package-Windows.ps1 place BOOT.cmd directly at the ZIP root.

The **3.0.1 candidate** on the main branch contains the consolidated native BOOT dispatch, qualification corrections and new path guards. Candidate.3 contains freshly compiled artifacts from its exact tagged source. Any new executable artifacts must also be rebuilt from their corresponding source. The packaging script refuses to combine those sources with stale executables from the reference package.

The [successful Windows build](https://github.com/LKVexa/LK-Vexa-Data-System/actions/runs/38677362390) compiled candidate.3 and passed 57 top-level native groups, 83 JavaScript tests and 34 path/ZIP cases, with zero skips in those build suites. The [separate downloaded-application run](https://github.com/LKVexa/LK-Vexa-Data-System/actions/runs/38677753946) verified the exact release ZIP and passed the actual WPF/Worker, VCMD readback, Photon verification/snapshot and graceful-close qualification. Both used fresh hosted Windows Server runners. Clean Windows 11 desktop provenance and normal no-argument BOOT acceptance on that guest remain outstanding. See [STATUS.md](STATUS.md).

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
