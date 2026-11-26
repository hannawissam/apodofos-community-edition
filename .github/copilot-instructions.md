# Copilot instructions

Offline Oracle report analyzer for **AWR, ASH and Statspack** reports (HTML or text): the report side by side with a plain-English analysis, confidence, a "Fix first" plan, charts, recommendations, ready-to-run SQL and an offline documentation catalog. **Never connects to a database or the internet.** Full project context is in `AGENTS.md` and `CLAUDE.md` (read both); the specialist agents are in `.github/agents/` (see `.github/agents/README.md`).

## Build, test, run (.NET 11 SDK; target `net11.0` / `net11.0-windows`)
- Build: `dotnet build Apodofos.slnx`
- Test: `dotnet test` (xunit). `LocalCorpusTests` use the git-ignored `EXAMPLES-local/` and skip when it is absent.
- CLI: `dotnet run --project src/Apodofos.Cli -- "<report>" --text --html out.html` (also `--json`, `--script`, `--version`, `--license`).
- Desktop: `dotnet run --project src/Apodofos.Desktop`. Publish: `./publish.ps1`.

## Layout
- `src/Apodofos.Core`: `Parsing/` (AWR, ASH, Statspack), `Providers/` (one auto-discovered `IReportProvider` per report type), `Model/`, `Analysis/Rules/` (auto-discovered `IRule`), `Knowledge/` (wait events, SQL templates, `doc-catalog.json`), `Export/` and `Web/` (embedded offline UI).
- `src/Apodofos.Desktop` (WinForms + WebView2), `src/Apodofos.Cli`, `tests/Apodofos.Tests`, `tools/`, `docs/` (`ui-contract.md` is the binding UI/host contract; `architecture.md`).

## Rules
- Every rule: evidence-based threshold, `Plain` and `WhyItMatters` readable by a non-DBA, `Technical` for DBAs, recommendations, valid Oracle 19c SQL, an official doc reference. Doc URLs are verified (`tools/verify-doc-links.ps1`) before they enter the catalog.
- Parsing stays tolerant: missing sections never throw; add warnings.
- Escape all report-derived text before inserting it into HTML. The UI makes no external requests (no CDN, fonts or telemetry). Never send report contents or project details to any external service.
- New source files carry the SPDX header (`tools/spdx-headers.ps1`).
- Test data is synthetic (`tests/Apodofos.Tests/TestData/`). Never commit or stage `EXAMPLES/`, `EXAMPLES-local/`, `_parked/`, `MARKETING-local/`. Never use real customer or employer reports anywhere.
- Text ASH and 12c+ Statspack layouts are inferred from synthetic fixtures: do not claim real-world validation.
- Antivirus-safe sources: never write recognisable attack payloads or system-tool paths as one contiguous literal; build such strings from fragments (`"java" + "script:void(0)"`).
- Every release updates `RELEASE-NOTES.md` before it is built.

## Git and GitHub
Use the `github-manager` agent for all versioning: topic branch, Conventional Commits with the Co-authored-by trailer, PR with squash merge once build and tests are green. Check `.github/PULL_REQUEST_TEMPLATE.md` before writing a PR body.

## Claude Code compatibility
`.claude/agents/*.md`, `CLAUDE.md` and `AGENTS.md` remain in place. `.github/agents/*.agent.md` is generated from `.claude/agents` by `pwsh tools/sync-copilot-agents.ps1`: edit the Claude source, never the generated copy.
