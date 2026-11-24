# UI contract (owned by the Chief Architect)

One offline web UI (HTML/CSS/JS, **no CDN, no network, no external fonts**) is embedded in `Apodofos.Core`
and rendered by every front-end: WebView2 desktop shell, CLI `--html` export. Source files live in
`src/Apodofos.Core/Web/` and are assembled by `DashboardBuilder.Build`.

## Files and placeholders
| File | Role |
|---|---|
| `index.html` | Page skeleton. Must contain the placeholders `<!--AWR:CSS-->` (in head), `<!--AWR:ICON-->` (inline logo SVG, optional), `<!--AWR:DATA-->` and `<!--AWR:SCRIPTS-->` (end of body). |
| `app.css` | Design system: CSS variables, light + dark (`prefers-color-scheme` and `[data-theme]`), responsive. |
| `charts.js` | Dependency-free SVG chart library exposing `window.AwrCharts.render(el, spec, opts)` for `hbar`, `bar`, `donut`, `line`, `stacked`. |
| `app.js` | App logic. Reads `window.__AWR__ = { analysis, reportHtml, mode }`. Exposes `window.AwrApp.load(payload)` so a host can inject a new analysis without reloading. |
| `icon.svg` | Logo (copy of `assets/apodofos.svg`). |

## Payload (camelCase JSON, see `Analysis/AnalysisResult.cs`, `Analysis/Finding.cs`)
`analysis` = `{ meta, health{score,grade,headline,paragraphs[],counts{}}, kpis[{key,label,value,unit,status,hint}], findings[], charts[], tables[], topSql[], warnings[] }`
- `findings[]`: `id, severity (Critical|High|Medium|Low|Info|Ok), category, title, plain, whyItMatters, technical, evidence[{label,value,note}], recommendations[], sql[{title,code,caution}], docs[{title,url}], sourceViews[], tableRefs[], chartRefs[], impact`
- `charts[]`: `id, group, title, subtitle, type (hbar|bar|donut|line|stacked), labels[], series[{name,values[]}], unit, explain, tableRef, logScale, reference, referenceLabel, xLabel, yLabel`
- `tables[]`: `id, title, section, summary, headers[], rows[][]` — every table of the report; ids match the `id` attributes inside `reportHtml`.
- `reportHtml`: sanitised original report (scripts removed, CSP set). Rendered in a sandboxed `<iframe srcdoc>` (`sandbox=""` + `allow-same-origin` is NOT allowed; use `sandbox="allow-same-origin"` only if scrolling by id needs it and no scripts run). To jump to a table: `iframe.contentDocument.getElementById(tableId).scrollIntoView()` and toggle class `awr-highlight`.
- `mode`: `"desktop"` or `"export"`. In export mode hide the Open / Save buttons.

### Trust and prioritisation fields (additive; the UI may ignore them until it renders them)
- `findings[].confidence`: `High | Medium | Low`; `findings[].dataQuality`: why confidence is below High (empty when High, e.g. "Very short window (3.2 min)").
- `findings[].rootCauseGroup` (group title, empty when the finding stands alone) and `findings[].relatedIds[]` (ids of findings with the same cause).
- `analysis.dataQuality`: `{ level: Good|Fair|Poor, notes[], alerts[], healthNote }`: report-level notes (short window, idle database, few ASH samples, missing core sections, partial report, failed cross-checks, RAC/PDB scope).
- `analysis.rootCauses[]`: `{ id, title, summary, findingIds[] }`, most severe first.
- `analysis.actionPlan[]` ("Fix first", at most 5, ranked): `{ rank, action, why, expectedImpactPct (0-100), expectedImpact (text), effort Low|Medium|High, risk Low|Medium|High, confidence, findingIds[] }`. `expectedImpactPct` is the share of DB time the findings involve (an upper bound, not a promise).
- `analysis.health.reliable` (bool, default true) and `health.reliabilityNote`: when false (window under 5 minutes, truncated report, ambiguous number format, impossible values) `health.grade` is `"n/a"` and the UI shows "NOT RELIABLE" instead of a grade; the CLI prints "Health: not reliable (...)".
- `analysis.dataQuality.alerts[]`: top-of-report warnings (truncated report, impossible values, "number format could not be determined", unreliable health). The Summary tab renders them in a red banner above the data-quality notes. `dataQuality.healthNote` repeats the reliability reason.
- `analysis.meta.numberFormat`: the detected number format, e.g. "Numbers read as 1.234,56 (comma decimal, dot thousands)." (shown under "About this report"; the CLI prints it under the header).
- `rootCauses[].leadFindingId` (the symptom that defines the cause) and `rootCauses[].impactPct` (share of DB time of the causal event, never of a member statement).
- Inconsistent headline numbers add one finding titled "Some numbers in this report do not agree with each other" (category `Report quality`, severity `Info`, confidence `Low`).
- Suggested rendering: a confidence badge next to the severity (tooltip = `dataQuality`), a data-quality banner from `analysis.dataQuality`, a "Fix first" card list on the overview, and "Related findings" links from `relatedIds`.
- Implemented in the UI: the Summary tab order is health card, data-quality banner (only when not Good), "Key numbers" (KPI tiles), "Fix first", then the narrative and top priorities (print follows the same order). It shows a "Fix first" list (rank, action, impact/effort/risk tags, confidence badge; click opens the first related finding) and a data-quality banner only when `analysis.dataQuality.level` is not `Good`. Finding cards show a confidence badge (tooltip = `dataQuality`), the data-quality note, the root-cause group label, and "Related findings" chips from `relatedIds`; the Findings toolbar has a "Root cause" filter. All fields are optional (older payloads render as before). Print includes the plan and banner.

