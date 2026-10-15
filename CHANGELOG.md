# 3.0.0 — 2026-10-14

Replaces the Python bootstrap, broker, storage and Photon execution path with compiled Windows x64 executables. Startup is offline and does not install or compile a runtime. Adds a real limited JA source 0.2 interpreter controlling operation admission and decision routing.

Bundles Microsoft Fixed Version WebView2 155.0.4283.45 x64 and retains the precompiled native WPF presenter, original command engine, catalog, Commands/Scripts interface, .vcmd examples, grant-bound native source reads, standalone execution, volume restoration and authenticated WS/WSS. A fresh launch defaults to loopback; an existing reviewed server-config.json is honored. Encrypted network and explicit test-LAN launchers remain available.

Uses the standard WPF WebView2 control for the console's single document surface, resolving the observed composition-controller window-creation failure. Adds bounded, credential-redacted startup and window lifecycle diagnostics. Standalone VCMD now defaults to a private loopback listener on an assigned ephemeral port and ignores normal server configuration; network overrides are rejected for that isolated job.

Retains volume and Generic Photon 0.2.0 state/ledger/receipt/snapshot compatibility, with identity continuity and failure checks. Implements native Doctor, complete immutable manifest coverage, precompiled regression tests, bounded JSON/RFC6455 transport, strict option parsing, token separation, readiness and draining shutdown.

The major version marks the host migration and removal of first-launch dependency provisioning. The presenter ABI remains 9.8.7. This release implements a limited hosted JA21 profile; it does not assert general JA21, R12/MCRT, MSSL or LCTL runtime conformance.
