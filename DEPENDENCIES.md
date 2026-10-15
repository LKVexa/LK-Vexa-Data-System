# Bundled dependencies and developer acquisition

The current main branch and its **Code → Download ZIP** contain the compiled candidate.3 application, matching project source, WebView2 SDK adapters and complete Fixed Version Runtime payload. The oversized `msedge.dll` is represented by ordered 48 MiB raw parts under `payload/webview2/msedge.dll.000` and subsequent numbered files. `BOOT.cmd` invokes the bundled helper to verify and reconstruct the exact DLL locally before application checks. Ordinary users do not need a download, source build, Go, Node or Python.

Earlier source-only commits and tags retain their original source-only archives; changing main does not add binaries to those historical downloads. The pinned versions below describe the bundled configuration, not the newest available vendor versions. Code acceptance evidence and the separate repository-archive/bootstrap qualification boundary are recorded in [STATUS.md](STATUS.md).

| Dependency | Reference | Required location or role |
| --- | --- | --- |
| Go | 1.27.2; module language floor 1.27.0 | Developer compiler; selected with `-GoExe` or developer PATH |
| Windows .NET Framework | OS-provided x64 CLR/WPF and compiler | `%WINDIR%/Microsoft.NET/Framework64/v4.0.30319/` |
| Windows PowerShell/.NET | OS-provided bootstrap facilities | Offline verification and assembly of the bundled DLL parts; no source compilation |
| WebView2 SDK | 1.0.4191.47 | Three adapter files in `runtime/webview2-sdk/1.0.4191.47/` |
| WebView2 Fixed Version Runtime | 155.0.4283.45, Windows x64 | Complete vendor tree in `runtime/webview2-fixed/` |
| Node.js | Optional developer tool | Retained JavaScript and optional integration tests; never production startup |

## Microsoft SDK

Main already contains the SDK files both beside the presenter in `bin/` and in the SDK staging directory below. Both locations are required by the current application/readiness checks. For a deliberate dependency replacement, repair, or an earlier source-only checkout, the reference package is [Microsoft.Web.WebView2 1.0.4191.47 on NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47), published by Microsoft. Preserve its license and notice material. Its .NET Framework-compatible library directory and Windows x64 native runtime directory supply:

```text
runtime/webview2-sdk/1.0.4191.47/
  Microsoft.Web.WebView2.Core.dll
  Microsoft.Web.WebView2.Wpf.dll
  WebView2Loader.dll
```

Use the x64 loader rather than an x86/ARM loader. Do not combine assemblies from different package versions. The build validates the managed file versions and native loader architecture; those checks do not independently authenticate the original download.

## Fixed Version browser

Main already contains the complete vendor payload, with only `msedge.dll` stored as raw numbered parts for Git storage. BOOT restores that file offline; it does not fetch a browser, accept an agreement or install a global runtime. The restored file must match the recorded full-file length and SHA-256 before startup continues. Retaining the numbered parts and restored DLL requires additional local disk space.

For a deliberate dependency replacement, repair, or an earlier source-only checkout, use Microsoft's [WebView2 download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) and [distribution documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution). Obtain the complete Windows x64 Fixed Version distribution under its applicable agreement. Preserve the exact vendor terms in `licenses/Microsoft-WebView2-Fixed-Runtime-License.txt` and all accompanying notices.

After extraction, `msedgewebview2.exe` must be directly under `runtime/webview2-fixed/`; retain all accompanying DLLs, data, locales, subdirectories and notices. A nested version-named parent folder is not the expected layout. Do not replace the full distribution with a single executable or merge vendor versions.

The earlier reference acquisition recorded this CAB identity:

```text
Microsoft.WebView2.FixedVersionRuntime.155.0.4283.45.x64.cab
bytes: 310544938
SHA-256: ec533bf1b7354d288703cf821a2d640d0130108224f93e330bb9c09faf3f8010
```

That identity records one prior artifact; it is not a signature or permission to relicense the vendor payload. Verify vendor provenance and signatures independently when obtaining dependencies. A different legitimate vendor version is a changed build configuration and needs a deliberate source/build update and fresh qualification.

## Build contract

Building is a developer operation, not an installation step for main's complete archive. Before changing source, `BOOT.cmd --verify` can restore the bundled split DLL and verify the supplied installation. Run `scripts/Build-Windows.ps1` when intentionally rebuilding project source with the full runtime present. It copies SDK adapters beside the presenter and regenerates package inventories. It does not execute qualification, accept licenses, install machine prerequisites, change global trust, or fetch missing dependencies.

The bundled application executables retain candidate.3's compiled Go/C# source identity, commit `629d44dc5c975ee5cb9d49576201d1b9aa5b4464`. Their existing build/application evidence does not by itself qualify the new repository ZIP and offline reassembly path; that distribution workflow is being validated separately.

The Fixed Version browser is selected explicitly at application launch and does not update itself automatically. Distributors must service it deliberately. Application use still depends on supported Windows OS components and a writable local directory. See [STATUS.md](STATUS.md) and [native/BUILD.md](native/BUILD.md) for the current acceptance boundary.
