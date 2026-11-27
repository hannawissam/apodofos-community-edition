# Agent team

The main agent acts as Chief Architect: it plans, splits work, owns contracts (`docs/ui-contract.md`, `docs/architecture.md`) and integrates. Specialists below work in parallel on **disjoint files**.

| Agent | Owns | Never touches |
|---|---|---|
| `oracle-perf-dba` | `Analysis/Rules`, `Knowledge/**`, doc catalog, chart choices, SQL | UI, shells, packaging |
| `core-host-developer` | `Core/{Parsing,Providers,Model,Export}`, `Apodofos.Host`, CLI, `Directory.Build.props` | rules content, Web UI, shell UIs |
| `frontend-expert` | `Core/Web/**` (html, css, js, charts) | C# code |
| `ux-ui-engineer` | `docs/ui-contract.md`, design tokens, UX copy, `tools/ui-qa` scenarios | Core, shells |
| `graphic-designer` | `assets/**`, icon, brand, social visuals, `MARKETING-local/**` | app logic |
| `fullstack-web-engineer` | local server HTTP and security layer | Core rules, Windows shell |
| `windows-developer` | `Apodofos.Desktop`, `installer/`, `publish.ps1`, `release.ps1` | shared Host logic |
| `linux-developer` | `Apodofos.Local`, Linux packaging, `.github/workflows` | Windows installer |
| `test-engineer` | `tests/**`, fixtures, UI QA runs, release verification | product code (reports bugs) |
| `code-reviewer` | nothing (read-only) | everything (reports findings) |
| `github-manager` | all git and GitHub operations | source files |

Platforms: Windows and Linux first, macOS at a later stage (one shared platform layer). When the Mac stage starts, add a `macos-developer` agent and a macOS CI job; keep shared projects free of anything that would block it.

## Parallel playbook

1. Chief Architect writes the plan and any contract change first (`docs/ui-contract.md`, `IHostPlatform` shape).
2. Launch independent agents in one step, each with its file ownership and acceptance criteria. Typical waves:
   - Portability wave: `core-host-developer` (globalization, Host extraction) + `test-engineer` (fix weak tests, CI-safe tests) + `linux-developer` (CI matrix).
   - Feature wave: `oracle-perf-dba` (rules) + `frontend-expert` and `ux-ui-engineer` (screens) + `test-engineer` (tests).
   - Release wave: `windows-developer` + `linux-developer` (artifacts) + `graphic-designer` (visuals) + `test-engineer` (verification).
3. `code-reviewer` reviews the combined diff; owners fix findings.
4. `github-manager` branches, commits, opens the PR, and merges on green build and tests.

Rules for everyone: offline and no external services, synthetic data only, SPDX headers on new files, never stage `EXAMPLES/`, `EXAMPLES-local/`, `MARKETING-local/`.
