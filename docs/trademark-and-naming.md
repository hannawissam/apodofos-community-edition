<!--
Product name: "Apodofos Report Analyzer"; "Apodofos" is the brand, kept out of the AGPL grant (see ../TRADEMARK.md). The name is NOT registered anywhere: screening found no conflicts, but nothing below says "Apodofos" is free to use in every jurisdiction. These notes have not been reviewed by a lawyer (see licensing.md, "Status of these documents").
-->

# Naming and trademarks: Apodofos

## 0. Naming decision (recorded 2026-11-19)
History of rejected names:
* "PerfLens" was rejected on 2026-11-19: it is already used by other software products (Microsoft Research PerfLens,
  an open-source perflens project, and an MCP server named perflens).
* "QuietLoad" was rejected on 2026-11-19 because quietload.com was already taken.
* "TuneGlass", "Corvane", "WaitWise", "LumiTune" and similar candidates were rejected because existing products or
  businesses already use them.

**Chosen: "Apodofos"** (Greek: *apodosi*, "performance", + *fos*, "light"). Product name "Apodofos Report Analyzer";
CLI `apodofos`, desktop executable `Apodofos.exe`; editions "Apodofos Community Edition" (public, AGPL-3.0-only,
GitHub `apodofos-community-edition`) and "Apodofos Professional Edition" (private).
"Report Analyzer" is a generic descriptor and is not claimed as part of the mark.

Etymology: Greek *apodosi* (Απόδοση, performance) + *fos* (Φως, light) = "light of performance"; pronounced a-PO-do-fos.

Screening done so far (2026-11-19):
* Domains: apodofos is free on .com, .net, .org, .io, .fr, .eu, .dev, .app, .co and .tech (not yet bought).
* npm, NuGet, PyPI and GitHub: no conflicting names.
* Web searches and trademark-aggregator searches: no conflicts found.
* The owner searched INPI and TMview on 2026-11-19 and found no matching marks.

Still advisable:
* File the mark at INPI (France) in classes 9 (downloadable software) and 42 (software as a service, software
  design and development), and consider an EU mark at EUIPO.
* Buy the domains (at least .com, .fr and .eu) and reserve the social and package handles.
* The word is invented (coined from two Greek roots), so it should be distinctive; this is not a legal conclusion.

## 1. Assessment of "Apodofos"
* **Positive:** engine-neutral, contains none of Oracle's, Microsoft's or PostgreSQL's names, so the product name does
  not imply endorsement by any vendor and works for the roadmap (ASH, Statspack, SQL Server, PostgreSQL).
* **Distinctiveness:** a coined compound of Greek roots; a Greek speaker will read it as "performance light", which is
  suggestive, but outside Greece it is essentially an invented word. **Not yet verified in an official register by a
  professional.**
* **Risks to check:** phonetic or visual variants and similar Greek-derived names in Classes 9 and 42, existing
  GitHub/NuGet/Store projects with a similar name, and domains/social handles.
* Because the owner sells in the EU (and possibly worldwide), a conflict in any market where you sell can
  force a rebrand; clear before spending on logo, installer, signing certificates and marketing.

## 2. Clearance steps (do in this order, record dates and results)
1. **EUIPO TMview** (tmdn.org/tmview) and **eSearch plus**: search "Apodofos" plus phonetic/visual variants
   (for example "Apodosfos", "Apodophos") in Classes 9 and 42 (also check 35/41 if you plan training). Done by the owner for
   the exact word on 2026-11-19 (INPI and TMview): no hits.
2. **WIPO Global Brand Database** (branddb.wipo.int): same terms, all offices including the Madrid system.
3. **USPTO** trademark search (tmsearch.uspto.gov) for the US; **UKIPO** and the owner's **national office** (INPI in
   France, done for the exact word) for their registers.
4. **Domains and handles:** apodofos.com / .net / .org / .io / .fr / .eu / .dev / .app (free as of 2026-11-19, not yet
   bought); GitHub, LinkedIn, X, YouTube, npm/NuGet/Microsoft Store name availability; check app stores and software
   directories.
