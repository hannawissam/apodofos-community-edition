# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository. Read `AGENTS.md` first; this file adds the working agreements.

## What this is
Offline Oracle report analyzer (Community edition, AGPL-3.0-only) for **AWR, ASH and Statspack** reports (HTML or text). Shows the report side by side with a plain-English analysis, confidence, a "Fix first" plan, charts, recommendations, ready-to-run SQL and an offline documentation catalog. **Never connects to a database or the internet.**

## Build / test / run (requires .NET 11 SDK)
```
dotnet build Apodofos.slnx
dotnet test
dotnet run --project src/Apodofos.Cli -- "<report>" --text --html out.html   # also --json, --script, --version, --license
dotnet run --project src/Apodofos.Desktop
./publish.ps1            # self-contained single-file exe in artifacts/
dotnet run --project tools/IconGen -- assets   # regenerate the icon
```

## Layout
- `src/Apodofos.Core` — parsing (`Parsing/`: AWR, ASH, Statspack), providers (`Providers/`: one `IReportProvider` per report type, auto-discovered), model (`Model/`), rules (`Analysis/Rules/`), knowledge base and offline doc catalog (`Knowledge/`, `doc-catalog.json`), chart specs (`Analysis/ChartBuilder.cs`), dashboard assembly (`Export/`), embedded offline UI (`Web/`).
- `src/Apodofos.Desktop` — WinForms + WebView2 shell hosting the embedded UI.
- `src/Apodofos.Cli` — `apodofos` command line.
- `tests/Apodofos.Tests` — xunit; synthetic fixtures in `TestData/`; `LocalCorpusTests` use the git-ignored `EXAMPLES-local/` and skip when absent.
- `tools/` — IconGen, `spdx-headers.ps1`, `verify-doc-links.ps1` (network tool for the doc catalog).
- `docs/` — `ui-contract.md` (binding JSON/host contract), `architecture.md`.

## Working agreements
- Every rule: evidence-based threshold, `Plain` + `WhyItMatters` readable by a non-DBA, `Technical` for DBAs, recommendations, valid Oracle 19c SQL, and an official doc reference. Add a rule by creating an `IRule` class; it is auto-discovered.
- Oracle doc URLs must be verified (docs.oracle.com/en/database/oracle/oracle-database/19/) before being added to `Knowledge/OracleDocs.cs`.
- The UI has no external requests: no CDN, fonts or telemetry. Escape all report-derived text before inserting into HTML.
- Do not send report contents or project details to any external service.
- Keep parsing tolerant: missing sections must never throw; add warnings instead.
- Target framework is `net11.0` / `net11.0-windows` (user requirement).
- Git/GitHub work goes through the `github-manager` agent (`.claude/agents/github-manager.md`): topic branches, Conventional Commits with the Co-Authored-By trailer, PR + squash merge when build and tests are green; never stage `EXAMPLES/` or `EXAMPLES-local/`.
- New source files carry the SPDX header (`tools/spdx-headers.ps1`). Community code is AGPL-3.0-only. This repository (`hannawissam/apodofos-community-edition`) is the public Community Edition; everything needed to work on it is in this repository.
- Every release updates `RELEASE-NOTES.md` (new section at the top of "Release history" and, when a capability changed, "All features") before it is built; the GitHub release body comes from it. Real customer/employer reports are never used in screenshots, tests, docs or releases: synthetic samples only.
- Text ASH and 12c+ Statspack layouts are inferred from synthetic fixtures: do not claim real-world validation.

## Team roles used for parallel work
Chief Architect (the main agent: contracts, integration) plus the specialist agents in `.claude/agents/` (see `.claude/agents/README.md` for ownership and the parallel playbook): `oracle-perf-dba`, `core-host-developer`, `frontend-expert`, `ux-ui-engineer`, `graphic-designer`, `fullstack-web-engineer`, `windows-developer`, `linux-developer`, `test-engineer`, `code-reviewer`, `github-manager`. Each owns distinct files; cross-cutting changes go through `docs/ui-contract.md`. Windows and Linux first, macOS at a later stage.
