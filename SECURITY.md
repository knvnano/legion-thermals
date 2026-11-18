# Security policy

This app requests administrator access and can change cooling settings on its supported hardware. Only install binaries from releases you trust; binaries are unsigned and accompanied by SHA256 checksums.

For a vulnerability involving arbitrary code execution, credential exposure or unsafe writes, use this repository's **Security → Report a vulnerability** private advisory flow when available. Do not post exploit details or private diagnostics in a public issue. A routine compatibility or display bug can use the bug-report template.

No account, network telemetry, saved password, separate service or kernel driver is required by the application. Startup tasks use the current administrator user's interactive token. Review source and release notices before use. This project has no guaranteed security response SLA; fixes target the latest release.