5. General web search for use in commerce, including unregistered (common-law) use.
6. Have a trademark attorney review the result (a professional "knock-out + full search" report is advisable).
7. **If clear:** file at INPI in Classes 9 and 42 and/or an EU trade mark (EUIPO); check current fees [verify,
   historically around EUR 850 for one class and about EUR 50 for the second at EUIPO]; the 6-month priority
   period lets you extend to other countries under the Paris Convention. Do not use the (R) symbol
   until registered; use TM before.
8. Keep records of first use date and evidence (screenshots, release notes) for unregistered rights.
## 3. Naming pattern for future engines
* **Preferred:** keep a single brand and describe the engine nominatively in the **tagline or sub-line**, not in
  the product name: "Apodofos - performance report analyzer for Oracle Database" or "Apodofos, now with PostgreSQL
  support". This has the least trademark risk.
* **Possible product-line names** ("Apodofos for PostgreSQL", "Apodofos for SQL Server", "Apodofos for Oracle"):
  nominative use is generally allowed when (a) the third-party mark is needed to identify the product the software
  works with, (b) only as much of the mark is used as necessary (word mark, no logos, no stylisation, no vendor
  colours), (c) nothing suggests sponsorship or endorsement, and (d) a non-affiliation statement appears.
  **But** vendors' written policies differ and are sometimes stricter than the general rule:
  * **Oracle:** Oracle's published trademark/guidelines have, in the past, restricted using Oracle marks in product
    names without permission. **Treat "Apodofos for Oracle" as NOT approved** until the current Oracle policy
    is read; use "Apodofos ... analyzes Oracle AWR reports" descriptively instead.
  * **Microsoft:** has trademark and brand guidelines permitting factual "for [product]" statements under conditions;
    read the current Microsoft trademark and brand guidelines before using "SQL Server" in names or packaging [verify].
  * **PostgreSQL:** the PostgreSQL Community Association of Canada has a trademark policy that regulates "Postgres" and
    "PostgreSQL" in product names and the elephant logo; read it before use [verify].
* Each variant needs its own non-affiliation sentence (templates in `NOTICE`), its own clearance for combined names,
  and a decision whether it is a separate product requiring a separate (Pro) licence (see _parked/legal/EULA.pro-draft.md 2.2).

## 4. How to refer to Oracle features (AWR, ASH, ADDM, Statspack, Diagnostics Pack)
* **Never in the product name, logo, icon, installer title, executable branding, domain name or social handles**
  (avoid domains like "apodofos", "oracle-awr-tool": risk of UDRP/domain disputes and confusion).
* **In descriptive text, yes:** "Apodofos reads Oracle AWR (Automatic Workload Repository) reports in HTML or
  text form"; "supports ASH reports", "supports Statspack reports". Use the plain word marks, capitalised as Oracle
  does, no logos, no wording such as "official", "certified", "authorised", "compatible with Oracle-approved".
  Use the non-affiliation statement wherever Oracle names appear in marketing (website, store page, README, About,
  invoices if they mention Oracle).
* **Statspack** is an Oracle-provided tool name; treat it the same way.
* Use "Oracle" once with the attribution, not repeatedly in ad titles or keywords. Search-ad keyword bidding on
  trademarks is also rules-governed [verify per platform].
* Required statement (use verbatim): "Apodofos is not an Oracle product and is not affiliated with, sponsored by or
  endorsed by Oracle. Oracle and all Oracle product names are trademarks or registered trademarks of Oracle
  Corporation and/or its affiliates."
* **Do not present Oracle documentation text or Oracle report text as your own.** Cite by link only; samples must be
  synthetic (see `docs/licensing.md`).

## 5. Owner's own marks
Use "Apodofos" consistently with the TM symbol until registered; add a trademark line to the website footer
("Apodofos is a trademark of Wissam HANNA" only if you intend to claim it); keep a brand-usage rule for resellers.
