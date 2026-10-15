# Licensing and component boundaries

**LK/Vexa Data System — Copyright (C) 2026 RUSSELL PHILIP SMITHSON.**

The project-owned implementation and documentation are licensed under **GNU GPL version 3 only**, identified as `GPL-3.0-only`. The complete, unmodified license text is in [LICENSE](LICENSE). The current grant and retained attribution are recorded in [COPYRIGHT](COPYRIGHT) and [NOTICE](NOTICE).

## Current grant and historical provenance

The rights holder has authorized GPL-3.0-only publication of the included project-owned console integration and HERMIT/SPIRAL/VT/VWS material. For that owned material, the current grant supersedes prior private-use or all-rights-reserved restrictions. Historical notices are retained to preserve attribution and lineage; they are not an alternative active restriction on material covered by the current grant. The active HERMIT notice is in `appliance/licenses/HERMIT-LICENSE.txt`; its superseded wording is retained in `licenses/HERMIT-HISTORICAL-NOTICE.txt`.

This grant applies only to material within the granting rights holder's authority. It does not transfer third-party ownership or replace separately retained third-party licenses.

| Component | Treatment in this repository |
| --- | --- |
| Native integration, managed presenter, admission profile and project documentation | GPL-3.0-only for project-owned material, subject to retained applicable component attribution |
| Project-owned HERMIT/SPIRAL/VT/VWS source | Current GPL-3.0-only grant; historical attribution preserved |
| Generic Photon configuration/program inputs and associated notices | Retained Apache-2.0 grant in `photon/LICENSE.txt` and `photon/NOTICE.txt` |
| Retained JA21 corpus material and inherited attribution | See `licenses/JA21-Corpora-Apache-2.0.txt`, `licenses/JA21-INHERITED-NOTICE.txt` and `appliance/licenses/JA21-*`; retain applicable notices |
| Go standard library incorporated into the supplied native executables and future builds | BSD terms retained in `licenses/Go-LICENSE.txt`; the Go development toolchain is not included |
| Microsoft WebView2 SDK | Bundled binary dependency; vendor terms retained in `licenses/Microsoft-WebView2-LICENSE.txt` and `licenses/Microsoft-WebView2-NOTICE.txt` |
| Microsoft Fixed Version WebView2 Runtime | Bundled separately licensed vendor payload; complete agreement retained unchanged in `licenses/Microsoft-WebView2-Fixed-Runtime-License.txt`; not covered by the project GPL grant |
| Windows/.NET Framework/WPF | Operating-system prerequisites; not supplied or relicensed by this repository |

## Repository and assembled distributions

The current main branch and its Code ZIP include project source, compiled candidate.3 application/test programs, WebView2 SDK adapters and the complete Fixed Version Runtime payload. Only the oversized `msedge.dll` is represented by ordered 48 MiB raw parts under `payload/webview2/msedge.dll.000` and subsequent numbered files. `BOOT.cmd` restores and verifies the exact vendor DLL offline before starting the application. Ordinary users do not need a runtime download or source build. Microsoft software remains separately licensed; raw split-file storage does not alter the reconstructed vendor bytes or applicable terms.

Earlier source-only commits/tags retain source-only archives. Main excludes private user state, active credentials/configuration, certificates, keys and nested historical packages. The supplied application executables retain candidate.3's compiled Go/C# source identity `629d44dc5c975ee5cb9d49576201d1b9aa5b4464`; the repository-archive/restoration workflow is a separate distribution validation scope, described in STATUS.md.

Developer rebuilds still require the documented compiler tools and complete local dependency layout. The build script does not download dependencies, accept vendor agreements, change system trust or install prerequisites.

Any party distributing an assembled binary package must preserve all applicable notices and satisfy the licenses of the components actually distributed. A project GPL file does not grant permission to relicense vendor browser binaries. The repository's technical description and file inventories are not legal certification or a declaration of third-party clearance beyond the explicit grants recorded here.

## License text integrity

Do not replace the Free Software Foundation copyright inside the GPL license text with the project copyright. The project's requested attribution belongs in COPYRIGHT, NOTICE, source notices and README. Preserve third-party attribution when modifying or redistributing affected components.
