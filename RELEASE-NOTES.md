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
- An **offline documentation catalog** (over 500 entries): findings link to related Oracle documentation (mainly Oracle Database 19c). A numbered section, "Oracle documentation referenced by this analysis", lists each cited page once with the findings that cite it, in the dashboard, the saved HTML and the print and PDF report. These pages are for checking and further reading: not every threshold or recommendation comes from Oracle documentation. Opening a link is your choice: your default web browser opens it (internet access needed), and the application itself makes no request.

### The application
- **Desktop app** for Windows 10/11 (WinForms and WebView2): the original report with jump-to-section links beside the analysis, light and dark themes, a recent-files list, drag and drop, remembered window position, Save HTML, Print, an About box and the version in the title bar and the About box.
- **Command line** `apodofos-report <report> [--text] [--html out.html] [--json out.json] [--script out.sql]`, `--version`, `--license`, `--help`, with documented exit codes, for automation. The former command name `apodofos` still works (the installer and the portable zip include a small forwarding file).
- **Print and PDF report**: Print (and Ctrl+P) always produce one complete, formatted report, whichever tab is open: cover, overall health, key numbers (every tile), the Fix first plan, root causes, every finding with evidence, steps, SQL and numbered documentation citations, all charts, top SQL, the list of referenced Oracle documentation and the report facts, with the application version on the cover and in the page footer. **Save PDF** writes it as a PDF file (A4, page numbers) without going through a print dialog.
- **Exports**: a self-contained interactive HTML dashboard, JSON, a SQL script and the PDF report.
- **Installer and portable zip**: an MSI (per user only, no administrator rights needed, in-place upgrades, and an uninstall that removes everything it put on the machine, settings included) and a portable zip with the CLI in the `cli` folder, both self-contained (no .NET install). SHA-256 checksums are published with every release. The files are not code-signed yet, so Windows SmartScreen may warn.

### Privacy and security
- **No network connection from the application's own processes** (from 1.1.1): no database connection, no telemetry, no update check, no CDN, fonts or external scripts. The Microsoft WebView2 Runtime that draws the window is started with its background network features switched off. Documentation links open in your default web browser when you click them. Your reports and outputs stay on your computer. The 1.1.1 notes say how this was checked and what the limits are.
- Reports are treated as untrusted input: scripts are removed, the report is shown in a sandboxed frame under a strict Content Security Policy, navigation is blocked and all report-derived text is escaped.
- Exported files can contain sensitive names (hosts, schemas, SQL text): review them before sharing.

### Licence
Free software under **AGPL-3.0-only**, no warranty. The name and logo are not covered by the licence (see TRADEMARK.md). A separate Professional Edition is planned (more Oracle analyses and more database engines); it is not available yet and nothing about its date, price or terms has been announced.

## Release history

**Version 1.0.7 is the first official public release of Apodofos Report Analyzer.** Versions 1.0.0 to 1.0.6 were test builds, published on 2026-11-19 and 2026-11-20 while the product was being finalised. They are kept for the record, are marked as pre-releases on GitHub, and should not be used: install 1.0.7 or later.

### 1.1.1 (2026-11-26), privacy and safety fixes
**A privacy fix, and a correction of what we said before. Please update.**

We stated that 1.1.0 had no network access. For the desktop application that was not true. The application's window is drawn by the Microsoft Edge WebView2 Runtime, a Microsoft component, and the application started it with its default settings. With those settings the runtime contacted Microsoft services on its own. We saw this when we measured 1.1.0, built from its source code, on a test computer. The 1.0.x versions start the runtime in the same way; this is based on their source code only, they were not measured. The command line tool does not use this component.

**What was contacted by 1.1.0:**
- Microsoft's configuration service (`config.edge.skype.com`), when the application started with a new WebView2 profile.
- Microsoft's component update service (`edge.microsoft.com`), from about one minute after the start. Downloads from `msedge.b.tlu.dl.delivery.mp.microsoft.com`, over unencrypted HTTP, were also seen.
- A name look-up used to discover a proxy automatically (`wpad`).
- Once the application had been used before: encrypted connections to addresses in Microsoft 365's network, a few seconds after the start, opened by the runtime's Windows account sign-in component.

**What was sent, as far as it could be read:**
- To the configuration service: an identifier (it was the same in every new profile on the test computer), the Windows version and processor type, an installation date, the runtime's version and release channel, and a few flags.
- To the component update service: the Windows version, processor features, memory size, a flag saying whether the computer belongs to a domain, the versions of the runtime and of Microsoft's updater, random request identifiers and a list of runtime components.
- In these readable requests we found no report content, report file name, database name, host name, Windows user name, file path or cookie.
- **The sign-in connection could not be read** because it is encrypted outside the part of the runtime that can be logged, so we cannot say what it carried. We found no account details saved in the application's WebView2 profile.

