# Licensing and component boundaries

**LK/Vexa Data System — Copyright (C) 2026 RUSSELL PHILIP SMITHSON.**

The project-owned implementation and documentation in this source distribution are licensed under **GNU GPL version 3 only**, identified as `GPL-3.0-only`. The complete, unmodified license text is in [LICENSE](LICENSE). The current grant and retained attribution are recorded in [COPYRIGHT](COPYRIGHT) and [NOTICE](NOTICE).

## Current grant and historical provenance

The rights holder has authorized GPL-3.0-only publication of the included project-owned console integration and HERMIT/SPIRAL/VT/VWS material. For that owned material, the current grant supersedes prior private-use or all-rights-reserved restrictions. Historical notices are retained to preserve attribution and lineage; they are not an alternative active restriction on material covered by the current grant. The active HERMIT notice is in `appliance/licenses/HERMIT-LICENSE.txt`; its superseded wording is retained in `licenses/HERMIT-HISTORICAL-NOTICE.txt`.

This grant applies only to material within the granting rights holder's authority. It does not transfer third-party ownership or replace separately retained third-party licenses.

| Component | Treatment in this repository |
| --- | --- |
| Native integration, managed presenter, admission profile and project documentation | GPL-3.0-only for project-owned material, subject to retained applicable component attribution |
| Project-owned HERMIT/SPIRAL/VT/VWS source | Current GPL-3.0-only grant; historical attribution preserved |
| Generic Photon configuration/program inputs and associated notices | Retained Apache-2.0 grant in `photon/LICENSE.txt` and `photon/NOTICE.txt` |
| Retained JA21 corpus material and inherited attribution | See `licenses/JA21-Corpora-Apache-2.0.txt`, `licenses/JA21-INHERITED-NOTICE.txt` and `appliance/licenses/JA21-*`; retain applicable notices |
| Go standard library incorporated into future builds | BSD terms retained in `licenses/Go-LICENSE.txt`; the Go development toolchain is not included |
| Microsoft WebView2 SDK | Binary dependency acquired separately; vendor terms retained in `licenses/Microsoft-WebView2-LICENSE.txt` and `licenses/Microsoft-WebView2-NOTICE.txt` |
| Microsoft Fixed Version WebView2 Runtime | Separately acquired, separately licensed vendor distribution; not included in this source repository and not covered by the project GPL grant |
| Windows/.NET Framework/WPF | Operating-system prerequisites; not supplied or relicensed by this repository |

## Source repository and assembled distributions

This Git repository contains project source and notices. It is not a ready-to-run portable binary release. A local assembly requires the compiled application/test programs and the separately acquired Microsoft SDK and complete Fixed Version Runtime. The build script does not download these dependencies, accept vendor agreements, change system trust, or install prerequisites.

Any party distributing an assembled binary package must preserve all applicable notices and satisfy the licenses of the components actually distributed. A project GPL file does not grant permission to relicense vendor browser binaries. The repository's technical description and file inventories are not legal certification or a declaration of third-party clearance beyond the explicit grants recorded here.

## License text integrity

Do not replace the Free Software Foundation copyright inside the GPL license text with the project copyright. The project's requested attribution belongs in COPYRIGHT, NOTICE, source notices and README. Preserve third-party attribution when modifying or redistributing affected components.
