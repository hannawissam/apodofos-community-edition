// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Charts tab: renders every curated chart, reports charts with no SVG / overflowing SVG / clipped labels,
// hovers one point of each chart to check the tooltip stays inside the window, and shoots a screenshot per page of charts.
// Usage: node charts.mjs <dashboard.html> <outDir> [WxH] [light|dark]
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { launch, sleep } from './cdp.mjs';

const [file, out, vp = '1100x700', theme = 'light'] = process.argv.slice(2);
const [w, h] = vp.split('x').map(Number);
mkdirSync(out, { recursive: true });
const b = await launch();
try {
  await b.viewport(w, h); await b.open(resolve(file));
  await b.ev(`(function(){var s=document.createElement('style');s.textContent='*,*::before,*::after{animation:none!important;transition:none!important}';document.head.appendChild(s);var d=document.documentElement.getAttribute('data-theme')||(matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light'); if(d!=='${theme}') document.getElementById('btnTheme').click();})()`);
  await b.ev(`document.getElementById('tab-charts').click()`); await sleep(500);
  const total = await b.ev(`document.querySelectorAll('#curated .ccard').length`);
  console.log('curated charts:', total);
  const bad = [];
  for (let i = 0; i < total; i++) {
    await b.ev(`(function(){var c=document.querySelectorAll('#curated .ccard')[${i}]; document.getElementById('panel-charts').style.scrollBehavior='auto'; c.scrollIntoView({block:'start'});})()`);
    await sleep(250);
    const r = await b.ev(`(function(){var c=document.querySelectorAll('#curated .ccard')[${i}], box=c.querySelector('.cbox'), svg=box&&box.querySelector('svg');
      var out={id:c.dataset.chart,title:c.querySelector('h3').textContent,svg:!!svg,h:Math.round(c.getBoundingClientRect().height)};
      if(svg){var cr=c.getBoundingClientRect(), sr=svg.getBoundingClientRect(); out.svgOverflowX=sr.right>cr.right+1||sr.left<cr.left-1; out.cardScroll=c.scrollWidth>c.clientWidth+1;
        var texts=[].slice.call(svg.querySelectorAll('text')); out.clippedText=texts.filter(function(t){var q=t.getBoundingClientRect(); return q.width&&(q.left<sr.left-1||q.right>sr.right+1||q.top<sr.top-1||q.bottom>sr.bottom+1)}).map(function(t){return t.textContent.slice(0,24)}).slice(0,4);}
      return out;})()`);
    // hover the middle of the chart for a tooltip
    const pt = await b.ev(`(function(){var svg=document.querySelectorAll('#curated .ccard')[${i}].querySelector('.cbox svg'); if(!svg) return null; var q=svg.getBoundingClientRect(); var sh=svg.querySelectorAll('rect,path.seg,circle'); var t=sh.length?sh[Math.floor(sh.length/2)].getBoundingClientRect():q; return {x:t.left+t.width/2,y:t.top+t.height/2};})()`);
    if (pt) {
      await b.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y }); await sleep(150);
      r.tip = await b.ev(`(function(){var t=document.querySelector('.awc-tip.show'); if(!t) return null; var q=t.getBoundingClientRect(); return {inside:q.left>=0&&q.right<=innerWidth&&q.top>=0&&q.bottom<=innerHeight, w:Math.round(q.width)}})()`);
      await b.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 5, y: 5 });
    }
    if (!r.svg || r.svgOverflowX || r.cardScroll || (r.clippedText && r.clippedText.length) || (r.tip && !r.tip.inside)) bad.push(r);
    if (i % 3 === 0) await b.shot(join(out, `charts-${vp}-${theme}-${String(i).padStart(2, '0')}.png`));
  }
  console.log(bad.length ? 'PROBLEMS:\n' + bad.map((x) => JSON.stringify(x)).join('\n') : 'all charts OK');
  console.log(b.logs.join('\n') || 'console clean');
} finally { b.close(); }
