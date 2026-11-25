# Apodofos Performance Report Analyzer (Community Edition)

<img src="assets/apodofos-256.png" width="96" align="right" alt="Apodofos Performance Report Analyzer icon"/>

An **offline** Windows tool that analyzes Oracle **AWR**, **ASH** and **Statspack** reports (HTML or text). It never
connects to a database and never uses the internet: you give it a report file, it gives you a diagnosis.

## About the name
*Apodofos* (pronounced a-PO-do-fos) combines two Greek words: *apodosi* (Απόδοση, "performance") and *fos* (Φως,
"light"): the light of performance (φως απόδοσης). The tool throws light on the performance of your database.

## What you see
Open a report and get, side by side:

- the **original report**, with jump-to-section links;
- **plain-English findings** a non-DBA can follow: what is wrong, why it matters, what to do, plus a technical view for DBAs;
- a **confidence level** and data-quality notes on every finding, and related findings grouped by root cause;
- a **Fix first plan**: actions ranked by expected payoff, with impact, effort, risk and confidence;
- **charts** for the metrics in the report (wait events, load profile, time model, top SQL, I/O, memory, segments,
  contention, ASH activity) and an "All metrics" explorer that charts any table;
- **recommendations and ready-to-run Oracle 19c SQL** (tuning tasks, plan baselines, index/redo/memory checks), each with
  cautions;
- an **offline documentation catalog**: findings link to the relevant Oracle Database 19c documentation pages. The
  catalog (titles, URLs, anchors, short own-words summaries) ships inside the app; opening a link is your choice, and the
  app itself makes no request.

## Features
- Three report types: AWR (HTML and text), ASH (HTML and text), Statspack (text). The type is detected from the content.
- Rules engine based on the Oracle performance method (DB time, wait events, CPU, parsing, SQL, I/O, memory advisories,
  redo/undo, resource limits, parameters, ASH hot spots, Statspack equivalents). Rules are auto-discovered `IRule` classes.
- Health score, KPI tiles, severity-ranked findings with evidence.
- Exports: self-contained interactive HTML dashboard, JSON, SQL script, print/PDF.
- Desktop app (WinForms + WebView2) and a command line tool for automation.

## Requirements
- Windows 10/11 and the Microsoft Edge WebView2 Evergreen runtime (included with Windows 11 and Edge) for the desktop app.
  The CLI does not need WebView2.
- To build: .NET 11 SDK. Published builds are self-contained (no .NET install needed).

## Build, run, test, publish
```
git clone https://github.com/hannawissam/apodofos-community-edition.git
cd apodofos
dotnet build Apodofos.slnx
dotnet test
dotnet run --project src/Apodofos.Desktop
dotnet run --project src/Apodofos.Cli -- report.html --text
./publish.ps1        # self-contained single-file desktop and CLI executables in artifacts/ (git-ignored)
```

## Command line
```
ApodofosReportAnalyzer <report> [--html out.html] [--json out.json] [--script out.sql] [--text]
ApodofosReportAnalyzer --version
ApodofosReportAnalyzer --license
ApodofosReportAnalyzer --help
```
The installer and the portable zip also provide the former command name `apodofos` as an alias, so existing scripts keep working. With no output option, `--text` is assumed. Exit codes: 0 ok, 1 usage error, 2 file unreadable, 3 not a valid
AWR/ASH/Statspack report, 4 other error. `--license` prints the copyright, licence, no-warranty and Oracle
non-affiliation notices.

## Supported reports
| Report | Formats | Notes |
|---|---|---|
| AWR | HTML, text | Layouts from 11g to 19c+ are parsed; rules are calibrated for 19c. |
| ASH | HTML, text | `ashrpt` output; activity by event, SQL, session, module and time slice. |
| Statspack | text | 10g-style layout and a 19c-style layout; mapped onto the same rules as AWR where an equivalent exists. |

## Limitations (please read)
- **Text ASH and 12c+ Statspack layouts are inferred.** They are implemented from the documented report structure and
  from synthetic fixtures written for this project, not validated against a broad set of real customer reports. Expect
  sections to be missed on layouts we have not seen; missing sections produce warnings, never crashes.
