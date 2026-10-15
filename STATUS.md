# Engineering and qualification status

Project: **LK/Vexa Data System**. Source lineage: **JP300 3.0.1 candidate**. Presenter compatibility ABI: **9.8.7**.

This publication is a source snapshot. It is not a certified, complete binary release, and it does not establish clean-machine portability.

| Evidence or change | Status |
| --- | --- |
| Single BOOT.cmd and native mode dispatch | Implemented in source; initial candidate built |
| Relocated immutable inventory check | Observed on the existing Windows 11 x64 host |
| Actual CLR/WPF/SDK and Fixed renderer probe | Passed on the existing host, including a path with spaces/symbols/non-English characters and Internet download marking on key files |
| Read-only managed probe | Observed not to create normal application data |
| Full current native regression run | Incomplete: 28 top-level groups passed, zero group failures reported, then the ten-minute test timeout occurred in automatic temporary-directory cleanup |
| Fixture-retention correction | Present in source; not rebuilt or validated at this publication checkpoint |
| Current complete WPF/Worker command qualification | Pending |
| Current no-argument BOOT end-to-end check | Pending |
| Genuine clean Windows 11 x64 qualification | Pending; no clean guest was available |

Subsequent native tool/probe processes stalled and separate filesystem operations showed delays. The cause was not established. This environmental observation is not proof of a product defect or proof that a restart resolves it.

Earlier **3.0.0** evidence recorded 44 passed native top-level groups, 83 passed retained JavaScript tests, actual WPF/Worker startup and standalone command execution. Those are historical baseline results and are not a current 3.0.1 suite pass. Skipped optional developer integrations or denied hard-link fixtures are not passed tests.

## Windows packaging and path correction

The repository source ZIP excludes compiled programs and Microsoft runtime binaries. Named Windows release assets contain those components; see RELEASES.md. The older 372,186,648-byte ZIP passed all 409 member CRC/length checks and its recorded SHA-256. Its size primarily comes from runtime dependencies and an unnecessary nested history archive.

The current source adds a 100 UTF-16-unit root budget, native/managed path guards, bounded qualification paths, short same-directory atomic temporary names, source/binary build receipts and audited packaging. The PowerShell ZIP/path checker passed 34 synthetic cases on this Windows host; the scripts parsed without errors. The source/binary receipt comparison also rejected an intentionally changed source hash. These checks do not validate rebuilt Go/C# executables or an actual browser workload. See WINDOWS_PORTABILITY.md.

Local Go/native process starts remained unreliable during this work. A manual hosted Windows workflow is provided for fresh compilation and regression evidence; an unexecuted workflow is not a test result. The legacy reference package preserves 3.0.0 binaries and corresponding source and does not contain the new native 3.0.1 guards.

## Acceptance gates for a binary release

1. Rebuild executables from the exact reviewed source and record toolchain/dependency identities.
2. Regenerate the presenter seal and complete immutable inventory after assembly.
3. Complete all native regressions, separating PASS, FAIL and SKIP.
4. Complete actual WPF/WebView2 Worker readiness, VCMD file readback, Photon verification/snapshot and acknowledged graceful close.
5. Verify normal BOOT defaults to loopback and honors its configuration boundaries.
6. Verify immutable package contents after execution and independently verify the final archive.
7. Execute the final artifact on a genuine clean Windows 11 x64 installation, preserving independent image/reset provenance and all successful and failed results.

The native installation report intentionally retains `cleanMachineCertified=false`: software cannot infer clean OS provenance from a fresh folder or restricted PATH. Use `CLEAN_MACHINE_TEST.txt` and `GUEST-ATTESTATION.template.json` for the separate operator record. No historical test total, static dependency audit or unsigned hash inventory substitutes for these gates.
