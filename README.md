# LK/Vexa Data System — JP300 3.0.0 reference bundle

Copyright (C) 2026 RUSSELL PHILIP SMITHSON

The named release asset **LK-Vexa-JP300-3.0.0-reference-Windows-x64.zip** is the complete legacy Windows 11 x64 application, including compiled executables, their original corresponding 3.0.0 source, the WebView2 SDK adapter and complete Microsoft Fixed Version Runtime. GitHub's automatically generated **Source code** ZIP contains only the reference source and cannot launch the application.

**This is not a rebuilt 3.0.1 release.** The current repository's path handling and other 3.0.1 corrections are absent from these original 3.0.0 application binaries. The BOOT.cmd preflight checks the installation root; it does not change the old native application's handling of generated or external paths.

## Start

1. Extract the full named asset to a fresh, writable local directory, preferably **C:\LKVS**. Preserve any previous installation and do not merge folders.
2. Run **BOOT.cmd**. It rejects UNC roots, roots over 100 UTF-16 code units and root characters outside a conservative ASCII set: letters, digits, spaces, colon, backslash, period, underscore and hyphen.
3. Use a local fixed drive. A drive letter can refer to a mapped network drive; this legacy batch preflight does not determine the underlying volume type.
4. Keep backup, import and script locations short. Arbitrarily deep runtime paths are not qualified by the installation-root check.

The original launchers remain for compatibility and diagnostics. Opening one directly bypasses BOOT.cmd's new root preflight. Windows 11 x64 and operating-system .NET Framework/WPF components are required. No Python, Node, compiler, first-launch runtime download or administrator elevation is required for normal launch.

With no server-config.json, the original launcher starts a loopback service and local native window. Review server-config.example.json and the original implementation before enabling network modes. No private configuration, TLS certificate/key, saved user workspace or browser profile is supplied. Mutable application data is created under workspace.

## Contents

| Directory | Contents |
| --- | --- |
| bin | Original JPBroker.exe, JPConsole.exe, JPTests.exe and three WebView2 adapter DLLs |
| runtime/webview2-fixed | Complete unmodified Microsoft Fixed Version Runtime 155.0.4283.45 x64 |
| runtime/webview2-sdk/1.0.4191.47 | Original SDK adapter files and provenance |
| native, host, source, appliance | Original corresponding 3.0.0 implementation, tests and retained command components |
| engine/ja21 | Original presenter ABI 9.8.7 metadata, IR and typed seal |
| photon, licenses | Component inputs, retained attribution and vendor agreements |
| validation/standalone-job.vcmd | Original deterministic demonstration fixture |

history/JP250.zip and all nested historical packages have been removed. Private execution logs and machine-specific validation outputs have been removed. They are not runtime dependencies.

## Integrity and qualification

VERSION and CONSOLE_VERSION remain **3.0.0**. All 33 original presenter seal entries, including the managed executable and its C# source, retain their exact original bytes. Go implementation files and native binaries also retain their original bytes. Public licensing, release documentation, the installation-root preflight and outer SHA256SUMS.txt inventory describe this repackaging and do not imply a new build.

The original archive's historical tests reported 44 native top-level passes and 83 retained JavaScript command passes. Those tests were not rerun for this repackaging. No independent clean Windows guest qualification, complete GUI-flow qualification, Windows 10 support, current 3.0.1 regression pass or arbitrary long-path compatibility is claimed. See STATUS.md.

SHA256SUMS.txt inventories the full binary asset's immutable contents and excludes itself. Hashes detect byte changes; they are not publisher signatures. The source-only GitHub ZIP intentionally omits this assembled inventory and all vendor/application binaries. Retain the independent archive SHA-256 receipt supplied with the release.

## Build and license

Original build instructions are in native/BUILD.md. Current source lives at https://github.com/LKVexa/LK-Vexa-Data-System and may contain later changes absent from this legacy bundle.

Authorized project-owned material is GPL-3.0-only under LICENSE, COPYRIGHT and NOTICE. Historical attribution remains. Microsoft binaries are separately licensed and are **not covered by the project GPL grant**. The exact vendor runtime agreement is in licenses/Microsoft-WebView2-Fixed-Runtime-License.txt. See LICENSING.md and THIRD_PARTY_NOTICES.md.

This software includes Microsoft Defender SmartScreen. It collects and sends end-user information to Microsoft as disclosed in Microsoft's Privacy Statement (https://aka.ms/privacy) and Edge Privacy Whitepaper (https://learn.microsoft.com/en-us/microsoft-edge/privacy-whitepaper#smartscreen). Vendor privacy interfaces and notices remain intact. Fixed Version Runtime does not update automatically; updating it requires a deliberately rebuilt and requalified distribution.
