# Architecture

```
 AWR report (.html/.txt)
        │
  Parsing (AwrReportLoader → AwrHtmlParser / AwrTextParser)  → AwrReport (generic tables + header info + sanitised HTML)
        │
  Analysis (AnalysisEngine → IRule* → Findings, KPIs, health, ChartBuilder, SQL summaries) → AnalysisResult
        │
  Export (DashboardBuilder) → one self-contained HTML (UI + JSON + report)
        │
  Host logic (Apodofos.Host: session, message router, recent files; shell-neutral)
        │
  Shells: Desktop (WinForms + WebView2)   CLI (apodofos)   Any browser (exported file)   [Linux/browser shell: planned]
```

Key decisions
- **Generic table model**: every `<table>` is kept (header + rows); rules address columns by normalized name, so Oracle layout differences (11g/12c/19c) don't break them and every metric can be charted.
- **Rules auto-discovered** (`IRule`); knowledge base (`Knowledge/`) holds plain-English text, doc citations and SQL templates.
- **Thin shells**: everything that must behave identically on every platform lives in `Apodofos.Host` behind `IHostPlatform` (see `docs/ui-contract.md`); a shell is only window, dialogs, web view and placement.
- **One UI** (`Core/Web`): no frameworks, no network; reused by desktop and export.
- **Security**: reports are untrusted input — scripts removed, CSP `default-src 'none'`, sandboxed iframe, escaped output, blocked navigation. HTML nested more than 500 levels deep is refused (the document is walked recursively). Every report value placed in a generated SQL statement goes through `Analysis/SqlSafe` (literals escaped; identifiers, SQL_IDs and numbers checked, else a `&variable`).

## UI technology options (considered)
| Option | Pros | Cons | Verdict |
|---|---|---|---|
| WinForms + WebView2 (chosen) | Tiny, native Windows, Evergreen runtime on Win 11, reuse of exported HTML UI, simple C# interop | Needs WebView2 runtime on Win 10 | **Chosen** |
| Electron | Cross-platform, mature | 150 MB+ per app, second runtime (Node) to maintain, parsing would be rewritten in JS or a sidecar | Good if macOS/Linux are required |
| Tauri | Small, Rust + WebView | Another toolchain (Rust), parsing rewrite | Alternative |
| Avalonia | Cross-platform, pure .NET | Charts and report HTML rendering need extra components | Alternative for native look |
| WPF | Native, XAML | HTML report display still needs WebView2; charts need libraries | No advantage |

Because Core is UI-independent and the UI is plain web assets, an Electron/Tauri shell can be added later by loading the same `index.html` and calling the CLI/Core as a sidecar.
