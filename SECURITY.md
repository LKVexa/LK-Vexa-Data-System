# Security model and reporting

This project is a development source candidate. No security certification, external audit, response SLA, multitenant isolation or production support commitment is asserted.

The trusted computing base includes Windows, .NET/WPF, the selected WebView2 runtime/adapter, the managed presenter, native broker, reviewed policy source, owner Worker and package integrity inventories. A malicious administrator or same-user process that can replace the program and its unsigned inventories is outside the local integrity model.

Normal startup uses loopback. Network mode requires operator-provided TLS and exact origin configuration. The terminal credential represents shared bearer authority; it does not provide individual accounts, roles or tenant separation. The virtual filesystem is shared across terminal sessions. The distinct engine credential is more privileged and is supplied through private redirected stdin rather than command-line arguments.

Never publish live credentials, private keys, active configuration, user volumes, browser profiles, identity state, raw private logs or complete working-package archives in an issue or commit. The repository excludes these artifacts by design. Public test literals are fixtures, not production secrets.

For a suspected vulnerability, use GitHub's private vulnerability reporting if the repository exposes that option. Otherwise open a minimal issue requesting a private reporting route without disclosing exploit details or sensitive data. No dedicated private reporting channel is claimed to exist until the maintainer provides one.

Dependency servicing belongs to the distributor. A Fixed Version browser does not update itself automatically. Verify vendor provenance, preserve notices, review security updates and requalify changed builds. Do not disable Windows protections or certificate validation to make a failing launch succeed.

Report affected commit/version, platform/build, mode, minimal reproduction and the relevant trust boundary. Clearly distinguish virtual command effects from native host capabilities. See README and STATUS for implemented bounds, limitations and current acceptance gaps.
