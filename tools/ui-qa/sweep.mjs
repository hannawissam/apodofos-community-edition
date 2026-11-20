// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Sweep: for each dashboard x viewport x theme x tab, run checks.js and print the distinct defects.
// Usage: node sweep.mjs <dashboard.html|dir> [--vp 1100x700,1400x900] [--themes light,dark] [--shots <dir>] [--scale 1.25]
import { readFileSync, readdirSync, statSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launch, sleep } from './cdp.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const checks = readFileSync(join(here, 'checks.js'), 'utf8');
const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : d; };
const target = resolve(args[0] || '.');
const files = statSync(target).isDirectory() ? readdirSync(target).filter((f) => f.endsWith('.html')).map((f) => join(target, f)) : [target];
const vps = opt('vp', '1100x700,1280x720,1400x900,1920x1080,1400x500').split(',').map((s) => s.split('x').map(Number));
const themes = opt('themes', 'light,dark').split(',');
const scale = parseFloat(opt('scale', '1'));
const shots = opt('shots', '');
if (shots) mkdirSync(shots, { recursive: true });
const TABS = ['summary', 'findings', 'charts', 'explorer', 'sql', 'script', 'docs'];

const b = await launch();
const agg = new Map();
try {
  for (const f of files) {
    const name = f.replace(/^.*[\\/]/, '').replace('.html', '');
    for (const [w, h] of vps) {
      await b.viewport(w, h, scale);
      await b.open(f);
      await b.ev(`(function(){var s=document.createElement('style');s.textContent='*,*::before,*::after{animation:none!important;transition:none!important;scroll-behavior:auto!important}';document.head.appendChild(s);})()`);
      for (const th of themes) {
        await b.ev(`(function(){var d=document.documentElement.getAttribute('data-theme')||(matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light'); if(d!=='${th}') document.getElementById('btnTheme').click();})()`);
        for (const tab of TABS) {
          await b.ev(`(function(){
            var t='${tab}';
            if(t==='explorer'){ document.getElementById('tab-charts').click(); var x=document.getElementById('cvAll'); if(x) x.click(); }
            else { document.getElementById('tab-'+t).click(); if(t==='charts'){ var c=document.getElementById('cvCur'); if(c) c.click(); } }
          })()`);
          await sleep(350);
          if (tab === 'charts') { // scroll through so every chart mounts, then back to top
            await b.ev(`(async function(){var p=document.getElementById('panel-charts');p.style.scrollBehavior='auto';for(var y=0;y<p.scrollHeight;y+=400){p.scrollTop=y;await new Promise(r=>setTimeout(r,60));}p.scrollTop=0;})()`);
            await sleep(300);
          }
          if (tab === 'findings') await b.ev(`(function(){var b=[].slice.call(document.querySelectorAll('#panel-findings .chipbtn')).filter(function(x){return x.textContent==='Expand all'})[0]; if(b) b.click();})()`);
          if (tab === 'sql') await b.ev(`(function(){var r=document.querySelector('#panel-sql tr.sqlrow'); if(r) r.click();})()`);
          await sleep(350);
          const res = await b.ev(checks);
          if (shots) await b.shot(join(shots, `${name}-${w}x${h}-${th}-${tab}.png`));
          for (const r of res) {
            const k = tab + ' | ' + r.replace(/\d+(\.\d+)?/g, '#');
            if (!agg.has(k)) agg.set(k, { n: 0, ex: [] });
            const a = agg.get(k); a.n++; if (a.ex.length < 2) a.ex.push(`${name} ${w}x${h} ${th}: ${r}`);
          }
        }
      }
    }
  }
  const sorted = [...agg.entries()].sort((a, b2) => b2[1].n - a[1].n);
  for (const [k, v] of sorted) console.log(`${v.n}x ${k}\n     e.g. ${v.ex[0]}`);
  console.log(sorted.length ? `\n${sorted.length} distinct defect signatures` : 'No defects detected.');
  const logs = [...new Set(b.logs)]; if (logs.length) console.log('\nCONSOLE:\n' + logs.join('\n'));
} finally { b.close(); }
