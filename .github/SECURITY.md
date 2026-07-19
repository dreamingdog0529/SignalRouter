# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security vulnerabilities.

Report security issues as soon as they are discovered using one of the following:

1. **GitHub Security Advisories** (recommended): use [Report a vulnerability](https://github.com/dreamingdog0529/SignalRouter/security/advisories/new) on this repository.
2. If private reporting is unavailable, contact the maintainers through a private channel and reference this document.

This project limits its runtime dependencies where practical in order to reduce the total cost of ownership, but all consumers should remain vigilant and have their security stakeholders review third-party products (3PP) like this one and their dependencies.

Maintainers: see [docs/security-hardening.md](../docs/security-hardening.md) for supply-chain hardening guidance (action pinning, dependency auditing, secret rotation, AI agent vetting).

### What to include

- Description of the issue and potential impact
- Steps to reproduce or a proof of concept (if available)
- Affected version / commit (if known)
- Suggested fix (optional)

### What to expect

- Acknowledgement when maintainers see the report
- A fix or mitigation plan when the issue is confirmed
- Credit in the advisory or changelog if you want it (say so in the report)

## Supported versions

Security fixes are applied to the latest released version on the default branch. Older releases may not receive backports unless a fix is trivial and still relevant.

| Version | Supported |
|---------|-----------|
| Latest release | Yes |
| Older releases | Best effort |

Thank you for helping keep SignalRouter and its users safe.