- The project does not redistribute third-party Oracle reports. The test suite includes optional regression tests over a
  git-ignored local corpus (`EXAMPLES-local/`); on a clean checkout those tests skip and the suite stays green, so
  real-world coverage depends on what the maintainer has locally.
- Thresholds are general guidance, not a substitute for knowing your workload. Findings carry a confidence level for a
  reason; treat low-confidence items as questions to check.
- Reports are static snapshots: the tool cannot see anything the report does not contain.
- Windows only for the desktop app. HTML export works in any modern browser.
- AWR, ASH and ADDM need the Oracle Diagnostics Pack, and SQL Tuning Advisor needs the Tuning Pack. Check your licence
  entitlement before running the suggested SQL; test every change outside production.

## Security and privacy model
- No network access: no database connection, no telemetry, no update check, no CDN, fonts or external scripts.
- Reports are treated as untrusted input: scripts are removed, the report is shown in a sandboxed frame under a strict
  Content Security Policy, navigation is blocked, and all report-derived text is escaped before display.
- Your reports and outputs stay on your machine. Exported files may contain sensitive names (hosts, schemas, SQL text):
  review before sharing. Never post unredacted reports in public issues. See [SECURITY.md](SECURITY.md) and
  [docs/privacy.md](docs/privacy.md).

## Release
The product website is https://apodofos.com (download links, documentation, privacy and legal notice). Download the installer or the portable zip from the [Releases page](https://github.com/hannawissam/apodofos-community-edition/releases) of the public repository. Each release
lists SHA-256 checksums. Verify your download before running it, in PowerShell:

```
Get-FileHash .\<downloaded-file> -Algorithm SHA256
```

The printed hash must match the one published for that file in the release notes.

## Architecture
See [docs/architecture.md](docs/architecture.md). In short: **Core** (parser, rules, chart specs, dashboard) is
UI-independent; one embedded offline web UI is shown by the **Desktop** WebView2 shell, by the **CLI** HTML export, or any
browser.

## License

**Apodofos Performance Report Analyzer, Community Edition** is free software, Copyright (c) 2026 Wissam HANNA, licensed under the
**GNU Affero General Public License, version 3 only** (SPDX: `AGPL-3.0-only`). See [LICENSE](LICENSE). It comes with
ABSOLUTELY NO WARRANTY. If you distribute it, or offer a modified version to users over a network, you must make the
corresponding source code available under the same licence (AGPL section 13). Source code:
https://github.com/hannawissam/apodofos-community-edition

* The name and logo "Apodofos" are not licensed under the AGPL: see [TRADEMARK.md](TRADEMARK.md). Forks must rename.
* Apodofos&trade; is a trademark of Wissam HANNA (French trade mark application no. 5322937, filed 2026-11-20, not yet registered) and the umbrella brand of a suite of database software; Apodofos Performance Report Analyzer is its first product.
* For now the project does **not** accept external code contributions by pull request unless the maintainer agreed beforehand in an issue. Bug reports, feature requests and documentation suggestions through issues are welcome. Any invited contribution requires the [CLA](CLA.md); see [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).
* Third-party components stay under their own licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), [NOTICE](NOTICE).
* **Apodofos Professional Edition** is a separate, private, proprietary product under its own licence; it is not part of this repository.

Apodofos is not an Oracle product and is not affiliated with, sponsored by or endorsed by Oracle. Oracle and all Oracle
product names are trademarks or registered trademarks of Oracle Corporation and/or its affiliates. The tool works
offline and does not transmit your reports. Reports may contain sensitive data (SQL text, host and schema names): review
before sharing. Recommendations are provided "as is"; test every change outside production. Some diagnostics (AWR, ASH,
ADDM, SQL Tuning Advisor) require Oracle Diagnostics/Tuning Pack licences.

## Disclaimer

* Recommendations and generated SQL are provided "as is". Review and test them outside production before any use in production.
* Apodofos is not professional advice and not Oracle advice.
* Some features and views referenced by the suggestions require Oracle Diagnostics Pack or Tuning Pack licences; check your own licence entitlement.
* Apodofos is not an Oracle product and is not affiliated with, sponsored by or endorsed by Oracle.
