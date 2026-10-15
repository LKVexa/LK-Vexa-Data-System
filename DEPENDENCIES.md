# Dependency acquisition and local assembly

This source repository excludes generated executables and Microsoft binary distributions. The reference configuration is explicit and is not a claim that these are the newest available vendor versions.

| Dependency | Reference | Required location or role |
| --- | --- | --- |
| Go | 1.27.2; module language floor 1.27.0 | Developer compiler; selected with `-GoExe` or developer PATH |
| Windows .NET Framework | OS-provided x64 CLR/WPF and compiler | `%WINDIR%/Microsoft.NET/Framework64/v4.0.30319/` |
| WebView2 SDK | 1.0.4191.47 | Three adapter files in `runtime/webview2-sdk/1.0.4191.47/` |
| WebView2 Fixed Version Runtime | 155.0.4283.45, Windows x64 | Complete vendor tree in `runtime/webview2-fixed/` |
| Node.js | Optional developer tool | Retained JavaScript and optional integration tests; never production startup |

## Microsoft SDK

The reference package is [Microsoft.Web.WebView2 1.0.4191.47 on NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47), published by Microsoft. Acquire the package and preserve its license and notice material. From its .NET Framework-compatible library directory and Windows x64 native runtime directory, stage these files directly in the SDK location:

```text
runtime/webview2-sdk/1.0.4191.47/
  Microsoft.Web.WebView2.Core.dll
  Microsoft.Web.WebView2.Wpf.dll
  WebView2Loader.dll
```

Use the x64 loader rather than an x86/ARM loader. Do not combine assemblies from different package versions. The build validates the managed file versions and native loader architecture; those checks do not independently authenticate the original download.

## Fixed Version browser

Use Microsoft's [WebView2 download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) and [distribution documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution). Acquire the complete Windows x64 Fixed Version distribution under its applicable agreement. This repository does not accept that agreement or download/install a runtime automatically.

After extraction, `msedgewebview2.exe` must be directly under `runtime/webview2-fixed/`; retain all accompanying DLLs, data, locales, subdirectories and notices. A nested version-named parent folder is not the expected layout. Do not replace the full distribution with a single executable or merge vendor versions.

The earlier reference acquisition recorded this CAB identity:

```text
Microsoft.WebView2.FixedVersionRuntime.155.0.4283.45.x64.cab
bytes: 310544938
SHA-256: ec533bf1b7354d288703cf821a2d640d0130108224f93e330bb9c09faf3f8010
```

That identity records one prior artifact; it is not a signature or permission to relicense the vendor payload. Verify vendor provenance and signatures independently when obtaining dependencies. A different legitimate vendor version is a changed build configuration and needs a deliberate source/build update and fresh qualification.

## Build contract

Run `scripts/Build-Windows.ps1` after staging dependencies. It compiles project source, copies SDK adapters beside the presenter and regenerates the two package inventories. It does not execute qualification, accept licenses, install machine prerequisites, change global trust, or fetch missing dependencies.

The Fixed Version browser is selected explicitly at application launch and does not update itself automatically. Distributors must service it deliberately. Application use still depends on supported Windows OS components and a writable local directory; source availability alone does not establish a qualified portable binary release. See STATUS and native/BUILD for the current acceptance boundary.
