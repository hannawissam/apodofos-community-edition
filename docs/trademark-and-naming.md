<!--
Product name: "QuietLoad Report Analyzer"; "QuietLoad" is the brand, kept out of the AGPL grant (see ../TRADEMARK.md). The name is NOT cleared in any official register; nothing below says "QuietLoad" is free to use. These notes have not been reviewed by a lawyer (see licensing.md, "Status of these documents").
-->

# Naming and trademarks: QuietLoad

## 0. Naming decision (recorded 2026-11-19)
* The product was previously named "PerfLens". On 2026-11-19 it was found to be in use by other software products
  (Microsoft Research PerfLens, an open-source perflens project, and an MCP server named perflens), so the name was dropped.
* "TuneGlass" and "Corvane" were considered and rejected because existing products already use them.
* **Chosen: "QuietLoad"** (CLI and executables: `quietload`, `QuietLoad.exe`; public GitHub repo `quietload-community-edition`).
  "Report Analyzer" is a generic descriptor and is not claimed as part of the mark.
* Informal screening of QuietLoad found: no hits on npm, NuGet or PyPI; no active websites on .com, .io, .app, .fr, .eu, .net;
  one unrelated personal GitHub account named `quietload`.
* **NOT cleared in the official registers (EUIPO, INPI, WIPO, USPTO).** Classes 9 and 42 must be searched
  before any filing or commercial launch.
* The two words ("quiet", "load") are ordinary, so the mark is descriptive/weak: expect a narrow scope of protection and
  plausible conflicts with similar names.

## 1. Assessment of "QuietLoad"
* **Positive:** engine-neutral, contains none of Oracle's, Microsoft's or PostgreSQL's names, so the product name does
  not imply endorsement by any vendor and works for the roadmap (ASH, Statspack, SQL Server, PostgreSQL).
* **Distinctiveness:** a compound of two ordinary English words ("quiet" + "load", hinting at a calm database workload).
  It is suggestive rather than purely descriptive, but weak. **Not verified in any register.**
* **Risks to check:** identical or confusingly similar existing marks, especially in Class 9 (downloadable software)
  and Class 42 (software as a service, software design/development, IT consulting); similar names
  ("Quiet Load", "Quiet-Load", "QuietLoad" with other spellings), existing GitHub/NuGet/Store projects with that name, and
  domains/social handles.
* Because the owner sells in the EU (and possibly worldwide), a conflict in any market where you sell can
  force a rebrand; clear before spending on logo, installer, signing certificates and marketing.

## 2. Clearance steps (do in this order, record dates and results)
1. **EUIPO TMview** (tmdn.org/tmview) and **eSearch plus**: search "QuietLoad" plus phonetic/visual variants
   ("Quiet Load", "Quiet-Load", "QuietLoad" misspellings) in Classes 9 and 42 (also check 35/41 if you plan training).
2. **WIPO Global Brand Database** (branddb.wipo.int): same terms, all offices including the Madrid system.
3. **USPTO** trademark search (tmsearch.uspto.gov) for the US; **UKIPO** and the owner's **national office** (e.g.
   INPI in France) for their registers.
4. **Domains and handles:** quietload.com / .io / .app / .eu / .fr; GitHub, LinkedIn, X, YouTube, npm/NuGet/Microsoft
   Store name availability; check app stores and software directories.
5. General web search for use in commerce, including unregistered (common-law) use.
6. Have a trademark attorney review the result (a professional "knock-out + full search" report is advisable).
7. **If clear:** consider filing an EU trade mark (EUIPO) in Classes 9 and 42 (and national if preferred); check
   current fees [verify, historically around EUR 850 for one class and about EUR 50 for the second]; priority
   period of 6 months lets you extend to other countries under the Paris Convention. Do not use the (R) symbol
   until registered; use TM before.
8. Keep records of first use date and evidence (screenshots, release notes) for unregistered rights.

## 3. Naming pattern for future engines
* **Preferred:** keep a single brand and describe the engine nominatively in the **tagline or sub-line**, not in
  the product name: "QuietLoad - performance report analyzer for Oracle Database" or "QuietLoad, now with PostgreSQL
  support". This has the least trademark risk.
* **Possible product-line names** ("QuietLoad for PostgreSQL", "QuietLoad for SQL Server", "QuietLoad for Oracle"):
  nominative use is generally allowed when (a) the third-party mark is needed to identify the product the software
  works with, (b) only as much of the mark is used as necessary (word mark, no logos, no stylisation, no vendor
  colours), (c) nothing suggests sponsorship or endorsement, and (d) a non-affiliation statement appears.
  **But** vendors' written policies differ and are sometimes stricter than the general rule:
  * **Oracle:** Oracle's published trademark/guidelines have, in the past, restricted using Oracle marks in product
    names without permission. **Treat "QuietLoad for Oracle" as NOT approved** until the current Oracle policy
    is read; use "QuietLoad ... analyzes Oracle AWR reports" descriptively instead.
  * **Microsoft:** has trademark and brand guidelines permitting factual "for [product]" statements under conditions;
    read the current Microsoft trademark and brand guidelines before using "SQL Server" in names or packaging [verify].
  * **PostgreSQL:** the PostgreSQL Community Association of Canada has a trademark policy that regulates "Postgres" and
    "PostgreSQL" in product names and the elephant logo; read it before use [verify].
* Each variant needs its own non-affiliation sentence (templates in `NOTICE`), its own clearance for combined names,
  and a decision whether it is a separate product requiring a separate (Pro) licence (see _parked/legal/EULA.pro-draft.md 2.2).

## 4. How to refer to Oracle features (AWR, ASH, ADDM, Statspack, Diagnostics Pack)
* **Never in the product name, logo, icon, installer title, executable branding, domain name or social handles**
  (avoid domains like "quietload", "oracle-awr-tool": risk of UDRP/domain disputes and confusion).
* **In descriptive text, yes:** "QuietLoad reads Oracle AWR (Automatic Workload Repository) reports in HTML or
  text form"; "supports ASH reports", "supports Statspack reports". Use the plain word marks, capitalised as Oracle
  does, no logos, no wording such as "official", "certified", "authorised", "compatible with Oracle-approved".
  Use the non-affiliation statement wherever Oracle names appear in marketing (website, store page, README, About,
  invoices if they mention Oracle).
* **Statspack** is an Oracle-provided tool name; treat it the same way.
* Use "Oracle" once with the attribution, not repeatedly in ad titles or keywords. Search-ad keyword bidding on
  trademarks is also rules-governed [verify per platform].
* Required statement (use verbatim): "QuietLoad is not an Oracle product and is not affiliated with, sponsored by or
  endorsed by Oracle. Oracle and all Oracle product names are trademarks or registered trademarks of Oracle
  Corporation and/or its affiliates."
* **Do not present Oracle documentation text or Oracle report text as your own.** Cite by link only; samples must be
  synthetic (see `docs/licensing.md`).

## 5. Owner's own marks
Use "QuietLoad" consistently with the TM symbol until registered; add a trademark line to the website footer
("QuietLoad is a trademark of Wissam HANNA" only if you intend to claim it); keep a brand-usage rule for resellers.
