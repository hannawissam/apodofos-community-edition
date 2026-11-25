# Apodofos Report Analyzer: release notes

This file is the single place for what the application does and what changed in each release. It is updated with **every** release (see "How this file is maintained" at the end), and each GitHub release body is taken from the matching section.

Apodofos™ is a trademark of Wissam HANNA (French trade mark application no. 5322937, filed 20 November 2026, not yet registered). Apodofos is not an Oracle product and is not affiliated with, sponsored by or endorsed by Oracle. Oracle and all Oracle product names are trademarks or registered trademarks of Oracle Corporation and/or its affiliates.

## All features (current version)

### Reports it reads
- **Oracle AWR** (HTML and text), **ASH** (HTML and text) and **Statspack** (text). The report type is detected from the content, and a "Not right?" menu lets you force another type.
- AWR layouts from 11g to 19c and later are parsed; the rules are calibrated for 19c. PDB reports are recognised so they do not produce false findings.
- **Number formats of any locale**: dot or comma decimals, thousands separators of every common style (comma, dot, space, apostrophe, Indian grouping), unit suffixes (K, M, G, ms, us ...), and reports that mix a decimal point and a decimal comma. The format that was detected is stated on the report.
- **Character sets**: UTF-8, UTF-16, a declared charset, or Windows-1252, so accents from French, German or Spanish databases are shown correctly.
- Missing sections never crash the analysis: they produce warnings, and checks that need them are skipped.
- Limitation: text ASH and 12c+ Statspack layouts are inferred from the documented structure and from synthetic fixtures, not validated against a broad set of real reports.

### What the analysis gives you
- **Plain-English findings** for non-DBAs (what is wrong, why it matters, what to do) with a **Technical** view for DBAs, evidence from the report, and a search box, severity filters, expand/collapse all and a root-cause filter.
- A **confidence level** and data-quality note on every finding, and related findings grouped by **root cause**.
- A **Fix first** plan: actions ranked by expected payoff, with impact, effort, risk and confidence.
- A **health score** (0 to 100), KPI tiles and a severity summary. The score is shown as "not reliable" instead of a grade when the data cannot support it (a very short window, a truncated file, misread numbers).
- A **rules engine** based on the Oracle performance method: DB time, wait events, CPU, parsing and cursor sharing, SQL, I/O, memory advisories, redo and undo, resource limits, parameters, ASH hot spots and the Statspack equivalents. Each rule has an evidence-based threshold, an official documentation reference and ready-to-run Oracle 19c SQL with cautions.
- **Data-quality checks**: impossible values (for example a share above 100%) are ignored and reported, DB CPU is accepted up to 150% of DB time because Oracle measures it separately, cross-checks between sections are made, and findings that rest on doubtful numbers are lowered in confidence.
- A **recommendations and SQL** tab: the top SQL statements with a filter, sorting, the full SQL text on click and copy buttons, paged 25 rows at a time.
- A combined **Script** tab: every suggested statement grouped by finding, with Copy script and Download .sql (the full script, whatever page is shown).
- **Charts** for wait events, load profile, time model, top SQL, I/O, memory, segments, contention and ASH activity, plus an "All metrics" explorer that charts any table of the report, with log scale and data tables.
- An **offline documentation catalog** (over 500 entries): findings link to the relevant Oracle Database 19c documentation. Opening a link is your choice; the application makes no request.

### The application
- **Desktop app** for Windows 10/11 (WinForms and WebView2): the original report with jump-to-section links beside the analysis, light and dark themes, a recent-files list, drag and drop, remembered window position, Save HTML, Print, an About box and the version in the title bar and the About box.
- **Command line** `apodofos-report <report> [--text] [--html out.html] [--json out.json] [--script out.sql]`, `--version`, `--license`, `--help`, with documented exit codes, for automation. The former command name `apodofos` still works (the installer and the portable zip include a small forwarding file).
- **Print and PDF report**: Print (and Ctrl+P) always produce one complete, formatted report, whichever tab is open: cover, overall health, key numbers (every tile), the Fix first plan, root causes, every finding with evidence, steps, SQL and documentation citations, all charts, top SQL and the report facts. **Save PDF** writes it as a PDF file (A4, page numbers) without going through a print dialog.
- **Exports**: a self-contained interactive HTML dashboard, JSON, a SQL script and the PDF report.
- **Installer and portable zip**: an MSI (per user only, no administrator rights needed, in-place upgrades, and an uninstall that removes everything it put on the machine, settings included) and a portable zip with the CLI in the `cli` folder, both self-contained (no .NET install). SHA-256 checksums are published with every release. The files are not code-signed yet, so Windows SmartScreen may warn.

