// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Screenshots of every tab for one dashboard. Usage: node shots.mjs <dashboard.html> <outDir> [WxH] [light|dark] [scale]
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { launch, sleep } from './cdp.mjs';

const [file, out, vp = '1400x900', theme = 'light', scale = '1'] = process.argv.slice(2);
const [w, h] = vp.split('x').map(Number);
mkdirSync(out, { recursive: true });
const b = await launch();
try {
  await b.viewport(w, h, parseFloat(scale));
  await b.open(resolve(file));
  await b.ev(`(function(){var d=document.documentElement.getAttribute('data-theme')||(matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light'); if(d!=='${theme}') document.getElementById('btnTheme').click();})()`);
  const tag = `${vp}-${theme}`;
  const click = (id) => b.ev(`document.getElementById('${id}').click()`);
  for (const t of ['summary', 'findings', 'charts', 'sql', 'script', 'docs']) {
    await click('tab-' + t); await sleep(700);
    if (t === 'charts') { await b.ev(`(async function(){var p=document.getElementById('panel-charts');p.style.scrollBehavior='auto';for(var y=0;y<p.scrollHeight;y+=300){p.scrollTop=y;await new Promise(r=>setTimeout(r,80));}p.scrollTop=0;})()`); await sleep(400); }
    await b.shot(join(out, `${tag}-${t}.png`));
  }
  await click('tab-charts'); await click('cvAll'); await sleep(700); await b.shot(join(out, `${tag}-explorer.png`));
  await click('btnAbout'); await sleep(300); await b.shot(join(out, `${tag}-about.png`));
  console.log(b.logs.join('\n') || 'no console errors');
} finally { b.close(); }
