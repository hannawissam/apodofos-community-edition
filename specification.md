# Database Performance Reports Analyzer - Specification (v2)

## Purpose
Offline analysis of Oracle AWR reports (Oracle Database 19c and above; older layouts tolerated) for DBAs and non-DBAs. No database or internet connection, ever.

## Functional requirements
1. **Import** AWR reports in HTML and text format (XML/active-HTML reports are detected and rejected with guidance).
2. **Side-by-side view**: original report on one side; analysis on the other (resizable).
3. **Findings in plain English** for non-DBAs (what / why it matters), with a technical view for DBAs; severity Critical/High/Medium/Low/Info/OK; evidence (metrics) on each finding.
4. **Graphs for all metrics**: curated charts per report section plus a generic explorer that can chart any table in the report.
5. **Recommendations and SQL** to resolve each issue (diagnostics, tuning tasks, DDL/parameter changes), with cautions and licence notes; combined script export.
6. **Documentation grounding**: each finding cites official Oracle 19c documentation (stored as citations; offline).
7. **Export**: standalone HTML dashboard, JSON, SQL script, print to PDF.
8. **Icon** reflecting purpose (report + metrics + magnifier/pulse).

## Non-functional requirements
- Windows desktop; .NET 11; self-contained single-file publish.
- Offline: no network calls (UI has no external resources; navigation outside the app is blocked).
- Safe handling of untrusted reports: scripts stripped, CSP applied, all text escaped.
- Large reports (≥ 1.5 MB HTML) analyze in seconds.

## AWR views covered
DBA_HIST_ACTIVE_SESS_HISTORY, DBA_HIST_SQLSTAT, DBA_HIST_WAIT_CLASS, DBA_HIST_SYSTEM_EVENT, DBA_HIST_SEG_STAT, and report sections built on DBA_HIST_SYS_TIME_MODEL, DBA_HIST_OSSTAT, DBA_HIST_SYSSTAT, DBA_HIST_FILESTATXS, DBA_HIST_PARAMETER, DBA_HIST_*_ADVICE, DBA_HIST_UNDOSTAT, DBA_HIST_LATCH, DBA_HIST_ENQUEUE_STAT, DBA_HIST_RESOURCE_LIMIT, DBA_ADDM_FINDINGS.

## Technology decision
Original plan: Windows Forms. Chosen: **WinForms host + WebView2 + embedded offline HTML/JS UI**.
Rationale: rich charts and a fluid UI are far easier in web tech, the report itself is HTML, and the same UI is exported as a standalone file. See `docs/architecture.md` for the comparison with Electron, WPF and Avalonia.