### Privacy and security
- **No network access of any kind**: no database connection, no telemetry, no update check, no CDN, fonts or external scripts. Your reports and outputs stay on your computer.
- Reports are treated as untrusted input: scripts are removed, the report is shown in a sandboxed frame under a strict Content Security Policy, navigation is blocked and all report-derived text is escaped.
- Exported files can contain sensitive names (hosts, schemas, SQL text): review them before sharing.

### Licence
Free software under **AGPL-3.0-only**, no warranty. The name and logo are not covered by the licence (see TRADEMARK.md). A separate Professional Edition is planned (more Oracle analyses and more database engines); it is not available yet and nothing about its date, price or terms has been announced.

## Release history

**Version 1.0.7 is the first official public release of Apodofos Report Analyzer.** Versions 1.0.0 to 1.0.6 were test builds, published on 2026-11-19 and 2026-11-20 while the product was being finalised. They are kept for the record, are marked as pre-releases on GitHub, and should not be used: install 1.0.7 or later.

### 1.1.0 (2026-11-25), the product name everywhere
- **One consistent name: Apodofos Report Analyzer.** "Apodofos" is the brand; the application is "Apodofos Report Analyzer" in the window, the installer, the shortcuts, the command line and the documentation. The program file is now `ApodofosReportAnalyzer.exe` and the command line tool is `apodofos-report` (the old command `apodofos` keeps working through a small forwarding file, so existing scripts are not broken).
- **Everything the application owns now lives in its own product folder**: the program in `%LOCALAPPDATA%\Programs\Apodofos\ReportAnalyzer`, your settings and recent files in `%LOCALAPPDATA%\Apodofos\ReportAnalyzer`, and the installer key in `HKCU\Software\Apodofos\ReportAnalyzer`. Uninstalling removes only this product's folders and keys, never anything else stored under the Apodofos name.
- **Your settings move by themselves.** The first time 1.1.0 starts it moves your window position, recent reports and the WebView2 profile from the old folder (`%LOCALAPPDATA%\Apodofos`) into the new one. Installing 1.1.0 over 1.0.x is an in-place upgrade (same installer family); the old shortcuts are replaced. If you launch the program or the command line by its old file path in your own scripts or shortcuts, point them at the new file names. Going back to 1.0.x starts with an empty recent-reports list.
- **Release files have new names**: `Apodofos-ReportAnalyzer-Setup-<version>-win-x64.msi` and `Apodofos-ReportAnalyzer-Portable-<version>-win-x64.zip`, with SHA-256 checksums as before.
- **New: a complete formatted print and PDF report.** Printing no longer prints only the tab that happens to be open (for example, the key-number tiles were missing when Findings was shown). Print and Ctrl+P give one designed report with every section, and the new **Save PDF** button writes it to a file.
- **New: documentation links open in your default browser**, and every link has a small copy button. Only secure (https) links to oracle.com are opened. The application itself still makes no connection: your browser does, when you click.
- **More robust parsing of dates in other languages**: month names in French, German, Spanish, Italian, Portuguese and Dutch reports are recognised by the application itself, so the result no longer depends on the language data of the computer. A new guard test also catches any script error that would blank the page before a build is released.
- **Under the hood** (no change in what you see): the logic shared by every window type moved into a separate library with its own tests, the recent-reports list is stored in `recent.json`, and the source repository now builds and tests automatically on Windows and Linux.
- **Known limits:** the files are still not code-signed, so Windows SmartScreen may warn; the first start after the upgrade takes a moment longer while settings are moved.