**What 1.1.1 changes:**
- The application now starts the runtime with its configuration service, component updates, Windows account sign-in, use of a proxy and look-up of internet host names switched off. It also asks the runtime not to send crash reports to Microsoft and not to check addresses with Microsoft Defender SmartScreen (the application only shows its own built-in page).
- Documentation links still open in your default web browser when you click them, and your browser then connects to the Oracle documentation site.
- Reports, analysis, exports, printing and your settings work as before.
- The source repository has a new script, `tools/check-network.ps1`, that repeats the network measurement on a built application.

**New in 1.1.1:**
- **The Oracle documentation behind an analysis is listed in one place.** The saved HTML page, the desktop view (end of the Summary tab, also reachable from the Documentation tab) and the print and PDF report have a section "Oracle documentation referenced by this analysis": each related official Oracle documentation page once, numbered, with its link and the findings that cite it. Findings show the same numbers, and the links can be clicked in the PDF. These pages are for checking and further reading: not every threshold or recommendation comes from Oracle documentation. Opening a link needs internet access in your web browser; the application fetches nothing.
- **Printed and PDF reports always state the version.** Printing a saved HTML page left the version out of the cover and the closing note. The version of the analysis is now used there and in the About box of a saved page, and it also appears in the footer of every printed page.

**Also fixed in 1.1.1 (robustness and safety when reading report files):**
- A damaged HTML file with extremely deeply nested markup is now refused with a message instead of closing the program.
- Names read from a report are now checked and escaped before they are placed in the suggested SQL. A value that does not look like a valid name, SQL ID or number is replaced by a `&variable` for you to fill in.
- The security policy of the displayed and exported report is now always placed at the very start of the document, whatever the report's own markup looks like.
- A report with an absurdly large number in one cell now opens: that cell is treated as not a number.
- Damaged ASH reports with blank cells in the session, object or file sections no longer cause those checks to be skipped.
- Small corrections to the documentation and to the command line help.
- If another version of the program is still running, the application now asks you to close it instead of saying that the WebView2 Runtime is missing.

**Important installer fix: uninstalling no longer deletes a folder you chose.**
- If 1.1.0 or an earlier version was installed into a folder of your own (the `INSTALLFOLDER` option on the command line), **uninstalling that version deletes the whole folder, including your own files.** Do not uninstall it: upgrade to 1.1.1 first, which keeps your files.
- 1.1.1 removes only the files it installed, plus its own default folder. A normal installation made with the setup wizard uses the default folder.

**How it was checked:** on one Windows 11 computer with WebView2 Runtime 158, we watched the network connections of the application and of its runtime processes and read the runtime's network log. We used new profiles and previously used ones, including profiles written by the earlier configuration, with and without a synthetic report open, in runs of 8 to 25 minutes plus shorter functional checks. With the change, no network connection and no network request from these processes was observed. Version 1.1.0, started at the same time on a copy of the same used profile, did connect. A 1.1.1 test build also passed `tools/check-network.ps1` (a new profile, then the same profile again, 10 minutes each), and 1.1.0 failed the same script. Opening a report, Save PDF, Save HTML, the print preview and a documentation link were checked. These measurements were made on test builds of this change.

**Limits and known issues:**
- Tested on one Windows 11 (x64) computer only, for up to 25 minutes per run. Not tested: Windows 10, ARM64, a computer behind a proxy, Entra-joined or domain-joined computers, and a copy installed with the MSI.
- The setting that stops the sign-in connection is internal to Microsoft's runtime and is not documented by Microsoft. Windows updates the runtime automatically, and a later version could behave differently.
- An administrator policy or an environment variable on your computer can add settings to the runtime and undo this change.
- This concerns the application's own processes. Windows and other Microsoft programs (for example the updater of the runtime, certificate checks, SmartScreen checks of downloaded files and Windows diagnostic data) are separate and follow your Windows settings.
- Close any older version before you start 1.1.1. Two different versions cannot run at the same time with the same settings folder. If an older version is already running, 1.1.1 asks you to close it. If you start an older version while 1.1.1 is running, the older one reports, wrongly, that the WebView2 Runtime is missing.
- The files are still not code-signed, so Windows SmartScreen may warn.

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
