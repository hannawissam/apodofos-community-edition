# UI QA harness (browser regression checks)

Dependency-free scripts that drive headless Edge/Chrome through the Chrome DevTools Protocol (Node 22+ with the
built-in `WebSocket`). They are **not** part of `dotnet test` (that suite never needs a browser); run them by hand
after changing anything under `src/Apodofos.Core/Web/`.

Nothing leaves the machine: the browser only opens local `file:///` dashboards.

## Prepare dashboards
Build, then export a standalone dashboard for each report you want to check (the CLI embeds the Web assets at
build time, so rebuild and regenerate after every UI change):

```
dotnet build Apodofos.slnx
dotnet run --no-build --project src/Apodofos.Cli -- "<report>" --html out/awr.html
```
Use at least one AWR, one ASH and one Statspack report (the synthetic ones in `tests/Apodofos.Tests/TestData`
are enough; real ones from `EXAMPLES/` and `EXAMPLES-local/` find more).

## Scripts
| Script | What it does |
|---|---|
| `node sweep.mjs <file-or-dir> [--vp 1100x700,1400x900,...] [--themes light,dark] [--scale 1.25] [--shots dir]` | Every dashboard x viewport x theme x tab (Summary, Findings expanded, Charts, All-metrics explorer, SQL with a row expanded, Script, Documentation). Runs `checks.js` and prints each distinct defect once. Goal: `No defects detected.` |
| `node flows.mjs <dashboard.html> <outDir>` | Scenarios: empty state (desktop and export), recent files, notice banner, "Not right?" menu, About dialog (Escape), keyboard (Tab order, focus rings, Enter/Space on rows), 5x reload with state-leak checks, hostile data (200-character words, 0 and 1 rows), print media. Prints PASS/FAIL, writes screenshots and a PDF. |
| `node charts.mjs <dashboard.html> <outDir> [WxH] [light\|dark]` | Renders every curated chart, hovers one point each, reports charts with no SVG, overflow, clipped text or a tooltip outside the window. |
| `node shots.mjs <dashboard.html> <outDir> [WxH] [light\|dark] [scale]` | One screenshot per tab plus the explorer and the About box. |

`checks.js` (injected into the page) reports: horizontal page overflow, grids/scripts whose scrollbar is cut off by
the panel, elements outside the viewport, clipped text without ellipsis or tooltip, overlapping controls, hit
targets under 24 px, controls without an accessible name, duplicate ids, empty containers and text contrast below
WCAG AA (computed from the resolved colours; code blocks and the gradient header are skipped).

## Notes
- Set `QA_BROWSER` to the browser executable if it is not at the default Edge path.
- The scripts switch themes through the real toggle button, so the report pane stays in sync like it does for a user.
- Zoom 125 % / 150 % of a 1100x700 window is `--vp 880x560` / `--vp 733x467` (CSS pixels). Below 1100 px the
  layout is best effort (the desktop app never goes below 1100x700); below 800 px the panes stack.
- The contrast of the design tokens is also pinned by `tests/Apodofos.Tests/UiLayoutTests.cs`, which needs no browser.
