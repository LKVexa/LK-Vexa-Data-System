# 3.0.1 — 2026-10-14

Consolidates all supported startup, diagnostics, tests, network, script and restoration modes into one BOOT.cmd. Normal startup checks the package and uses loopback without inheriting server-config.json. TLS network access and explicit unencrypted LAN testing remain deliberate command-line modes.

Adds an offline installation qualifier using the shipped regression executable and actual WPF/WebView2 command Worker, with separate test data, file readback, native Photon verification/snapshot and acknowledged graceful shutdown. The qualifier records its executing OS and never infers a clean Windows installation. Includes a separate clean-guest procedure and attestation template.

Retains disposable regression fixtures with qualification evidence, avoiding the observed Windows temporary-directory cleanup stall. Normal developer test runs retain their automatic cleanup behavior.

Handles relocated paths containing spaces, symbols and non-English text in launcher and diagnostic output. Streams large-file integrity checks through a fixed-size buffer. Fixes platform diagnostic serialization, isolates presenter data during qualification, and waits for the broker to receive the final persistence acknowledgement after presenter exit.

# 3.0.0 — 2026-10-14

Replaces the Python bootstrap, broker, storage and Photon execution path with compiled Windows x64 executables. Startup is offline and does not install or compile a runtime. Adds a real limited JA source 0.2 interpreter controlling operation admission and decision routing.

Bundles Microsoft Fixed Version WebView2 155.0.4283.45 x64 and retains the precompiled native WPF presenter, original command engine, catalog, Commands/Scripts interface, .vcmd examples, grant-bound native source reads, standalone execution, volume restoration and authenticated WS/WSS. A fresh launch defaults to loopback; an existing reviewed server-config.json is honored. Encrypted network and explicit test-LAN launchers remain available.

Uses the standard WPF WebView2 control for the console's single document surface, resolving the observed composition-controller window-creation failure. Adds bounded, credential-redacted startup and window lifecycle diagnostics. Standalone VCMD now defaults to a private loopback listener on an assigned ephemeral port and ignores normal server configuration; network overrides are rejected for that isolated job.

Retains volume and Generic Photon 0.2.0 state/ledger/receipt/snapshot compatibility, with identity continuity and failure checks. Implements native Doctor, complete immutable manifest coverage, precompiled regression tests, bounded JSON/RFC6455 transport, strict option parsing, token separation, readiness and draining shutdown.

The major version marks the host migration and removal of first-launch dependency provisioning. The presenter ABI remains 9.8.7. This release implements a limited hosted JA21 profile; it does not assert general JA21, R12/MCRT, MSSL or LCTL runtime conformance.