### Offline documentation catalog (`analysis.docs[]`, `analysis.sectionDocs{}`)
Source: `src/Apodofos.Core/Knowledge/doc-catalog.json` (embedded as `Apodofos.Docs.doc-catalog.json`; API `DocCatalog`, `DocLinker`). It stores only facts (titles, URLs, anchors, keywords) and short own-words summaries; no Oracle text. Links are never fetched: every link can be opened in the machine's default browser (`{type:'openUrl', url}` to the host) and copied ("Copy link"); the app itself never connects.
- `analysis.docs[]`: every catalog entry applicable to the report kind (AWR, ASH or Statspack), about 400-550 items. Each item:
  `{ id, title, url, book, tags[], summary, kinds[], releases[], section?, local? }`
  - `id`: stable string, e.g. `wait.log_file_sync`, `view.dba_hist_sqlstat`, `param.cursor_sharing`, `tgdba.ash.activity`, `statspack.spdoc`.
  - `title`: official page or section title; `book`: e.g. "Performance Tuning Guide"; `url`: full URL including `#anchor` when the entry points to a section (https://docs.oracle.com/...).
  - `tags[]`: topic tags (`wait-event`, `view`, `parameter`, `package`, `statistic`, `awr`, `ash`, `statspack`, `sql`, `io`, `redo`, `memory`, `rac`, `licensing`, `report-generation`, ...) for filter chips.
  - `summary` (max 240 chars, plain English), `kinds[]` subset of `awr|ash|statspack`, `releases[]` (e.g. `["19c"]`; legacy entries `["9.2"]`, `["12.1"]`).
  - `section`: where to look on the page (optional). `local`: present only for an offline-only reference (Statspack `spdoc.txt`): the file path on the database host; `url` is then a `file:///` URL that must NOT be opened as a link, show `local` as plain text instead.
  - Suggested **Documentation** tab: search box (matches title, summary, tags), tag chips, book grouping, each row with "Copy link". Keywords are intentionally not in the payload.
- `analysis.sectionDocs{}`: dictionary `tableId -> docId[]` (at most 4, best first) for each report table whose title matches a documented report section (e.g. "Load Profile", "Top 10 Foreground Events", "Time Model Statistics", "SQL ordered by Elapsed Time", "Activity Over Time", "Top User Events", "Instance Efficiency Percentages", "Statspack snapshot level"). Tables without a match are absent. Use it to render a "Learn more" link beside the table in the report pane or table list; resolve ids through `analysis.docs[]` (`id` lookup). Every id in `sectionDocs` exists in `docs`.
- `findings[].docs[{title,url}]` now also contains catalog entries added by `DocLinker` (max about 6 per finding, existing rule-supplied docs first, de-duplicated by URL). A `file:///` url (Statspack spdoc.txt) is an offline reference, not a clickable link. Their `url` equals the `url` of a matching `analysis.docs[]` item, so the UI can show the summary on hover by URL lookup.
- Verification: `tools/verify-doc-links.ps1` re-checks every URL and anchor (network; run manually). `verifiedOn` (in the JSON file only) records the last check date.

## Required screens (all in one page, side-by-side layout)
Left pane: original report (iframe), with a section jump list. Right pane (resizable splitter, collapsible): tabs
**Summary** (health score ring, KPI tiles, plain-English narrative), **Findings** (severity filter, search, expandable cards: plain / why it matters / technical toggle, evidence, numbered recommendations, SQL blocks with *Copy* button and caution notes, doc citations, "show in report" and "show chart" links), **Charts** (curated charts grouped by `group`, plus an "All metrics" explorer that auto-charts any table from `tables[]` by choosing label column + numeric column(s)), **SQL** (merged top-SQL grid, sortable, full text expandable), **Script** (all SQL of all findings in one copy/downloadable script, grouped by finding), **Documentation** (searchable `analysis.docs[]`: text search, book select, tag chips, summary, "Copy link"; entries with `local` show the path as plain text, never a link. Order: first a **Relevant to this report** group (docs linked from `findings[].docs` matched by URL, most severe finding first, then `analysis.sectionDocs` for tables present in the report), then the remaining entries grouped by book with "Performance Tuning Guide" and "Database Reference" first and other books alphabetically; search, book and tag filters apply to both parts). A "Learn more" line next to the jump list shows `analysis.sectionDocs[tableId]` for the table last jumped to. Links: export mode opens `https://` links in a new tab; desktop mode sends `openUrl` to the host (default browser, no copy needed) and falls back to copying the link when no host takes the message. Links in the "Learn more" line and in finding cards carry a small "Copy link" button; Documentation rows have a "Copy link" button. The summary shows a report-type badge (`meta.reportType`: AWR, ASH, Statspack) and labels adapt (ASH: "active session time" instead of "DB time", including `actionPlan[].expectedImpact`). An **About** dialog (topbar button and footer link) carries copyright, AGPL-3.0-only, no warranty, Oracle non-affiliation, the public source URL (https://github.com/hannawissam/apodofos-community-edition) and the AGPL section 13 network notice.
Non-DBA audience: default to plain English; technical details one click away. Empty state: big drop zone + "Open AWR report" button.

## Host messaging (desktop mode only)
The page talks to its shell only through `window.AwrHost.send(msg)` (defined in `app.js`; returns `true` when a shell took the message). Transport detection: a shell may install `window.AwrHost = { post(msg) {...} }` before the page starts; otherwise WebView2 (`window.chrome.webview.postMessage`) is used. With no transport (export mode, plain browser) `send` returns `false` and the page falls back to Blob downloads, `navigator.clipboard` and `window.print()`.

UI to host (`{ type, ... }`, handled by `Apodofos.Host.HostMessageRouter`; unknown or malformed messages are ignored):
| type | fields | effect |
|---|---|---|
| `open` | | file dialog, then load |
| `openPath` | `path` | load a file (recent list) |
| `dropFile` | | file dropped on the page; the shell attaches the native path (WebView2: `postMessageWithAdditionalObjects`) |
| `reanalyze` | `provider` | re-read the open file with another provider ("Not right?") |
| `exportHtml` | | save the dashboard as a standalone HTML file |
| `exportScript` | `text` | save the SQL script |
| `copy` | `text` | system clipboard |
| `print` | | system print dialog |
| `openUrl` | `url` | open a documentation link in the default browser. The host accepts only `https` URLs on `oracle.com` (and subdomains), without credentials; anything else is refused with a warning (`HostSession.IsAllowedDocUrl`) |
| `removeRecent` | `path` | |
| `clearRecent` | | |

Host to UI (run in the page by `IHostPlatform.RunScriptAsync`): `window.AwrApp.load({ analysis, reportHtml, mode })`, `setSource({ name, provider, forced })`, `setRecent([{ path, name, type, opened, exists }])`, `notify({ kind: 'error'|'warning', message })`, `setAppVersion("1.0.4")` (shown in the About box; the host also puts the version in the window title).
In export mode use `navigator.clipboard` with a `document.execCommand('copy')` fallback, and `Blob` downloads.

### Shared host library (`src/Apodofos.Host`, `net11.0`, no UI dependency)
- `IHostPlatform`: what a shell provides: `IsClosed`, `DataDirectory`, `PickOpenFileAsync`, `PickSaveFileAsync` (neutral `FileFilter` lists, return null when cancelled), `SetClipboardAsync`, `PrintAsync`, `OpenUrlAsync`, `ShowMessageAsync`, `SetWindowTitle`, `RunScriptAsync`, `SetBusy`.
- `HostSession`: the single load pipeline (button, drag and drop, command line, recent, re-analyze; parsing runs off the UI thread), busy and dialog re-entrancy guards, pending file before the page is ready, exports, window title, host-to-UI pushes. Call it from the shell's UI thread.
- `HostMessageRouter`: parses the messages above and calls the session; a failing action is shown with `ShowMessageAsync` (never thrown into the shell).
- `RecentStore`: paths only, newest first, at most 5, persisted as `recent.json` in the data directory. The list older versions kept inside `window.json` is imported once.
- A shell owns only: the window, dialogs, the web view, drag-and-drop plumbing, key bindings (F5 / Ctrl+O), window placement (`window.json`, Windows only today) and its `IHostPlatform` implementation.

## Quality bar
Accessible (keyboard, aria, contrast AA), prints cleanly (print CSS shows summary + findings), works at 1100 px minimum width, no external requests, total inlined size < 1.5 MB excluding report.

- Layout rules (pinned by `tests/Apodofos.Tests/UiLayoutTests.cs`, browser checks in `tools/ui-qa/`): the SQL and Script tabs are "fitted" (`.panel.fit`): the grid or script fills the space left in the panel and is paged, so its scrollbars and the pager are always on screen; the panel scrolls only on very short windows. Long unbroken words (SQL IDs, object names) wrap or ellipsise with a tooltip, never widen a panel. Text and tint colours keep WCAG AA contrast in both themes.

## Host-injected behaviour (desktop shell)
- Drag and drop: the shell injects a capture-phase `drop` listener (`AddScriptToExecuteOnDocumentCreated`) that stops the page's own drop handling and posts `{ type: 'dropFile' }` with the dropped `File` via `chrome.webview.postMessageWithAdditionalObjects`; the host reads the native path and loads it. The page's own `drop` handler therefore never fires in the desktop app (it remains the fallback for export mode, where it only toasts).
- The host owns the window title (`<file name> — Apodofos Report Analyzer <version>`), F5 / Ctrl+O (open dialog), window size/position (`%LOCALAPPDATA%\Apodofos\window.json`, minimum 1100x700) and blocks all navigation except the in-memory page.
