// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Scenario checks on one dashboard: empty state, desktop-mode chrome (recent files, notice, type menu), keyboard,
// reload leaks, print, hostile data (very long words, zero / one row). Prints PASS/FAIL lines and writes screenshots.
// Usage: node flows.mjs <dashboard.html> <outDir>
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { readFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launch, sleep } from './cdp.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const checks = readFileSync(join(here, 'checks.js'), 'utf8');
const [file, out] = process.argv.slice(2);
mkdirSync(out, { recursive: true });
const b = await launch();
let fails = 0;
const ok = (c, msg, extra = '') => { console.log((c ? 'PASS ' : 'FAIL ') + msg + (extra ? ' :: ' + extra : '')); if (!c) fails++; };
const clickId = (id) => b.ev(`document.getElementById('${id}').click()`);
const shot = (n) => b.shot(join(out, n + '.png'));
const issues = async (label) => { const r = (await b.ev(checks)).filter((x) => !/small-target|sr#|\.sr /.test(x)); ok(r.length === 0, 'layout checks: ' + label, r.slice(0, 6).join(' || ')); };

try {
  await b.open(resolve(file));
  await b.viewport(1100, 700);
  await b.ev(`(function(){var s=document.createElement('style');s.textContent='*,*::before,*::after{animation:none!important;transition:none!important;scroll-behavior:auto!important}';document.head.appendChild(s);})()`);
  await sleep(300);

  // ---- 1. empty state (export + desktop) ----
  await b.ev(`window.AwrApp.load({mode:'desktop'})`);
  await b.ev(`window.AwrApp.setRecent([
    {name:'AWR_SAMPLEDB_01012026_1200_1300_with_a_really_long_file_name_that_keeps_going_and_going.html', path:'C:/x/a.html', type:'AWR', opened:'2026-11-18', exists:true},
    {name:'gone.html', path:'C:/x/b.html', type:'ASH', opened:'2026-11-10', exists:false},
    {name:'sp.txt', path:'C:/x/c.txt', type:'Statspack', opened:'2026-11-01', exists:true}])`);
  await sleep(400);
  await shot('empty-desktop-1100x700');
  await issues('empty desktop 1100x700');
  await b.viewport(1100, 460); await sleep(200);
  const emptyScroll = await b.ev(`(function(){var e=document.getElementById('empty');return {sh:e.scrollHeight,ch:e.clientHeight,ov:getComputedStyle(e).overflowY}})()`);
  ok(emptyScroll.sh <= emptyScroll.ch || /auto|scroll/.test(emptyScroll.ov), 'empty state scrolls or fits at 1100x460', JSON.stringify(emptyScroll));
  await shot('empty-desktop-1100x460');
  await b.ev(`window.AwrApp.load({mode:'export'})`); await sleep(200);
  ok(await b.ev(`document.getElementById('btnOpenBig').offsetParent===null`), 'export mode hides the Open buttons');
  await shot('empty-export');
  await b.viewport(1100, 700);

  // ---- 2. desktop-mode chrome ----
  await b.ev(`window.AwrApp.load(Object.assign({}, window.__AWR__, {mode:'desktop'}))`);
  await b.ev(`window.AwrApp.setSource({provider:'oracle-awr', name:'AWR_SAMPLEDB.html', forced:false}); window.AwrApp.setAppVersion('1.0.4');
    window.AwrApp.notify({kind:'warning', message:'This report looks truncated: the last section ends in the middle of a table, so some findings may be missing. '.repeat(3)});`);
  await sleep(400);
  await shot('desktop-notice-1100x700');
  await issues('desktop with notice');
  await clickId('btnNotRight'); await sleep(200);
  await shot('desktop-typemenu');
  ok(await b.ev(`!document.getElementById('typeMenu').hidden && document.activeElement && document.activeElement.dataset.provider==='oracle-awr'`), 'type menu opens and focuses the current type');
  await b.key('Escape'); await sleep(100);
  ok(await b.ev(`document.getElementById('typeMenu').hidden && document.activeElement.id==='btnNotRight'`), 'Escape closes the type menu and returns focus');
  await clickId('btnAbout'); await sleep(300);
  ok(await b.ev(`document.getElementById('aboutVersion').hidden===false && /1\\.0\\.4/.test(document.getElementById('aboutVersionNo').textContent)`), 'About shows the version');
  await shot('about-1100x700');
  const dlg = await b.ev(`(function(){var d=document.getElementById('aboutDlg').getBoundingClientRect();return {h:d.height,top:d.top,bottom:d.bottom,vh:innerHeight, scroll:document.getElementById('aboutDlg').scrollHeight>document.getElementById('aboutDlg').clientHeight}})()`);
  ok(dlg.top >= 0 && dlg.bottom <= dlg.vh, 'About dialog fits in 700px high window', JSON.stringify(dlg));
  await b.key('Escape'); await sleep(200);
  ok(await b.ev(`!document.getElementById('aboutDlg').open`), 'Escape closes the About dialog');
  await b.viewport(1100, 460); await sleep(200); await clickId('btnAbout'); await sleep(300);
  const dlg2 = await b.ev(`(function(){var e=document.getElementById('aboutDlg'),d=e.getBoundingClientRect();return {top:d.top,bottom:d.bottom,vh:innerHeight, ov:getComputedStyle(e).overflowY, scrolls:e.scrollHeight>e.clientHeight}})()`);
  ok(dlg2.top >= 0 && dlg2.bottom <= dlg2.vh + 1, 'About dialog fits (or scrolls) in a 460px high window', JSON.stringify(dlg2));
  await shot('about-1100x460'); await b.key('Escape'); await b.viewport(1100, 700);
  await b.ev(`window.AwrApp.notify(null)`);

  // ---- 3. keyboard ----
  await b.ev(`document.activeElement && document.activeElement.blur(); window.scrollTo(0,0)`);
  const order = [];
  for (let i = 0; i < 40; i++) {
    await b.key('Tab');
    order.push(await b.ev(`(function(){var e=document.activeElement; if(!e||e===document.body) return 'BODY'; var cs=getComputedStyle(e); var r=e.getBoundingClientRect(); return (e.id||e.tagName.toLowerCase()+'.'+e.className)+'|'+((e.getAttribute('aria-label')||e.title||e.textContent||'').trim().slice(0,24))+'|ring:'+(cs.outlineStyle!=='none'&&parseFloat(cs.outlineWidth)>0 || cs.boxShadow!=='none')+'|vis:'+(r.width>0&&r.bottom>0&&r.top<innerHeight)})()`));
  }
  writeFileSync(join(out, 'tab-order.txt'), order.join('\n'));
  ok(!order.some((o) => /ring:false/.test(o) && !/splitter|BODY|reportFrame|panel|analysis/.test(o)), 'every focus stop shows a focus indicator', order.filter((o) => /ring:false/.test(o)).join(' ; '));
  ok(!order.some((o) => /vis:false/.test(o) && !/iframe/.test(o)), 'focus never lands on an invisible element', order.filter((o) => /vis:false/.test(o)).join(' ; '));
  await clickId('tab-sql'); await sleep(300);
  await b.ev(`document.querySelector('#panel-sql tr.sqlrow').focus()`);
  await b.key('Enter'); await sleep(150);
  ok(await b.ev(`document.querySelector('#panel-sql tr.sqlrow').getAttribute('aria-expanded')==='true' && !!document.querySelector('#panel-sql .sqldetail')`), 'Enter expands a SQL row');
  await b.key(' '); await sleep(150);
  ok(await b.ev(`document.querySelector('#panel-sql tr.sqlrow').getAttribute('aria-expanded')==='false'`), 'Space collapses a SQL row');
  await clickId('tab-findings'); await sleep(200);
  await b.ev(`document.querySelector('#panel-findings .fhead').focus()`);
  const before = await b.ev(`document.querySelector('#panel-findings .fcard').classList.contains('open')`);
  await b.key('Enter'); await sleep(150);
  ok((await b.ev(`document.querySelector('#panel-findings .fcard').classList.contains('open')`)) !== before, 'Enter toggles a finding');
  await b.ev(`document.getElementById('tab-findings').focus()`); await b.key('ArrowRight');

  // ---- 4. state leaks between reports ----
  await clickId('tab-findings');
  await b.ev(`var q=document.getElementById('fq'); q.value='zzz'; q.dispatchEvent(new Event('input')); document.querySelector('#sevFilters .filter').click();`);
  await clickId('tab-sql'); await b.ev(`var n=document.querySelector('#panel-sql .pgbtn[title="Next page"]'); if(n&&!n.disabled) n.click(); var s=document.querySelector('#panel-sql .pager select'); if(s){s.value='10'; s.dispatchEvent(new Event('change'));}`);
  await clickId('tab-charts'); await sleep(300); await clickId('cvAll'); await sleep(400);
  const nodes1 = await b.ev(`document.getElementsByTagName('*').length`);
  for (let i = 0; i < 5; i++) await b.ev(`window.AwrApp.load(Object.assign({}, window.__AWR__, {mode:'desktop'}))`);
  await sleep(500);
  const st = await b.ev(`(function(){return {tab:document.querySelector('[role=tab][aria-selected=true]').id, fq:(document.getElementById('fq')||{}).value, pressed:document.querySelectorAll('#sevFilters .filter[aria-pressed=true]').length,
    open:document.querySelectorAll('.fcard.open').length, hidden:document.querySelectorAll('.fcard[hidden]').length, charts:document.getElementById('panel-charts').children.length, sqlRows:0}})()`);
  ok(st.tab === 'tab-summary' && !st.fq && st.pressed === 0 && st.hidden === 0 && st.charts === 0, 'reload resets tab, filters and charts', JSON.stringify(st));
  await clickId('tab-sql'); await sleep(200);
  const pg = await b.ev(`document.querySelector('#panel-sql .pager .pgstatus').textContent`);
  ok(/Rows 1[–-]/.test(pg), 'reload resets the SQL page', pg);
  const nodes2 = await b.ev(`document.getElementsByTagName('*').length`);
  await clickId('tab-charts'); await sleep(300);
  ok(Math.abs(nodes2 - nodes1) < nodes1 * 0.25 + 500, 'DOM size stable after 5 reloads', nodes1 + ' -> ' + nodes2);

  // ---- 5. hostile data ----
  const long = 'A'.repeat(220);
  const mut = `(function(){var p=JSON.parse(JSON.stringify(window.__AWR__)); var a=p.analysis;
    a.findings[0].title='${long} very long finding title without spaces ${long}'; a.findings[0].plain='${long}${long}'; a.findings[0].evidence=[{label:'${long}',value:'${long}',note:'${long}'}];
    a.findings[0].sql=[{title:'${long}',code:'SELECT '+'x'.repeat(600)+' FROM dual',caution:'${long}'}];
    a.topSql=a.topSql.slice(0,1); a.topSql[0].sqlId='${long}'; a.topSql[0].module='${long}'; a.topSql[0].text='${long}${long}';
    a.meta.dbName='${long}'; a.meta.host='${long}'; a.kpis=a.kpis.slice(0,2); a.kpis[0].value='1234567890123456789012345678901234567890'; a.kpis[0].hint='${long}';
    a.actionPlan=[{rank:1,action:'${long}',why:'${long}',expectedImpactPct:50,effort:'Low',risk:'Low',confidence:'High',findingIds:[a.findings[0].id]}];
    a.health.headline='${long}'; a.dataQuality={level:'Poor',notes:['${long}'],alerts:['${long}']};
    window.AwrApp.load({analysis:a, reportHtml:p.reportHtml, mode:'desktop'}); return true;})()`;
  await b.ev(mut); await sleep(500);
  for (const t of ['summary', 'findings', 'sql', 'script']) {
    await clickId('tab-' + t); await sleep(300);
    if (t === 'findings') await b.ev(`document.querySelector('#panel-findings .fhead').click(); document.querySelector('#panel-findings .fhead').click(); document.querySelectorAll('.chipbtn').forEach(function(x){if(x.textContent==='Expand all')x.click()})`);
    await sleep(300); await shot('hostile-' + t);
    await issues('hostile data / ' + t);
  }
  await b.ev(`(function(){var p=JSON.parse(JSON.stringify(window.__AWR__)); var a=p.analysis; a.findings=[]; a.topSql=[]; a.charts=[]; a.tables=[]; a.kpis=[]; a.docs=[]; a.actionPlan=[]; a.warnings=[]; window.AwrApp.load({analysis:a, reportHtml:'', mode:'export'});})()`);
  await sleep(400);
  for (const t of ['summary', 'findings', 'charts', 'sql', 'script', 'docs']) { await clickId('tab-' + t); await sleep(250); await shot('zero-' + t); await issues('zero rows / ' + t); }
  await clickId('tab-charts'); await clickId('cvAll'); await sleep(300); await issues('zero rows / explorer');
  await b.ev(`(function(){var p=JSON.parse(JSON.stringify(window.__AWR__)); var a=p.analysis; a.findings=a.findings.slice(0,1); a.topSql=a.topSql.slice(0,1); a.charts=a.charts.slice(0,1); window.AwrApp.load({analysis:a, reportHtml:p.reportHtml, mode:'export'});})()`);
  await sleep(400);
  for (const t of ['findings', 'charts', 'sql', 'script']) { await clickId('tab-' + t); await sleep(250); await shot('one-' + t); await issues('one row / ' + t); }

  // ---- 6. print ----
  await b.ev(`window.AwrApp.load(window.__AWR__)`); await sleep(500);
  await b.media('print'); await sleep(300);
  await shot('print-media');
  const pr = await b.ev(`(function(){return {topbar:getComputedStyle(document.querySelector('.topbar')).display, report:getComputedStyle(document.getElementById('paneReport')).display, tabs:getComputedStyle(document.getElementById('tablist')).display, sw:document.documentElement.scrollWidth, cw:document.documentElement.clientWidth}})()`);
  ok(pr.topbar === 'none' && pr.report === 'none' && pr.tabs === 'none' && pr.sw <= pr.cw + 1, 'print hides chrome and does not overflow', JSON.stringify(pr));
  await b.media('');
  const pdf = await b.send('Page.printToPDF', { printBackground: true, preferCSSPageSize: false });
  writeFileSync(join(out, 'print.pdf'), Buffer.from(pdf.result.data, 'base64'));

  console.log('\nconsole:', [...new Set(b.logs)].join('\n') || 'clean');
  ok(b.logs.length === 0, 'no console errors or warnings');
} finally { b.close(); }
console.log(fails ? `\n${fails} FAILED` : '\nALL PASSED');
