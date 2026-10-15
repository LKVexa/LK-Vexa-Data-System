# Engineering and qualification status

Project: **LK/Vexa Data System**. Executable: **3.0.1 candidate.3**. Presenter compatibility ABI: **9.8.7**. Copyright (C) 2026 RUSSELL PHILIP SMITHSON.

A complete Windows x64 executable candidate is published. It has passed the exact-source hosted build and a separate downloaded-package application qualification. It remains a prerelease because independently attested clean Windows 11 desktop acceptance is outstanding.

## Published artifact identity

- [Complete executable ZIP](https://github.com/LKVexa/LK-Vexa-Data-System/releases/download/v3.0.1-candidate.3/LKVS-3.0.1-Windows-x64-candidate.zip): **331,597,148 bytes**.
- SHA-256: `e63e1e0666ef99a79e5cbc6b785932f11c96abac7c39d6a5282fb4a4218f2981`.
- Exact binary source/tag commit: `629d44dc5c975ee5cb9d49576201d1b9aa5b4464`. Later documentation/evidence workflow commits do not rebuild or replace these bytes.
- [Build and regression run](https://github.com/LKVexa/LK-Vexa-Data-System/actions/runs/38677362390); [separate downloaded-package run](https://github.com/LKVexa/LK-Vexa-Data-System/actions/runs/38677753946). Both completed successfully on fresh GitHub-hosted Windows Server 2025 runners.

## Observed results

| Evidence | Result and scope |
| --- | --- |
| Exact-source Go/C# build | Passed; source identities and six compiled/adapter hashes recorded in the build receipt |
| Build native suite | 57 top-level groups passed; zero skipped |
| Build JavaScript suite | 83 tests passed; zero skipped |
| Windows path/ZIP policy | 34 synthetic cases passed; stage and archive audits also passed |
| Download identity and extraction | Published archive SHA-256 matched; names audited before extraction |
| Immutable package verification | Passed on the downloaded package |
| Managed dependency probe | Actual x64 CLR/WPF, app-local adapter and Fixed Version browser resolved; installed-renderer fallback false |
| Offline doctor | Passed |
| Packaged native regressions under restricted PATH | 56 top-level groups passed; one optional `TestRetainedJSEngineNativeBrokerIntegration` skipped because Node is excluded |
| Actual native presenter/Worker qualification | Passed with restricted PATH and precompiled tests; VCMD file readback, Photon verification/snapshot, isolated data and acknowledged graceful close |
| Normal no-argument BOOT on a clean Windows 11 desktop | Pending |
| Independently attested clean Windows 11 x64 acceptance | Pending; hosted Windows Server is a separate environment |
| Maximum 100-unit installation root with browser-generated profile names | Not fully qualified; the actual hosted workload used a short root |

The hosted application workflow invokes `JPBroker.exe --boot --qualify` directly. Its isolated workspace and ephemeral loopback port do not exercise the batch wrapper, ordinary workspace or availability of default port 8877. The embedded candidate documentation predates these later qualification observations; the release page and this current status record contain the reconciled evidence.

The [original qualification report ZIP](https://github.com/LKVexa/LK-Vexa-Data-System/releases/download/v3.0.1-candidate.3/qualification-run-38677753946.zip) is preserved as a release asset without transformation: 14,809 bytes, SHA-256 `fceb3ee25fba0825bf2ae68d756cb225122bc4c43f020f96db1426317259b9eb`. Its [verified summary](https://github.com/LKVexa/LK-Vexa-Data-System/releases/download/v3.0.1-candidate.3/qualification-run-38677753946.summary.json) records the actual flags and explicit skip. [Preservation workflow](https://github.com/LKVexa/LK-Vexa-Data-System/actions/runs/38678557318) checked the original artifact digest, candidate release digest, source/tag identity and every required success flag before attaching the reports. No application test was rerun by preservation.

The build suite totals above describe the developer-enabled build runner. Packaged qualification restricts PATH and records its own `nativeTopLevelPassed` and `nativeSkipped`; optional developer integration skips in that separate run must be read as skips. A successful workflow is evidence of its recorded checks, not of every possible execution environment.

The native report intentionally retains `cleanMachineCertified=false`. A fresh folder, restricted PATH or hosted Windows Server image cannot establish independently attested clean Windows 11 provenance. Use `CLEAN_MACHINE_TEST.txt` and `GUEST-ATTESTATION.template.json` for the separate desktop acceptance record.

## Packaging and path policy

The repository source ZIP excludes generated programs and Microsoft binary dependencies. The named Windows release ZIP includes the three compiled programs, WebView2 managed/native adapters, complete Fixed Version browser, application assets, matching source, GPL/vendor terms and verification inventories. Extract all files into `C:\LKVS` or `D:\LKVS`; `BOOT.cmd` is directly at the archive root. See [RELEASES.md](RELEASES.md).

Candidate.3 includes the 100 UTF-16-unit installation-root budget, matching Go/C# path guards, bounded qualification directories, short same-directory atomic temporary names, source/binary receipts and audited packaging. Traditional file/directory limits are enforced without changing machine registry, Group Policy or security settings. See [WINDOWS_PORTABILITY.md](WINDOWS_PORTABILITY.md).

## Earlier evidence retained for context

An earlier local 3.0.1 run passed 28 native groups before a ten-minute cleanup timeout. Subsequent local diagnostic processes and filesystem operations stalled; the cause was not established. The fixture-retention correction was later rebuilt and the complete build suite passed on the hosted runner above. The earlier failed workflow attempts remain in Actions history; only the linked successful runs support the current result.

Historical 3.0.0 results (44 native groups, 83 JavaScript tests and actual WPF/Worker execution) remain baseline evidence only. The separately labelled 3.0.0 reference package preserves its corresponding binaries/source and does not contain the new native 3.0.1 path guards. The original 372,186,648-byte ZIP passed all 409 member CRC/length checks; its larger size included unnecessary nested history.

## Remaining desktop acceptance

1. Obtain an independently recorded clean Windows 11 x64 guest/image with no developer tools.
2. Download the exact candidate ZIP above; verify SHA-256 and extract all files into a short writable local folder.
3. Record ordinary no-argument BOOT startup, loopback defaults, usable window/Worker command execution, persistence and orderly close.
4. Run package integrity/probe/doctor/qualification modes, retain PASS/FAIL/SKIP and guest provenance, and verify immutable contents after execution.
5. Exercise supported relocation and path boundary cases before broad portability acceptance.

Unsigned inventories and build receipts establish byte matching within their recorded provenance. They are not publisher code signatures or proof of a trustworthy build host.
