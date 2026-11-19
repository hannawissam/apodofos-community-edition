# Security Policy

## Supported versions
| Version | Supported |
|---|---|
| Latest 1.x release (Community Edition) | yes (security fixes) |
| Older 1.x releases | upgrade to the latest 1.x |
| Pre-release / development builds | no |

Apodofos Professional Edition is a separate product with its own support terms.

## Reporting a vulnerability
Please **do not open a public issue or pull request** for a security problem.
* Use GitHub's **Private vulnerability reporting**: https://github.com/hannawissam/apodofos-community-edition/security/advisories/new (Security tab, "Report a
  vulnerability").
* Include: affected version, steps to reproduce, impact, and a proof of concept. **Do not send real AWR/ASH/Statspack
  reports or customer data**; use a synthetic sample. No PGP key is published at this time.

## What to expect
Acknowledgement within 5 working days, an assessment within 14 days, and a fix or mitigation plan appropriate to the
severity. We credit reporters who want credit. Please allow a reasonable time (e.g. 90 days) before public disclosure.

## Scope
The desktop and CLI applications (parsing of untrusted report files, HTML/JS UI escaping, file export). The app is offline
by design; a report of unexpected network activity is a high-priority issue. Out of scope: vulnerabilities in Microsoft
WebView2/.NET (report to Microsoft), social engineering, and issues requiring a compromised machine.
