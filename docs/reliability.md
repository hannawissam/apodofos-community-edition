# Reliability: how much can you trust a result

Apodofos analyzes a report offline and cannot check it against the database. Everything below is about being honest when the input is
short, damaged, oddly formatted or simply too quiet to say anything.

## The data-quality model

Every analysis carries a report-level `dataQuality` (`Good | Fair | Poor`), a list of `notes`, a list of top-of-report `alerts`, and each
finding carries a `confidence` (`High | Medium | Low`) with a plain reason.

| Situation | Level | Effect |
|---|---|---|
| Window under 5 minutes | Poor | measured findings capped at Medium; health grade shown as "not reliable" (grade `n/a`) |
| Window 5-10 minutes | Fair | confidence Medium |
| Nearly idle database (AAS < 0.2 or DB time < 2 min) | Poor | confidence Low for share-based findings |
| Missing "End of Report" marker (AWR / ASH, HTML or text) | Poor | alert, health not reliable; (Statspack: a note only) |
| Header inconsistencies: begin/end span vs Elapsed, release that never existed (e.g. 12.3), end snapshot not after begin, implausible year | Fair (Poor with 2+) | warnings |
| Impossible values (a share above 100% in a table where that cannot be true) | Poor | the cells are blanked before any rule reads them, text that would print them is rewritten, findings resting on them are capped at Info, alert shown, health not reliable |
| Number format could not be determined | Poor | alert, no finding is rated worse than Low, health not reliable |
| Failed cross-checks (DB time vs time model, Load Profile vs AAS, wait-class sum of the *foreground* table, DB CPU vs CPU count) | Fair / Poor | confidence lowered on share-based findings |

ASH reports add: fewer than 100 samples (Poor), under 1000 (Fair); ASH severity is also capped when average active sessions are below the CPU
count and no latency evidence exists (ASH has no average wait times): below half the CPUs the cap is Medium, below the CPU count High.

The health score of an unreliable report is not a grade: the UI shows "NOT RELIABLE" and the CLI prints `Health: not reliable (...)`.
The "older than 19c" note is Info and is not counted in issue totals or the score.

## Number formats

Reports print numbers in the locale of the session that made them. The format is decided once per report, never per cell:

* votes from cells only one convention can produce: `1.942,31`, `0,25`, `1.234.567` (comma decimal) against `1,942.31`, `0.25`,
  `1,234,567`, Indian `12,34,567.8`, space / NBSP / narrow NBSP and apostrophe groups (`1 234,5`, `1’234.5`) with their decimal sign;
* weak votes from one or two decimals (the shape of percentages and rates) and a printed `NLS_NUMERIC_CHARACTERS` value;
* a cell such as `1,234` carries no vote: the report-level convention reads it. A malformed group (`1,2,3`, `12 3456`) is not a number.

Conflicting evidence, or a report whose only numbers are such ambiguous cells, is flagged ambiguous. The detected format is always stated
(`meta.numberFormat`, shown in the UI and the CLI). Elapsed/DB-time units in other languages (`Min.`, `Std.`, `heures`, `horas`) and month
names in French, German, Spanish, Italian and Portuguese, 12/24-hour clocks and `dd.MM.yyyy` dates are understood.

## Licence caveats

Any generated SQL or advice that touches `DBA_HIST_*`, `DBMS_WORKLOAD_REPOSITORY`, `DISPLAY_AWR`, ASH views, SQL Tuning Advisor, SQL Access
Advisor or SQL Monitor gets a uniform "Requires the Oracle Diagnostics Pack / Tuning Pack" caution, added centrally by
`LicenceCaveats.Apply`; a unit test scans every snippet of every fixture. Always check your own licence before running it.

## Known limits (honest list)

* **No RAC view.** A report covers one instance; cluster-wide problems can be incomplete.
* **Few real text AWR layouts were available.** The text parser is exercised by synthetic and a handful of real reports; unusual
  column wrapping can lose a table (reported as a warning, never an exception).
* **Locales.** Fixed-width text reports whose thousands separator is an ordinary or non-breaking space cannot be split into columns
  reliably (use the HTML report); other formats are covered by tests that re-render the same report in German, French, Swiss, Indian
  and Spanish layouts and require identical findings. Languages that Oracle does not support in AWR text itself (the report headings are English) are out of scope.
* **No ground truth.** Thresholds come from Oracle documentation and practice, not from measured outcomes on your system. "Impact" is an
  upper bound of the share of DB time involved, not a promised gain.
* **Heuristics.** Root-cause grouping is conservative (a statement joins a lock group only when it is wait-bound and the wait is at least
  5% of DB time); it can miss real links.
* **Impossible-value scan** covers tables where a share above 100% is provably wrong (top events, foreground wait classes, SQL elapsed/CPU,
  host CPU, ASH); other tables legitimately exceed 100% in real reports and are not checked.
