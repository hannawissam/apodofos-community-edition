// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Minimal Chrome DevTools Protocol driver (Node 22+, no dependencies) used by the UI QA scripts.
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export async function launch(opts = {}) {
  const edge = opts.browser || process.env.QA_BROWSER || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
  const port = opts.port || 9400 + Math.floor(Math.random() * 400);
  const dir = mkdtempSync(join(tmpdir(), 'apodofos-qa-'));
  const proc = spawn(edge, [`--remote-debugging-port=${port}`, '--headless=new', '--disable-gpu', '--no-first-run', `--user-data-dir=${dir}`, 'about:blank'], { stdio: 'ignore' });
  let wsUrl = null;
  for (let i = 0; i < 60 && !wsUrl; i++) {
    try { const t = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((x) => x.type === 'page'); if (t) wsUrl = t.webSocketDebuggerUrl; } catch { /* not up yet */ }
    if (!wsUrl) await sleep(200);
  }
  if (!wsUrl) { proc.kill(); throw new Error('browser did not start'); }
  const ws = new WebSocket(wsUrl);
  await new Promise((r) => (ws.onopen = r));
  let id = 0; const pending = new Map(); const listeners = [];
  ws.onmessage = (m) => {
    const d = JSON.parse(m.data);
    if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); } else if (d.method) listeners.forEach((l) => l(d));
  };
  const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
  const logs = [];
  listeners.push((d) => {
    if (d.method === 'Runtime.consoleAPICalled' && ['error', 'warning'].includes(d.params.type)) logs.push(d.params.type + ': ' + d.params.args.map((a) => a.value ?? a.description).join(' '));
    if (d.method === 'Runtime.exceptionThrown') logs.push('exception: ' + (d.params.exceptionDetails.exception?.description || d.params.exceptionDetails.text));
    if (d.method === 'Log.entryAdded' && ['error', 'warning'].includes(d.params.entry.level)) logs.push('log ' + d.params.entry.level + ': ' + d.params.entry.text + ' ' + (d.params.entry.url || ''));
  });
  await send('Page.enable'); await send('Runtime.enable'); await send('Log.enable');
  const api = {
    send, logs,
    async ev(expr) {
      const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
      if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 600));
      return r.result.result.value;
    },
    async viewport(w, h, scale = 1) { await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: scale, mobile: false }); await sleep(150); },
    async scheme(s) { await send('Emulation.setEmulatedMedia', { media: '', features: [{ name: 'prefers-color-scheme', value: s }] }); },
    async media(m) { await send('Emulation.setEmulatedMedia', { media: m, features: [] }); },
    async open(file) { await send('Page.navigate', { url: 'file:///' + file.replace(/\\/g, '/') }); await sleep(1800); },
    async shot(file) { const r = await send('Page.captureScreenshot', { format: 'png' }); writeFileSync(file, Buffer.from(r.result.data, 'base64')); },
    async key(key, mods = 0) {
      const vk = { Tab: 9, Enter: 13, Escape: 27, ' ': 32 }[key] || (key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0);
      const text = key === 'Enter' ? '\r' : key.length === 1 ? key : undefined;
      await send('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', key, modifiers: mods, windowsVirtualKeyCode: vk, text });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key, modifiers: mods, windowsVirtualKeyCode: vk });
    },
    close() { try { ws.close(); } catch { /* */ } proc.kill(); try { rmSync(dir, { recursive: true, force: true }); } catch { /* */ } }
  };
  return api;
}
