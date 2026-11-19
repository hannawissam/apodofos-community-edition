# Contributing to Apodofos Report Analyzer (Community Edition)

Thank you for your interest. Apodofos Community Edition is licensed **AGPL-3.0-only**. Because the maintainer may also license the
code under other terms (see `CLA.md`), any accepted contribution requires the CLA.

## Contribution policy
For now the project does **not** accept external code contributions by pull request unless the maintainer has agreed to them
beforehand in an issue. Pull requests opened without that prior agreement may be closed without review.

Welcome through issues: bug reports, feature requests and documentation suggestions (see `SECURITY.md` for security issues).

The steps below apply only to a contribution the maintainer has invited.

The public repository is <https://github.com/hannawissam/apodofos-community-edition>; open issues there.

## Before you start
1. **Accept the CLA** (`CLA.md`): tick the CLA checkbox in the pull request template, or write this exact sentence in the pull
   request description or in a comment: `I have read the CLA and agree to it`. The maintainer will not merge a pull request
   without it. If your employer may own your work, you need its written permission first.
2. **Sign off your commits** (DCO): `git commit -s` adds `Signed-off-by: Your Name <you@example.com>` (see
   https://developercertificate.org). The DCO sign-off is requested in addition to the CLA, not instead of it.
3. Open an issue first and wait for the maintainer's agreement; no pull request is accepted without it.

## Content rules (important)
* **No third-party reports or samples.** Do not add real AWR, ASH, Statspack or other database reports (from oracle.com,
  oracle-base.com, your employer, customers, forums). Use only **synthetic** reports you created with invented names, ids,
  hosts and SQL. Do not add test data derived from real reports.
* **No Oracle documentation text** (or other vendor text) copied into the knowledge base, rules, comments or docs.
  Paraphrase in your own words and cite by link only. No Oracle scripts, My Oracle Support content, logos or images.
* No secrets, keys, licence-key code or proprietary code, and no customer data or hostnames.
* Only code you wrote or have the right to submit. State third-party code and its licence in the pull request; it must be
  compatible with AGPL-3.0 and with relicensing by the maintainer (permissive MIT/BSD/Apache-2.0 only).
* Keep the product offline: no network calls, telemetry or external resources in the UI.
* Every rule needs an evidence-based threshold, plain-English text, technical text, recommendations, valid Oracle SQL and
  an official documentation link (see `CLAUDE.md`).
* Never use "Oracle", "AWR", "ASH" or "Statspack" in product names, logos, domains or branding.

## Code style and checks
`dotnet build Apodofos.slnx` and `dotnet test` must pass. Source files carry the header
`SPDX-License-Identifier: AGPL-3.0-only` and `Copyright (c) 2026 Wissam HANNA` (see `tools/spdx-headers.ps1`).

## Security issues
Do not open a public issue: see `SECURITY.md`.

## Conduct
See `CODE_OF_CONDUCT.md`. Do not paste unredacted reports in issues: remove host names, schema names and SQL text.

## Forks and the name
You may fork under the AGPL, but must rename the project and not use the Apodofos name or logo (`TRADEMARK.md`).
