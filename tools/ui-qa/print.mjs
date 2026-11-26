// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Print / PDF report check: node print.mjs <dashboard.html> <outDir>
// Opens the dashboard on the FINDINGS tab (the case that used to print without the KPI tiles), triggers Print, then in print
// media verifies that the complete report is what gets printed: every KPI tile, every finding, the charts, nothing else.
// Writes report.pdf and a full-page screenshot. Prints PASS/FAIL lines; exit code 1 on any FAIL.
import { mkdirSync, writeFileSync, readFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { launch, sleep } from './cdp.mjs';

const [, , file, outArg] = process.argv;
if (!file) { console.error('usage: node print.mjs <dashboard.html> <outDir>'); process.exit(2); }
const out = resolve(outArg || 'print-out'); mkdirSync(out, { recursive: true });
let failed = 0;
const check = (ok, name, detail = '') => { console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' :: ' + detail : '')); if (!ok) failed++; };

const b = await launch();
try {
  await b.viewport(1400, 900);
  await b.open(resolve(file));
  await b.ev("window.__prints = 0; window.print = function () { window.__prints++; }"); // headless has no print dialog; count the calls instead
  await b.ev("document.getElementById('tab-findings').click()");
  await sleep(300);
  const expect = await b.ev("(function(){var A=window.__AWR__.analysis;return {kpis:A.kpis.length,findings:A.findings.filter(function(f){return f.severity!=='Ok';}).length,ok:A.findings.filter(function(f){return f.severity==='Ok';}).length,charts:A.charts.length,plan:(A.actionPlan||[]).length,sql:Math.min(15,(A.topSql||[]).length)};})()");
  await b.ev("document.getElementById('btnPrint').click()"); // builds the report; window.print() is a no-op in headless
  await sleep(500);
  await b.media('print');
  await b.viewport(794, 1123);
  await sleep(300);
  const m = await b.ev(`(function(){
    function vis(el){ if(!el) return false; var r=el.getBoundingClientRect(), s=getComputedStyle(el); return s.display!=='none' && s.visibility!=='hidden' && r.width>0 && r.height>0; }
    var rep=document.getElementById('printreport'), q=function(s){return rep.querySelectorAll(s);};
    var hidden=['.topbar','.split','#empty','.appfoot','#toast'].filter(function(s){var e=document.querySelector(s);return e&&vis(e);});
    return { reportVisible: vis(rep), hiddenStillShown: hidden, kpi: q('.rpt-kpi').length, findings: q('.rpt-f').length, ok: q('.rpt-oklist li').length,
      charts: q('.rpt-chart').length, drawn: q('.rpt-chart svg').length, plan: q('.rpt-planlist li').length, sqlRows: q('.rpt-grid tbody tr').length,
      h1: (q('h1')[0]||{}).textContent, sections: Array.prototype.map.call(q('.rpt-sec > h2'), function(h){return h.textContent;}),
      overflowX: rep.scrollWidth - rep.clientWidth, width: Math.round(rep.getBoundingClientRect().width),
      emptyBoxes: Array.prototype.filter.call(q('.rpt-chartbox'), function(e){return !e.querySelector('svg, .awc');}).length };
  })()`);
  console.log('sections:', m.sections.join(' | '));
  check(m.reportVisible, 'the report is visible in print media');
  check(m.hiddenStillShown.length === 0, 'app chrome is not printed', m.hiddenStillShown.join(','));
  check(m.kpi === expect.kpis && m.kpi > 0, 'every KPI tile is in the report (even when the Findings tab is open)', m.kpi + ' of ' + expect.kpis);
  check(m.findings === expect.findings, 'every finding is in the report', m.findings + ' of ' + expect.findings);
  check(m.ok === expect.ok, 'healthy checks are listed', m.ok + ' of ' + expect.ok);
  check(m.plan === expect.plan, 'Fix first plan is in the report', m.plan + ' of ' + expect.plan);
  check(m.charts === expect.charts, 'every chart has a figure', m.charts + ' of ' + expect.charts);
  check(m.drawn === m.charts, 'every chart is drawn', m.drawn + ' of ' + m.charts);
  check(m.sqlRows === expect.sql, 'top SQL table rows', m.sqlRows + ' of ' + expect.sql);
  check(!!m.h1, 'cover title present', m.h1);
  const tm = await b.ev("(function(){var t=document.getElementById('printreport').innerText;return {tm:/Apodofos\u2122 is a trademark of Wissam HANNA/.test(t),n:(t.match(/is a trademark of Wissam HANNA/g)||[]).length,product:document.querySelector('#printreport .rpt-product').textContent,page:document.querySelector('#printreport style').textContent};})()");
  check(tm.tm && tm.n >= 2, 'trade mark notice on the cover and in the closing note', tm.n + ' times');
  check(/^Apodofos Report Analyzer/.test(tm.product), 'cover shows the product name', tm.product);
  check(tm.page.includes(tm.product), 'page footer carries the product name', tm.page);
  check(m.overflowX <= 1, 'no horizontal overflow', 'overflow ' + m.overflowX + 'px, width ' + m.width + 'px');
  const refs = await b.ev(`(function(){var A=window.__AWR__.analysis, urls={}; A.findings.forEach(function(f){(f.docs||[]).forEach(function(d){urls[d.url]=1;});});
    var rep=document.getElementById('printreport'), a=Array.prototype.slice.call(rep.querySelectorAll('a'));
    return { expected: Object.keys(urls).length, listed: rep.querySelectorAll('.rpt-reflist > li').length,
      broken: a.filter(function(x){var h=x.getAttribute('href')||'';return h.charAt(0)==='#'?!document.getElementById(h.slice(1)):!/^https:\\/\\//.test(h);}).length,
      version: A.meta.analyzerVersion, cover: rep.querySelector('.rpt-gen').textContent, foot: rep.querySelector('.rpt-foot p').textContent }; })()`);
  check(refs.listed === refs.expected && refs.listed > 0, 'every referenced documentation page is listed once', refs.listed + ' of ' + refs.expected);
  check(refs.broken === 0, 'report links are https or point inside the report', refs.broken + ' broken');
  check(!!refs.version && refs.cover.includes(refs.version) && refs.foot.includes(refs.version) && tm.page.includes(refs.version), 'cover, closing note and page footer state the version', refs.version);

  // the same report whichever tab is open
  await b.media('');
  await b.viewport(1400, 900);
  for (const t of ['summary', 'charts', 'sql', 'docs']) {
    await b.ev("document.getElementById('tab-" + t + "').click()"); await sleep(150);
    await b.ev("document.getElementById('btnPrint').click()"); await sleep(250);
    const n = await b.ev("document.querySelectorAll('#printreport .rpt-kpi').length");
    check(n === expect.kpis, 'same report from the ' + t + ' tab', n + ' KPI tiles');
  }

  check((await b.ev('window.__prints')) === 5, 'every Print click reaches window.print', String(await b.ev('window.__prints')) + ' calls');

  // PDF + screenshot
  await b.media('print'); await b.viewport(794, 1123); await sleep(300);
  const pdf = await b.send('Page.printToPDF', { printBackground: true, preferCSSPageSize: true, displayHeaderFooter: false, transferMode: 'ReturnAsBase64' });
  if (!pdf.result) { console.log('printToPDF error:', JSON.stringify(pdf.error)); failed++; throw new Error('printToPDF failed'); }
  const bytes = Buffer.from(pdf.result.data, 'base64');
  writeFileSync(join(out, 'report.pdf'), bytes);
  const pages = (bytes.toString('latin1').match(/\/Type\s*\/Page[^s]/g) || []).length;
  check(bytes.length > 20000 && bytes.slice(0, 5).toString() === '%PDF-', 'PDF written', bytes.length + ' bytes, ' + pages + ' pages');
  const lm = await b.send('Page.getLayoutMetrics');
  const h = Math.min(Math.ceil(lm.result.cssContentSize.height), 16000);
  const shot = await b.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: 794, height: h, scale: 1 } });
  writeFileSync(join(out, 'report-full.png'), Buffer.from(shot.result.data, 'base64'));
  console.log('page count (PDF):', pages, '| screenshot height:', h);
  const noisy = b.logs.filter((l) => !/favicon/i.test(l));
  check(noisy.length === 0, 'no console errors', noisy.join(' ; '));
} finally { b.close(); }
console.log(failed ? '\n' + failed + ' FAILED' : '\nALL PASSED');
process.exit(failed ? 1 : 0);
