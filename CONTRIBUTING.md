# Contribution and change control

Contributions should identify the concrete behavior changed, the affected source and schema/protocol contracts, relevant failure modes, and observed verification. Small focused changes are easier to review than unrelated rewrites.

## Compatibility boundaries

Preserve the documented `JA21-VOLUME/1`, `JA21-EXPORT/1`, Photon formats, `hermit.vws.v2` framing, private bootstrap protocol and presenter ABI unless a deliberate migration is supplied. Keep denied operations denied on parse, type, identity, integrity or persistence failures. Do not substitute host-shell execution for virtual command behavior.

Changing names or documentation must not imply a broader JA compiler/VM, physical fabric execution, security certification or clean-machine qualification than the source and evidence establish.

## Verification

Build on Windows x64 using the documented toolchain and separately acquired SDK/runtime. Run native tests from `native/`; use the assembled package's `BOOT.cmd --qualify` for actual managed/Worker qualification. Optional Node developer integration is separate and must be identified when used or skipped.

When changing immutable source or binaries, rebuild and regenerate both inventories using the supplied build/manifest workflow. Preserve failed results. A patch is not validated merely because a source formatter, static analyzer or offline Doctor report succeeds.

Document storage migration and backup requirements for persisted-format changes. Test meaningful authorization, failure and recovery behavior appropriate to the change. Do not claim an entire suite passed when cases were skipped or a run was interrupted.

## Licensing and repository hygiene

Only contribute material you have authority to license under the project's applicable terms. Preserve third-party notices. Do not add binaries, archived private bundles, vendor browser runtimes, secrets, certificates, active configuration, volumes, browser profiles or private execution logs. See LICENSING and SECURITY.

The repository does not assert a contributor agreement, signed-off-by requirement, formal governing body, mandatory review quorum, automated CI gate or support SLA. Any such process must be established explicitly rather than implied by this document.
