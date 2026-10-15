# Native build and tests

Application startup uses the supplied executables. Building is optional and requires Go 1.27.2 and the Windows .NET Framework C# compiler. No external Go modules are used. These developer tools are not target-machine prerequisites.

From this directory, run:

```powershell
go test ./...
go build -trimpath -ldflags="-s -w" -o ..\bin\JPBroker.exe .
go test -c -trimpath -o ..\bin\JPTests.exe
```

The precompiled regression executable expects this directory as its working directory. `VERIFY.cmd` sets a package-local temporary directory and runs it. The optional actual JavaScript/native-broker integration test requires explicitly setting `JA21_ENGINE_NODE` to a developer Node executable; it uses the retained engine modules and real native broker sockets. Production startup never reads that variable or invokes Node.

The `.ja` source interpreter executes the admission profile at `../source/jp250-service.ja`. `ja21-profile.ja` is its test fixture. Supported syntax and rejection boundaries are exercised in ja21_test.go. Storage, Photon compatibility, protocol and failure-case tests are supplied with source. Photon golden data was produced by the original Python implementation before the native rewrite.

Compile the native presenter from the six files in `../host`: JA21VirtualBrowser.cs, Vec1Substrate.cs, PortableDesktop.cs, LiveWebContracts.cs, LiveWebBridge.cs and JPConsoleEntry.cs. Select main type VBJA21.JPConsoleEntry, x64, and reference the Windows .NET Framework WPF/System.Xaml/System.Web.Extensions assemblies plus the two retained managed WebView2 adapter DLLs. Keep WebView2Loader.dll and the managed adapter DLLs beside JPConsole.exe. Use the runtime/webview2-fixed directory for the official complete Fixed Version runtime; startup fails if it is absent.

After deliberately changing immutable source or binaries, regenerate the presenter inner seal while retaining its 9.8.7 ABI version, then regenerate the complete outer SHA256SUMS.txt. Those files are integrity inventories, not signing credentials. Exclude only workspace, certs, server-config.json and the outer inventory itself. Do not bypass a failed verifier to launch modified bytes.
