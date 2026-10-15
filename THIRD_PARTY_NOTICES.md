# Third-party and platform notices

The root GPL grant covers authorized project-owned material. Separately retained grants remain effective; see [LICENSING.md](LICENSING.md).

- Generic Photon inputs retain the Apache-2.0 license and attribution in `photon/LICENSE.txt` and `photon/NOTICE.txt`.
- JA21 corpus and inherited attribution remain in `licenses/JA21-Corpora-Apache-2.0.txt`, `licenses/JA21-INHERITED-NOTICE.txt` and `appliance/licenses/JA21-*`.
- Go's BSD notice is retained in `licenses/Go-LICENSE.txt`. The Go compiler/toolchain is a development prerequisite and is not distributed here.
- Microsoft owns the WebView2 SDK and Fixed Version Runtime. Their binaries are excluded from Git. The official Fixed Version terms are retained in `licenses/Microsoft-WebView2-Fixed-Runtime-License.txt`. Retained SDK terms are in `licenses/Microsoft-WebView2-LICENSE.txt` and `licenses/Microsoft-WebView2-NOTICE.txt`. Acquire vendor software from the official Microsoft distribution and preserve its notices when assembling a local package.
- The bundled Microsoft runtime includes Microsoft Defender SmartScreen and can collect and send end-user information to Microsoft as described in [Microsoft's Privacy Statement](https://aka.ms/privacy) and [Edge Privacy Whitepaper](https://learn.microsoft.com/en-us/microsoft-edge/privacy-whitepaper#smartscreen). Preserve Microsoft's privacy disclosures and interfaces. Distributors must provide the applicable vendor terms and required notices to their end users. This repository does not remove vendor notices or change vendor terms.
- Windows, .NET Framework and WPF remain operating-system components. No ownership or relicensing claim is made over them.

Reference dependencies for the existing source candidate are WebView2 SDK **1.0.4191.47** and Fixed Version Runtime **155.0.4283.45 x64**. A dependency update requires fresh qualification; a version listed here is not a promise of ongoing vendor support or an automatic-update mechanism.

Project-owned historical HERMIT attribution is retained separately under `licenses/HERMIT-HISTORICAL-NOTICE.txt`; the active project-owned GPL grant supersedes that historical restriction.
