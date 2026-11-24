# AGENTS.md

## Project context
Apodofos Report Analyzer, Community Edition: an offline tool (Windows desktop app and CLI; Linux and macOS planned) that analyzes Oracle **AWR**,
**ASH** and **Statspack** reports (HTML or text). It reads report files only: it never connects to a database and never
uses the internet. Licence: AGPL-3.0-only, Copyright (c) 2026 Wissam HANNA. This repository (`hannawissam/apodofos-community-edition`)
is the public source of truth; everything needed to work on it is here.

## Commands
- Build: `dotnet build Apodofos.slnx` (needs the .NET 11 SDK; target `net11.0` / `net11.0-windows`).
- Test: `dotnet test` (xunit). `LocalCorpusTests` use the git-ignored `EXAMPLES-local/` and skip when it is absent.
- CLI: `dotnet run --project src/Apodofos.Cli -- <report> --text --html out.html`; also `--json`, `--script`, `--version`, `--license`.
- Desktop: `dotnet run --project src/Apodofos.Desktop`. Publish: `./publish.ps1` (artifacts/ is git-ignored).

## Architecture
- `src/Apodofos.Core/Parsing` - loaders and parsers for AWR (HTML/text), ASH (HTML/text) and Statspack (text) into a tolerant generic table model.
- `src/Apodofos.Core/Providers` - `IReportProvider` implementations (`OracleAwrProvider`, `OracleAshProvider`, `OracleStatspackProvider`), auto-discovered by `ReportProviderRegistry`, which detects the report type from content. One provider per report type; extension assemblies may `Register` more.
- `src/Apodofos.Core/Analysis` - `AnalysisEngine`, rules in `Rules/` (auto-discovered `IRule`), chart specs, action plan ("Fix first"), root-cause grouping, data-quality and confidence.
- `src/Apodofos.Core/Knowledge` - wait-event knowledge, SQL templates, and the offline documentation catalog (`doc-catalog.json` + `DocCatalog`, `DocLinker`): titles, URLs, anchors and own-words summaries only, no Oracle text.
- `src/Apodofos.Core/Export` and `Web/` - dashboard assembly and the embedded offline UI (no CDN, fonts or telemetry).
- `src/Apodofos.Desktop` (WinForms + WebView2), `src/Apodofos.Cli`, `tests/Apodofos.Tests`, `tools/` (IconGen, spdx-headers, verify-doc-links).
- Contracts: `docs/ui-contract.md`, `docs/architecture.md`, `docs/coverage-matrix.md`.

## Rules for changes
- Every rule: evidence-based threshold, `Plain` and `WhyItMatters` for non-DBAs, `Technical` for DBAs, recommendations, valid Oracle 19c SQL, a doc reference.
- Oracle doc URLs must be verified (`tools/verify-doc-links.ps1`, network tool, not part of tests) before being added to the catalog.
- Parsing stays tolerant: missing sections never throw; add warnings.
- Escape all report-derived text before inserting into HTML. No external requests from the UI.
- Do not send report contents or project details to any external service.
- New source files need the SPDX header (`tools/spdx-headers.ps1`).
- Text ASH and 12c+ Statspack layouts are inferred from synthetic fixtures; say so when claiming coverage.

## Test data
Committed fixtures are synthetic (`tests/Apodofos.Tests/TestData/`). `EXAMPLES/` and `EXAMPLES-local/` hold third-party material and must never be committed.
Antivirus-safe sources: never write recognisable attack payloads or system-tool paths as one contiguous literal in committed files or in shell commands (for example `cmd.exe` launch lines, `javascript:` URLs with a payload, protocol-handler exploits). Security tests assert on scheme, host or character class, so use harmless values or build the string from fragments (`"java" + "script:void(0)"`). Prefer the Write/Edit tools over inline heredoc scripts for code that contains such strings.

## Git and GitHub
All versioning goes through the `github-manager` agent (`.claude/agents/github-manager.md`): branch per topic, Conventional Commits with the Co-Authored-By trailer, PR with squash merge once build and tests are green.