### 1.0.7 (2026-11-20), first official public release
- **First official public release.** It includes everything from the test builds 1.0.1 to 1.0.6 listed below.
- **Installer re-issued on 2026-11-22.** The MSI was replaced by a per-user-only installer (no administrator rights) with the Apodofos artwork in the setup wizard. The application files are unchanged and the portable zip is untouched. If you downloaded the MSI earlier, its SHA-256 differs from the one now listed in SHA256SUMS.txt (9546f99af87db8cc34a8dc59c69dfc03d17197c96e15ee8c7680ada2ef12ed3c); both install the same application.
- **Uninstalling now cleans everything.** Removing Apodofos from Settings, Apps (or with `msiexec /x`) deletes the program folder, including any file left in it, the Start menu and desktop shortcuts, the PATH entry, the installer's registry key and your settings and WebView2 profile folder `%LOCALAPPDATA%\Apodofos` (window position, recent files, cache). Updating to a newer version keeps your settings; only an uninstall deletes them. Settings of other Windows users on a shared PC are not touched, and a portable copy is not affected.
- **More reliable installs and updates.** Updating a per-user install was tested end to end (an older version replaced by a newer one, then uninstalled) and leaves one registered copy.
- The installer is now per user only. If you installed an earlier version for all users and it is still listed in Settings, Apps, uninstall that entry once and install again.
- The source repository has an installer test script (`tools/test-installer.ps1`) that installs, upgrades and uninstalls the MSI per-user and checks that nothing is left.

### 1.0.6 (2026-11-20), test build
- **Fixed a huge arrow icon on the Summary tab.** In "Top priorities" the small arrow at the end of each row stretched to the full width of the row, which squeezed the finding title into a column one letter wide. The arrow is back to its normal 16 px size and the titles read normally.
- The browser regression script for the source repository (`tools/ui-qa`) now reports oversized icons, and a new test requires an explicit size for every icon.

### 1.0.5 (2026-11-20), test build
- **SQL and Script tabs are paged.** The SQL grid shows 25 rows at a time (10, 25, 50 or 100) with first/previous/next/last buttons, and the Script tab shows a few findings at a time (1, 3, 5 or 10). Their scrollbars and pagers now stay inside the visible window instead of far down the page. Copy script and Download .sql still give the whole script.
- **Layout and accessibility review of every tab** (11 defects fixed): the SQL ID column stays visible when scrolling the wide SQL table; duplicate element ids removed; severity badges, tab counters, SQL IDs and links meet AA contrast in the light theme; long unbroken words (SQL IDs, object names) no longer overflow the summary, findings or action plan; tooltips on shortened header titles; larger click targets; the findings toolbar no longer fills a short window; a transparent scrollbar corner in dark theme.
- **Accents are read correctly**: report files in Windows-1252 or another declared character set (common for French, German and Spanish databases), and UTF-16 files, no longer show "?" in place of accented letters.
- 14 new layout and contrast tests, 6 encoding tests and a browser regression script (`tools/ui-qa`) for the source repository.

### 1.0.4 (2026-11-20), test build
- The **version number** is now shown in the window title bar and in the About box.
- Includes everything in 1.0.3.

### 1.0.3 (2026-11-20), test build
- **DB CPU slightly above 100% of DB time** (for example 101.13% in real 19c reports, where DB CPU and DB time are measured separately) is no longer treated as an impossible value, so such a report no longer loses its health score. DB CPU is accepted up to 150% of DB time; every other share above 100%, and larger DB CPU values, are still flagged.
- **Reports that mix decimal styles** (some columns with a decimal point, others with a decimal comma) are read correctly. Reports in one style still reject malformed numbers.
- Sections of newer 19c AWR layouts are recognised and shown in the explorer (Modified Parameters, Database Resource Limits, Top 10 Channel Waits, Top Process Types).

### 1.0.2 (2026-11-20), test build
- The **trademark notice** (Apodofos™, French trade mark application no. 5322937, not yet registered) was added to the application footer, the About box and `--license`.

### 1.0.1 (2026-11-19), test build
- The product was renamed from QuietLoad to **Apodofos** ("light of performance"). The command line tool is `apodofos`, the desktop executable `Apodofos.exe`.

### 1.0.0 (test build)
- First version, published under the name QuietLoad: AWR, ASH and Statspack analysis, findings, Fix first plan, charts, SQL, script and exports, offline documentation catalog.

## How this file is maintained
- Every release adds a new section at the top of "Release history" **before** the release is built: the version, the date, and what changed for users (new features, fixes, behaviour changes, known limits). Keep entries short and factual, and do not mention anything that is not in the release.
- When a release adds or changes a capability, also update "All features (current version)" so that section always describes the current product.
- The GitHub release body is the matching section of this file, plus the downloads, SmartScreen and licence notes.
- Never put customer or employer report names, database names, host names or SQL from real reports in this file. Use synthetic samples only.
