# Windows source build

This repository contains the LK/Vexa Data System source snapshot derived from JP300 3.0.1. Compiled executables, WebView2 SDK binaries, the Microsoft Fixed Version browser runtime, historical archives, and mutable user data are excluded from Git. A clone is not a ready-to-launch binary release. Building and resealing a local installation does not establish clean-machine qualification.

## Developer prerequisites

Use 64-bit PowerShell on Windows x64. The application target is Windows 11 x64. Reference native builds use **Go 1.27.2**; `go.mod` requires Go 1.27.0 or later. The Go code uses the standard library and has no external module dependencies or CGO requirement. The Windows .NET Framework x64 C# compiler and WPF reference assemblies must be available under `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`.

Acquire the Microsoft components separately from Microsoft under their respective terms. Review and accept applicable vendor agreements yourself. The build script does not download software, accept licenses, alter machine security settings, install global dependencies, or substitute an installed Evergreen browser runtime.

1. Obtain **Microsoft.Web.WebView2 SDK 1.0.4191.47**. Place `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`, and the **x64** `WebView2Loader.dll` directly in `runtime/webview2-sdk/1.0.4191.47/`. Retain the vendor license/notice material. The managed assemblies come from the SDK's .NET Framework-compatible library directory; the loader comes from its `runtimes/win-x64/native/` directory.
2. Obtain the official **Fixed Version WebView2 runtime 155.0.4283.45 for Windows x64**. Extract its complete contents into `runtime/webview2-fixed/`, so `msedgewebview2.exe` is directly inside that directory. Preserve every vendor file, locale, subdirectory, license, and notice. Do not provide only the executable or merge files from different runtime versions.
3. Keep the checked-in `engine/ja21/runtime.integrity.json` as the inner seal template. The build procedure updates its listed source and executable hashes while retaining the **9.8.7 presenter ABI** identity.

The script checks representative runtime files and pinned file versions; those checks do not independently prove a complete vendor extraction or vendor authenticity. Verify the original download and vendor signatures using your organization's software acquisition procedures. Microsoft components remain outside the project's GPL grant.

## Build the local installation

From the repository root, in PowerShell:

```powershell
.\scripts\Build-Windows.ps1
```

To select a particular existing Go executable:

```powershell
.\scripts\Build-Windows.ps1 -GoExe 'D:\Tools\go\bin\go.exe'
```

The script checks prerequisites before compilation, selects `native/` as the Go working directory, builds `bin/JPBroker.exe` and the precompiled `bin/JPTests.exe`, compiles the six presenter sources into `bin/JPConsole.exe`, copies the three SDK adapter DLLs beside the presenter, and runs `native/refresh-manifests.ps1`. C# compilation references the OS-provided WPF/System.Xaml/System.Web.Extensions assemblies and the pinned managed SDK adapters; its entry point is `VBJA21.JPConsoleEntry` and its target is x64.

Compiler caches and temporary files are placed in `workspace/build/`. Environment changes apply to the current build invocation and are restored in a `finally` block. Automatic Go toolchain/module acquisition is disabled. Go's `-work` option preserves intermediate compiler directories to avoid the previously observed slow cleanup on the development machine. No recursive deletion is performed; manage that directory separately when no build is running. Build tooling may retain its own pre-existing user settings; the script does not change global Go configuration or telemetry preferences.

The manifest updater changes tracked integrity files after a local build. This is expected: source inventories and locally assembled binary distributions have different contents. Review those changes before any commit. `workspace/`, certificates, active server configuration, and Git metadata are excluded from the application inventory. SHA-256 inventories detect byte changes; they are not publisher signatures or independent authenticity attestations.

## Verify and qualify

From the assembled repository root:

```powershell
.\BOOT.cmd --verify
.\BOOT.cmd --doctor
.\BOOT.cmd --qualify
```

The build script compiles tests; it does not execute them. `BOOT.cmd --qualify` runs the precompiled native regression executable, checks the managed presenter probe, and attempts the real application/VCMD smoke workflow. It uses an isolated package-local qualification workspace and records its results there. A passing run on the development host remains a same-host result. Follow `CLEAN_MACHINE_TEST.txt` and provide a genuine clean Windows 11 guest or separate PC before asserting clean-machine certification. The current source snapshot has an incomplete qualification status; do not infer a successful release from a successful build.

For a developer-only native test run:

```powershell
Set-Location .\native
go test -buildvcs=false ./...
```

The optional actual JavaScript/native-broker integration test requires explicitly setting `JA21_ENGINE_NODE` to a developer Node executable; it uses the retained appliance modules and real broker sockets. The optional JavaScript suite can be run from `appliance/` with a developer Node installation using `node --test tests/core.test.mjs tests/client-flow.test.mjs`. Node and Go are development tools, not application startup dependencies.

The `.ja` interpreter executes the admission profile at `source/jp250-service.ja`; `native/ja21-profile.ja` is its fixture. Tests describe the supported hosted syntax and rejection boundaries. This is a limited interpreted service profile, not a general JA21 compiler. Photon tests retain golden compatibility data from the earlier implementation and do not establish physical fabric or four-node execution.

## Controlled source changes

After deliberately changing immutable sources or binaries, rebuild affected outputs and regenerate the inner and outer inventories through the supplied updater. Preserve the presenter's 9.8.7 ABI version unless making an intentional ABI change. Do not bypass an integrity failure to launch changed bytes. Rebuilding with a different toolchain or Microsoft component version is a new configuration that requires review and qualification, not the existing reference configuration.

Primary vendor documentation: [WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) and [official runtime download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).
