// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

/* Apodofos Report Analyzer - UI logic (vanilla JS, offline).
 * Reads window.__AWR__ = { analysis, reportHtml, mode } and exposes window.AwrApp.load(payload).
 * SECURITY: every string that comes from the report or the analysis is inserted with textContent /
 * setAttribute only (see h()). innerHTML is never used. The report itself lives in a sandboxed iframe (srcdoc).
 */
(function () {
  'use strict';

  /* ================= tiny DOM toolkit ================= */
  var NS = 'http://www.w3.org/2000/svg';
  var PROPS = { value: 1, checked: 1, disabled: 1, hidden: 1, type: 1, tabIndex: 1, selected: 1 };
  function add(parent, c) {
    if (c === null || c === undefined || c === false) return;
    if (Array.isArray(c)) { for (var i = 0; i < c.length; i++) add(parent, c[i]); return; }
    parent.appendChild(c.nodeType ? c : document.createTextNode(String(c)));
  }
  function h(tag, props) {
    var e = document.createElement(tag);
    if (props) {
      for (var k in props) {
        var v = props[k];
        if (v === null || v === undefined || v === false) continue;
        if (k === 'class') e.className = v;
        else if (k === 'text') e.textContent = v;
        else if (k === 'style') e.style.cssText = v;
        else if (k.slice(0, 2) === 'on') e.addEventListener(k.slice(2), v);
        else if (PROPS[k]) e[k] = v;
        else e.setAttribute(k, v === true ? '' : v);
      }
    }
    for (var i = 2; i < arguments.length; i++) add(e, arguments[i]);
    return e;
  }
  var ICONS = {
    chev: 'M6 9l6 6 6-6',
    search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM21 21l-4.3-4.3',
    copy: 'M9 9h10v11H9zM5 15V4h10',
    jump: 'M7 17L17 7M8 7h9v9',
    chart: 'M4 20V10M10 20V4M16 20v-7M22 20H2',
    table: 'M3 5h18v14H3zM3 10h18M9 5v14',
    download: 'M12 3v12m0 0l-4-4m4 4l4-4M5 20h14',
    check: 'M5 12l5 5 9-10'
  };
  function ico(name) {
    var s = document.createElementNS(NS, 'svg');
    s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true');
    var p = document.createElementNS(NS, 'path'); p.setAttribute('d', ICONS[name]); s.appendChild(p);
    return s;
  }
  function $(id) { return document.getElementById(id); }
  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }
  function num(v, d) {
    if (v === null || v === undefined || v === '' || isNaN(v)) return '-';
    try { return Number(v).toLocaleString(undefined, { maximumFractionDigits: d === undefined ? 2 : d }); } catch (e) { return String(v); }
  }
  function nextFrame(fn) { requestAnimationFrame(function () { requestAnimationFrame(fn); }); }

  /* ================= state ================= */
  var SEV = ['Critical', 'High', 'Medium', 'Low', 'Info', 'Ok'];
  var SEV_LABEL = { Critical: 'Critical', High: 'High', Medium: 'Medium', Low: 'Low', Info: 'Info', Ok: 'Healthy' };
  var A = null, REPORT = '', MODE = 'export';
  var st = null;
  var tablesById = {}, chartsById = {}, docsById = {};
  var frameReady = false, pendingJump = null, lastHi = null;
  var chartInstances = [];

  function freshState() {
    return { tab: 'summary', sev: {}, q: '', tech: false, open: {}, chartsView: 'curated', chartsBuilt: false, explorerBuilt: false, scriptText: '', dq: '', dbook: '', dtags: {}, cards: {}, techBtns: [] };
  }

  /* ================= host messaging ================= */
  function rtype() { var t = A && A.meta && A.meta.reportType; return t === 'ASH' || t === 'Statspack' ? t : 'AWR'; }
  function rtName() { return rtype(); }
  function timeLabel() { return rtype() === 'ASH' ? 'active session time' : rtype() === 'Statspack' ? 'DB time (Statspack)' : 'DB time'; }
  function applyWording() {
    var t = A ? rtName() + ' ' : '';
    $('paneReport').setAttribute('aria-label', 'Original ' + t + 'report');
    $('reportFrame').title = 'Original ' + t + 'report';
  }

  /* ---- report type badge, "Not right?" menu, notices, recent reports ---- */
  var SRC = null;
  function versionLabel(rel) {
    var m = /^(\d+)/.exec(rel || ''); if (!m) return '';
    var n = parseInt(m[1], 10);
    return n >= 23 ? n + 'ai' : n >= 12 ? n + 'c' : n + 'g';
  }
  function updateBadge() {
    var b = $('typeBadge'), nr = $('notRight');
    if (!A) { b.hidden = true; nr.hidden = true; closeTypeMenu(false); return; }
    var m = A.meta || {}, fmt = /html/i.test(m.format || '') ? 'HTML' : /text|txt/i.test(m.format || '') ? 'Text' : '';
    b.textContent = [rtName(), versionLabel(m.release), fmt].filter(Boolean).join(' \u00b7 ');
    b.title = SRC && SRC.forced ? 'Report type chosen by you' : 'Report type detected from the file contents';
    b.hidden = false;
    nr.hidden = MODE !== 'desktop' || !SRC;
    var items = $('typeMenu').querySelectorAll('[data-provider]');
    for (var i = 0; i < items.length; i++) items[i].setAttribute('aria-checked', SRC && items[i].dataset.provider === SRC.provider ? 'true' : 'false');
  }
  function openTypeMenu() {
    var mn = $('typeMenu'); mn.hidden = false; $('btnNotRight').setAttribute('aria-expanded', 'true');
    var cur = mn.querySelector('[aria-checked="true"]') || mn.querySelector('[data-provider]'); cur.focus();
  }
  function closeTypeMenu(refocus) {
    var mn = $('typeMenu'); if (mn.hidden) return;
    mn.hidden = true; $('btnNotRight').setAttribute('aria-expanded', 'false');
    if (refocus) $('btnNotRight').focus();
  }
  function initTypeMenu() {
    $('btnNotRight').addEventListener('click', function () { if ($('typeMenu').hidden) openTypeMenu(); else closeTypeMenu(true); });
    var mn = $('typeMenu');
    mn.addEventListener('click', function (e) {
      var b = e.target.closest('[data-provider]'); if (!b) return;
      closeTypeMenu(true);
      if (SRC && b.dataset.provider === SRC.provider) return;
      host({ type: 'reanalyze', provider: b.dataset.provider });
    });
    mn.addEventListener('keydown', function (e) {
      var items = Array.prototype.slice.call(mn.querySelectorAll('[data-provider]')), i = items.indexOf(document.activeElement);
      if (e.key === 'ArrowDown') { items[(i + 1) % items.length].focus(); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { items[(i + items.length - 1) % items.length].focus(); e.preventDefault(); }
      else if (e.key === 'Escape') { closeTypeMenu(true); e.preventDefault(); }
      else if (e.key === 'Tab') closeTypeMenu(false);
    });
    document.addEventListener('click', function (e) { if (!$('notRight').contains(e.target)) closeTypeMenu(false); });
  }
  function setSource(s) { SRC = s || null; if (SRC && SRC.name) document.title = SRC.name + ' \u2014 Apodofos Report Analyzer'; updateBadge(); }

  /* The desktop host tells the UI which version is running (the window title is set by the host). A saved page has no host:
     the About box and the printed report then use the version the analysis carries (meta.analyzerVersion). */
  var APP_VERSION = '';
  function setAppVersion(v) {
    if (!v) return;
    APP_VERSION = String(v); reportDirty = true; showVersion();
  }
  function showVersion() {
    var p = $('aboutVersion'), n = $('aboutVersionNo'); if (!p || !n) return;
    var v = window.AwrReport ? window.AwrReport.versionText(APP_VERSION, A && A.meta && A.meta.analyzerVersion) : APP_VERSION;
    n.textContent = v; p.hidden = !v;
  }

  function notify(n) {
    var box = $('notice'); n = n || {};
    if (!n.message) { box.hidden = true; return; }
    box.className = 'notice ' + (n.kind === 'warning' ? 'warn' : 'err');
    $('noticeText').textContent = n.message;
    box.hidden = false;
  }
  var RECENT = [];
  function fmtDate(d) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(d || ''); if (!m) return d || '';
    var mon = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'][parseInt(m[2], 10) - 1];
    return parseInt(m[3], 10) + ' ' + mon + ' ' + m[1];
  }
  function setRecent(list) { RECENT = Array.isArray(list) ? list.slice(0, 5) : []; renderRecent(); }
  function renderRecent() {
    var box = $('recent'), ul = clear($('recentList'));
    box.hidden = !RECENT.length || MODE !== 'desktop';
    RECENT.forEach(function (r) {
      var gone = r.exists === false;
      var open = h('button', { type: 'button', class: 'r-open', title: r.path, disabled: gone, 'aria-label': (r.name || r.path) + (gone ? ', file not found' : ', ' + (r.type || '') + ' report, opened ' + fmtDate(r.opened)),
        onclick: function () { host({ type: 'openPath', path: r.path }); } },
        h('span', { class: 'r-name', text: r.name || r.path }),
        h('span', { class: 'tbadge small', text: r.type || '' }),
        gone ? h('span', { class: 'r-gone', text: 'File not found' }) : h('span', { class: 'r-date', text: fmtDate(r.opened) }));
      var x = h('button', { type: 'button', class: 'r-x', title: gone ? 'File not found: remove from the list' : 'Remove from recent reports', 'aria-label': 'Remove ' + (r.name || r.path) + ' from recent reports',
        onclick: function () { host({ type: 'removeRecent', path: r.path }); } }, '\u00d7');
      ul.appendChild(h('li', { class: gone ? 'gone' : null }, open, x));
    });
  }

  // Host bridge (docs/ui-contract.md): every UI-to-host message goes through window.AwrHost.send. A shell may install its own
  // transport before the page starts: window.AwrHost = { post: function (msg) { ... } }. WebView2 is detected automatically.
  var AwrHost = window.AwrHost = window.AwrHost || {};
  AwrHost.send = function (msg) {
    if (typeof AwrHost.post === 'function') { AwrHost.post(msg); return true; }
    var w = window.chrome && window.chrome.webview;
    if (w && typeof w.postMessage === 'function') { w.postMessage(msg); return true; }
    return false;
  };
  function host(msg) { return MODE === 'desktop' && AwrHost.send(msg); }
  var toastTimer = null;
  function toast(msg) {
    var t = $('toast'); t.textContent = msg; t.classList.add('show');
    clearTimeout(toastTimer); toastTimer = setTimeout(function () { t.classList.remove('show'); }, 3600);
  }
  function legacyCopy(text) {
    var ta = h('textarea', { style: 'position:fixed;left:-9999px;top:0;opacity:0', 'aria-hidden': 'true' });
    ta.value = text; document.body.appendChild(ta); ta.select();
    var ok = false; try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    document.body.removeChild(ta); return ok;
  }
  function copyText(text, okMsg) {
    okMsg = okMsg || 'Copied to clipboard';
    if (host({ type: 'copy', text: text })) { toast(okMsg); return; }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(function () { toast(okMsg); }, function () { toast(legacyCopy(text) ? okMsg : 'Copy failed - select the text and press Ctrl+C'); });
    } else toast(legacyCopy(text) ? okMsg : 'Copy failed - select the text and press Ctrl+C');
  }
  function download(name, text, mime) {
    var blob = new Blob([text], { type: mime || 'text/plain;charset=utf-8' });
    var a = h('a', { href: URL.createObjectURL(blob), download: name });
    document.body.appendChild(a); a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); document.body.removeChild(a); }, 500);
  }
  function copyBtn(getText, label, cls) {
    var b = h('button', { type: 'button', class: cls || 'btn sm' }, ico('copy'), h('span', { text: label || 'Copy' }));
    b.addEventListener('click', function (e) {
      e.stopPropagation();
      copyText(getText());
      var s = b.querySelector('span'), old = s.textContent;
      s.textContent = 'Copied'; b.classList.add('done');
      setTimeout(function () { s.textContent = old; b.classList.remove('done'); }, 1400);
    });
    return b;
  }

  /* ================= theme ================= */
  function effectiveDark() {
    var a = document.documentElement.getAttribute('data-theme');
    if (a) return a === 'dark';
    return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  }
  function toggleTheme() {
    var next = effectiveDark() ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    try { localStorage.setItem('awr.theme', next); } catch (e) { /* storage may be blocked */ }
    syncFrameTheme();
  }
  function syncFrameTheme() {
    var f = $('reportFrame'), b = $('btnInvert');
    if (b.dataset.user !== '1') { var on = effectiveDark(); f.classList.toggle('invert', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); }
  }

  /* ================= report pane ================= */
  var HI_CSS = '.awr-highlight{outline:4px solid #F59E0B !important;outline-offset:5px;border-radius:2px;animation:awrp 1.2s ease 3}@keyframes awrp{50%{outline-color:#EF4444;outline-offset:8px}}html{scroll-behavior:smooth}';
  function onFrameLoad() {
    frameReady = true;
    try {
      var d = $('reportFrame').contentDocument;
      var s = d.createElement('style'); s.textContent = HI_CSS; (d.head || d.documentElement).appendChild(s);
    } catch (e) { /* cross-origin should not happen with allow-same-origin */ }
    $('frameEmpty').hidden = true;
    if (pendingJump) { var id = pendingJump; pendingJump = null; showTable(id); }
  }
  function ensureReportVisible() {
    var sp = $('split');
    if (sp.classList.contains('rep-collapsed')) { sp.classList.remove('rep-collapsed'); $('btnLayoutReport').setAttribute('aria-pressed', 'false'); }
  }
  function showTable(id) {
    if (!id) return;
    ensureReportVisible();
    var doc = null;
    try { doc = $('reportFrame').contentDocument; } catch (e) { doc = null; }
    if (!frameReady || !doc) { pendingJump = id; return; }
    var el = doc.getElementById(id);
    if (!el) { toast('That table could not be found in the report'); return; }
    if (lastHi) { try { lastHi.classList.remove('awr-highlight'); } catch (e) { /* detached */ } }
    el.classList.add('awr-highlight'); lastHi = el; showLearn(id);
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }
  function buildJump() {
    var sel = clear($('jump'));
    sel.appendChild(h('option', { value: '', text: 'Jump to section or table\u2026' }));
    var groups = {}, order = [];
    A.tables.forEach(function (t) {
      var g = t.section || 'Report header';
      if (!groups[g]) { groups[g] = h('optgroup', { label: g }); order.push(g); }
      groups[g].appendChild(h('option', { value: t.id, text: t.title || t.id }));
    });
    order.forEach(function (g) { sel.appendChild(groups[g]); });
    sel.onchange = function () { if (sel.value) showTable(sel.value); sel.selectedIndex = 0; };
  }

  /* ================= print / PDF report ================= */
  var reportDirty = true;
  function buildReport() {
    var box = $('printreport');
    if (!A || !box || !window.AwrReport) { if (box) clear(box); return false; }
    if (!reportDirty && box.firstChild) return true;
    clear(box);
    try {
      var logo = document.querySelector('.logo svg');
      var el = window.AwrReport.build(A, { logo: logo ? uniqueIds(logo.cloneNode(true)) : null, version: APP_VERSION, now: new Date() });
      box.appendChild(el);
      if (el._afterAttach) el._afterAttach(); /* charts draw once the report is in the page (they measure their width) */
      reportDirty = false;
      return true;
    } catch (e) { clear(box); return false; }
  }

  /* ================= load ================= */
  function load(payload) {
    payload = payload || {};
    reportDirty = true; clear($('printreport'));
    MODE = payload.mode === 'desktop' ? 'desktop' : 'export';
    document.body.setAttribute('data-mode', MODE);
    chartInstances.forEach(function (c) { try { c.destroy(); } catch (e) { /* ignore */ } });
    chartInstances = [];
    A = payload.analysis || null; REPORT = payload.reportHtml || '';
    st = freshState(); frameReady = false; pendingJump = null; lastHi = null;
    showVersion();
    var empty = $('empty');
    notify(null); SRC = null; renderRecent();
    if (!A) {
      empty.hidden = false;
      $('emptyHint').textContent = MODE === 'desktop' ? 'Supported: Oracle 19c and later AWR, ASH and Statspack reports saved as .html or .txt.' : 'No analysis data is embedded in this page.';
      $('appTitle').textContent = 'Apodofos Report Analyzer';
      $('appSub').textContent = 'Offline performance report analysis';
      document.title = 'Apodofos Report Analyzer';
      $('reportFrame').srcdoc = '';
      $('frameEmpty').hidden = false;
      ['summary', 'findings', 'charts', 'sql', 'script', 'docs'].forEach(function (n) { clear($('panel-' + n)); });
      applyWording(); updateBadge(); $('learn').hidden = true;
      $('cntFindings').textContent = ''; $('cntSql').textContent = '';
      return;
    }
    empty.hidden = true;
    A.meta = A.meta || {}; A.health = A.health || { score: 0, grade: '', headline: '', paragraphs: [], counts: {} };
    A.kpis = A.kpis || []; A.findings = A.findings || []; A.charts = A.charts || []; A.tables = A.tables || []; A.topSql = A.topSql || []; A.warnings = A.warnings || [];
    A.rootCauses = A.rootCauses || []; A.actionPlan = A.actionPlan || []; A.dataQuality = A.dataQuality || { level: 'Good', notes: [] }; A.dataQuality.notes = A.dataQuality.notes || []; A.dataQuality.alerts = A.dataQuality.alerts || [];
    A.docs = A.docs || []; A.sectionDocs = A.sectionDocs || {};
    docsById = {}; A.docs.forEach(function (d) { docsById[d.id] = d; });
    tablesById = {}; A.tables.forEach(function (t) { tablesById[t.id] = t; });
    chartsById = {}; A.charts.forEach(function (c) { chartsById[c.id] = c; });
    A.findings = A.findings.map(function (f, i) { f._i = i; return f; }).sort(function (a, b) {
      var d = SEV.indexOf(a.severity) - SEV.indexOf(b.severity); return d || (b.impact || 0) - (a.impact || 0) || a._i - b._i;
    });
    A._refs = window.AwrReport ? window.AwrReport.references(A) : { list: [], byUrl: {} }; /* after the sort: the numbers follow it */
    var m = A.meta;
    var name = [m.dbName, m.instance && m.instance !== m.dbName ? m.instance : ''].filter(Boolean).join(' / ') || m.sourceFile || ((m.reportType || 'AWR') + ' report');
    $('appTitle').textContent = name; $('appTitle').title = name;
    $('appSub').textContent = [m.release ? 'Oracle ' + m.release : '', m.host, m.beginTime && m.endTime ? m.beginTime + ' \u2192 ' + m.endTime : ''].filter(Boolean).join('  \u00b7  ') || ((m.reportType || 'AWR') + ' report analysis');
    $('appSub').title = $('appSub').textContent;
    document.title = name + ' \u2014 Apodofos Report Analyzer';
    var fr = $('reportFrame');
    $('frameEmpty').hidden = !!REPORT; frameReady = !REPORT;
    fr.srcdoc = REPORT;
    syncFrameTheme();
    buildJump();
    $('cntFindings').textContent = String(A.findings.filter(function (f) { return f.severity !== 'Ok' && f.severity !== 'Info'; }).length || A.findings.length);
    $('tab-findings').title = 'Issues to address (Info and Healthy items are also listed inside)';
    $('cntSql').textContent = A.topSql.length ? String(A.topSql.length) : '';
    applyWording(); updateBadge(); $('learn').hidden = true; clear($('learn'));
    buildSummary(); buildFindings(); buildSqlTab(); buildScript(); buildDocs();
    clear($('panel-charts'));
    activateTab('summary', false);
  }

  /* ================= tabs ================= */
  var TABS = ['summary', 'findings', 'charts', 'sql', 'script', 'docs'];
  function activateTab(name, focus) {
    st.tab = name;
    TABS.forEach(function (n) {
      var sel = n === name, b = $('tab-' + n), p = $('panel-' + n);
      b.setAttribute('aria-selected', sel ? 'true' : 'false'); b.tabIndex = sel ? 0 : -1;
      p.hidden = !sel;
    });
    if (focus) $('tab-' + name).focus();
    if (name === 'charts') ensureCharts();
    $('panel-' + name).scrollTop = 0;
  }
  function initTabs() {
    var tl = $('tablist');
    tl.addEventListener('click', function (e) { var b = e.target.closest('[data-tab]'); if (b) activateTab(b.dataset.tab, false); });
    tl.addEventListener('keydown', function (e) {
      var i = TABS.indexOf(st ? st.tab : 'summary'), n = -1;
      if (e.key === 'ArrowRight') n = (i + 1) % TABS.length;
      else if (e.key === 'ArrowLeft') n = (i + TABS.length - 1) % TABS.length;
      else if (e.key === 'Home') n = 0; else if (e.key === 'End') n = TABS.length - 1;
      if (n >= 0) { e.preventDefault(); activateTab(TABS[n], true); }
    });
  }

  /* ================= summary ================= */
  function ringColor(score) { return score >= 85 ? 'var(--good)' : score >= 70 ? 'var(--accent)' : score >= 50 ? 'var(--warn)' : 'var(--bad)'; }
  function buildSummary() {
    var p = clear($('panel-summary')), hl = A.health, m = A.meta;
    var C = 2 * Math.PI * 52, score = Math.max(0, Math.min(100, +hl.score || 0)), unreliable = hl.reliable === false;
    var ring = h('div', { class: 'ring', role: 'img', 'aria-label': unreliable ? 'Health score not reliable for this report' : 'Health score ' + score + ' out of 100, grade ' + hl.grade },
      (function () {
        var s = document.createElementNS(NS, 'svg'); s.setAttribute('viewBox', '0 0 120 120'); s.setAttribute('aria-hidden', 'true');
        var t = document.createElementNS(NS, 'circle'); [['cx', 60], ['cy', 60], ['r', 52], ['class', 'track']].forEach(function (a) { t.setAttribute(a[0], a[1]); }); s.appendChild(t);
        var a = document.createElementNS(NS, 'circle'); [['cx', 60], ['cy', 60], ['r', 52], ['class', 'arc'], ['stroke-linecap', 'round']].forEach(function (x) { a.setAttribute(x[0], x[1]); });
        a.style.setProperty('--len', unreliable ? '0' : (C * score / 100).toFixed(1)); a.style.setProperty('--ring', ringColor(score)); s.appendChild(a);
        return s;
      })(),
      h('div', { class: 'center' }, h('div', { class: 'score', text: unreliable ? '—' : String(score) }), h('div', { class: 'grade', text: unreliable ? 'NOT RELIABLE' : 'GRADE ' + (hl.grade || '-') })));
    var chips = h('div', { class: 'sevchips' });
    SEV.forEach(function (s) {
      var n = (hl.counts && hl.counts[s]) || 0;
      if (!n) return;
      chips.appendChild(h('button', { type: 'button', class: 'sevchip sev-' + s, title: 'Show ' + SEV_LABEL[s] + ' findings', onclick: function () { st.sev = {}; st.sev[s] = true; syncFilters(); activateTab('findings', false); } },
        h('b', { text: String(n) }), SEV_LABEL[s]));
    });
    p.appendChild(h('div', { class: 'hero' }, ring, h('div', { class: 'hero-text' }, h('h2', null, hl.headline || 'Analysis complete', h('span', { class: 'rtbadge', title: 'Report type', text: rtName() })),
      h('p', { text: 'Overall health score from 0 (urgent attention) to 100 (healthy), based on the issues found below.' }), chips)));

    var dq = A.dataQuality;
    if (dq.alerts.length) {
      p.appendChild(h('div', { class: 'dqalert', role: 'alert' }, h('strong', { text: 'Warning about this report' }),
        h('ul', null, dq.alerts.map(function (n) { return h('li', { text: n }); }))));
    }
    if (dq.level && dq.level !== 'Good') {
      p.appendChild(h('div', { class: 'dqbanner dq-' + dq.level, role: 'note' }, h('strong', { text: 'Data quality: ' + dq.level }),
        h('span', { class: 'dqsub', text: ' Treat the conclusions below with care.' }),
        dq.notes.length ? h('ul', null, dq.notes.map(function (n) { return h('li', { text: n }); })) : null));
    }
    if (A.kpis.length) {
      p.appendChild(h('div', { class: 'panel-title', text: 'Key numbers' }));
      var g = h('div', { class: 'kpis' });
      A.kpis.forEach(function (k, i) {
        g.appendChild(h('div', { class: 'kpi ' + String(k.status || 'neutral').toLowerCase(), style: '--i:' + i, title: k.hint || '' },
          h('div', { class: 'l', text: k.label }), h('div', { class: 'v' }, k.value, k.unit ? h('small', { text: k.unit }) : null),
          k.hint ? h('div', { class: 'h', text: k.hint }) : null));
      });
      p.appendChild(g);
    }
    if (A.actionPlan.length) {
      p.appendChild(h('div', { class: 'panel-title', text: 'Fix first' }));
      var ol = h('ol', { class: 'plan', 'aria-label': 'Ranked action plan' });
      A.actionPlan.forEach(function (a, i) {
        var ids = (a.findingIds || []).filter(function (id) { return A.findings.some(function (f) { return f.id === id; }); });
        var inner = [h('span', { class: 'rank', 'aria-hidden': 'true', text: String(a.rank || i + 1) }),
          h('span', { class: 'pbody' }, h('span', { class: 'pt', text: a.action }), a.why ? h('span', { class: 'pw', text: a.why }) : null,
            h('span', { class: 'pmeta' },
              a.expectedImpact || a.expectedImpactPct ? h('span', { class: 'tag', title: 'Upper bound, not a promise', text: 'Impact: ' + (a.expectedImpact || (num(a.expectedImpactPct, 0) + '% of ' + timeLabel())) }) : null,
              a.effort ? h('span', { class: 'tag lvl-' + a.effort, text: 'Effort: ' + a.effort }) : null,
              a.risk ? h('span', { class: 'tag lvl-' + a.risk, text: 'Risk: ' + a.risk }) : null,
              a.confidence ? confBadge(a.confidence, '') : null))];
        if (ids.length) inner.push(ico('jump'));
        var el = ids.length ? h('button', { type: 'button', title: 'Open the related finding', onclick: function () { gotoFinding(ids[0]); } }, inner) : h('div', { class: 'planrow' }, inner);
        ol.appendChild(h('li', null, el));
      });
      p.appendChild(ol);
    }
    if (hl.paragraphs && hl.paragraphs.length) {
      p.appendChild(h('div', { class: 'panel-title', text: 'In plain English' }));
      var nar = h('div', { class: 'card narrative' });
      hl.paragraphs.forEach(function (t) { nar.appendChild(h('p', { text: t })); });
      p.appendChild(nar);
    }
    var top = A.findings.filter(function (f) { return f.severity !== 'Ok' && f.severity !== 'Info'; }).slice(0, 4);
    if (top.length) {
      p.appendChild(h('div', { class: 'panel-title', text: 'Top priorities' }));
      var ul = h('ul', { class: 'toplist' });
      top.forEach(function (f) {
        ul.appendChild(h('li', null, h('button', { type: 'button', onclick: function () { gotoFinding(f.id); } },
          h('span', { class: 'badge sev-' + f.severity, text: SEV_LABEL[f.severity] || f.severity }), h('span', { class: 't', text: f.title }), ico('jump'))));
      });
      p.appendChild(ul);
    }
    p.appendChild(h('div', { class: 'panel-title', text: 'About this report' }));
    var rows = [
      ['Database', [m.dbName, m.dbId ? '(' + m.dbId + ')' : ''].filter(Boolean).join(' ')], ['Instance', m.instance + (m.rac ? ' (RAC)' : '')],
      ['Oracle release', m.release], ['Host', m.host], ['Platform', m.platform],
      ['CPUs / cores', m.cpus ? m.cpus + (m.cores ? ' / ' + m.cores : '') : ''], ['Memory', m.memoryGb ? num(m.memoryGb, 1) + ' GB' : ''],
      ['Container DB', m.cdb === true ? 'Yes' : m.cdb === false ? 'No' : ''],
      ['Snapshots', m.beginSnap && m.endSnap ? m.beginSnap + ' \u2192 ' + m.endSnap : ''], ['From', m.beginTime], ['To', m.endTime],
      ['Elapsed', m.elapsedMinutes ? num(m.elapsedMinutes, 1) + ' min' : ''], [m.reportType === 'ASH' ? 'Active session time' : 'DB time', m.dbTimeMinutes ? num(m.dbTimeMinutes, 1) + ' min' : ''],
      ['Avg active sessions', m.averageActiveSessions ? num(m.averageActiveSessions, 1) : ''], ['Number format', m.numberFormat], ['Source file', m.sourceFile],
      ['Analyzed', [m.analyzedAt, m.analyzerVersion ? 'v' + m.analyzerVersion : ''].filter(Boolean).join('  ')]
    ].filter(function (r) { return r[1]; });
    var dl = h('dl', { class: 'metagrid' });
    rows.forEach(function (r) { dl.appendChild(h('div', null, h('dt', { text: r[0] }), h('dd', { text: r[1] }))); });
    p.appendChild(h('div', { class: 'card' }, dl));
    if (A.warnings.length) {
      p.appendChild(h('div', { class: 'panel-title', text: 'Analysis notes' }));
      p.appendChild(h('details', { class: 'warnbox' }, h('summary', { text: A.warnings.length + ' note(s) from the analyzer' }),
        h('ul', null, A.warnings.map(function (w) { return h('li', { text: w }); }))));
    }
    buildReferences(p);
  }

  /* ---- referenced documentation, at the end of the Summary tab (data and wording: report.js, so print matches) ---- */
  function buildReferences(p) {
    var list = A._refs.list; if (!list.length) return;
    p.appendChild(h('h3', { class: 'panel-title', id: 'refsTitle', tabindex: '-1', text: window.AwrReport.referencesTitle }));
    p.appendChild(h('p', { class: 'refnote', text: window.AwrReport.referencesNote }));
    p.appendChild(h('ol', { class: 'reflist', 'aria-labelledby': 'refsTitle' }, list.map(refItem)));
  }
  function refItem(r) {
    var link = r.local ? null : docLink(r.title, r.url, '');
    var meta = [r.book, r.section ? 'Section: ' + r.section : ''].filter(Boolean).join('  \u00b7  ');
    return h('li', { class: 'ref', id: 'ref-' + r.n, tabindex: '-1' }, h('span', { class: 'refn', text: '[' + r.n + ']' }),
      h('div', { class: 'refbody' }, link || h('span', { class: 'reftitle', text: r.title }),
        meta ? h('div', { class: 'refmeta', text: meta }) : null,
        r.local ? h('div', { class: 'refurl', text: 'Local reference (on the database host): ' + r.local }) : link ? h('div', { class: 'refurl', text: r.url }) : null,
        h('div', { class: 'refcited' }, h('span', { text: 'Cited by:' }), r.findings.map(function (f) {
          return h('button', { type: 'button', class: 'chipbtn', title: 'Open this finding', onclick: function () { gotoFinding(f.id); } }, f.title);
        }))));
  }
  /* n = 0: the top of the list */
  function gotoReference(n) {
    activateTab('summary', false);
    setTimeout(function () {
      var el = $(n ? 'ref-' + n : 'refsTitle'); if (!el) return;
      el.focus({ preventScroll: true }); el.scrollIntoView({ block: n ? 'center' : 'start', behavior: 'smooth' });
      if (n) { el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash'); }
    }, 60);
  }

  /* ================= findings ================= */
  function confBadge(c, why) {
    return h('span', { class: 'conf conf-' + c, title: why || ('Confidence in this result: ' + c), text: 'Confidence: ' + c });
  }
  function sevCounts() { var c = {}; A.findings.forEach(function (f) { c[f.severity] = (c[f.severity] || 0) + 1; }); return c; }
  function hay(f) {
    if (f._hay) return f._hay;
    var parts = [f.title, f.category, f.plain, f.whyItMatters, f.technical, f.severity, (f.recommendations || []).join(' ')];
    (f.evidence || []).forEach(function (e) { parts.push(e.label, e.value, e.note); });
    (f.sql || []).forEach(function (s) { parts.push(s.title, s.code); });
    (f.sourceViews || []).forEach(function (v) { parts.push(v); });
    f._hay = parts.filter(Boolean).join('\n').toLowerCase(); return f._hay;
  }
  function safeUrl(u) { return /^https:\/\//i.test(u || '') ? u : null; }

  function buildFindings() {
    var p = clear($('panel-findings')); st.cards = {}; st.techBtns = [];
    if (!A.findings.length) { p.appendChild(h('div', { class: 'empty-note', text: 'No findings were produced for this report.' })); return; }
    var counts = sevCounts();
    var search = h('input', { type: 'search', placeholder: 'Search findings, evidence, SQL\u2026', 'aria-label': 'Search findings', id: 'fq' });
    search.addEventListener('input', function () { st.q = search.value.trim().toLowerCase(); applyFilters(); });
    var seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Detail level' },
      h('button', { type: 'button', 'aria-pressed': 'true', id: 'segPlain', text: 'Plain English', onclick: function () { setTech(false); } }),
      h('button', { type: 'button', 'aria-pressed': 'false', id: 'segTech', text: 'Technical', onclick: function () { setTech(true); } }));
    var filters = h('div', { class: 'toolrow', role: 'group', 'aria-label': 'Filter by severity', id: 'sevFilters' });
    SEV.forEach(function (s) {
      if (!counts[s]) return;
      filters.appendChild(h('button', { type: 'button', class: 'filter sev-' + s, 'data-sev': s, 'aria-pressed': 'false', onclick: function () { if (st.sev[s]) delete st.sev[s]; else st.sev[s] = true; syncFilters(); } },
        SEV_LABEL[s], h('span', { class: 'n', text: String(counts[s]) })));
    });
    var groups = []; A.findings.forEach(function (f) { if (f.rootCauseGroup && groups.indexOf(f.rootCauseGroup) < 0) groups.push(f.rootCauseGroup); });
    if (groups.length) {
      var gsel = h('select', { id: 'fgroup', 'aria-label': 'Filter by root cause' }, h('option', { value: '', text: 'All root causes' }));
      groups.forEach(function (g) { gsel.appendChild(h('option', { value: g, text: g })); });
      gsel.addEventListener('change', function () { st.group = gsel.value; syncFilters(); });
      filters.appendChild(h('label', { class: 'gfilter' }, 'Root cause ', gsel));
    }
    filters.appendChild(h('button', { type: 'button', class: 'chipbtn', text: 'Clear', id: 'fclear', onclick: function () { st.sev = {}; st.q = ''; st.group = ''; search.value = ''; syncFilters(); } }));
    var tb = h('div', { class: 'toolbar' },
      h('div', { class: 'toolrow' }, h('label', { class: 'search' }, ico('search'), search), seg,
        h('button', { type: 'button', class: 'chipbtn', text: 'Expand all', onclick: function () { setAllOpen(true); } }),
        h('button', { type: 'button', class: 'chipbtn', text: 'Collapse all', onclick: function () { setAllOpen(false); } })),
      filters, h('div', { class: 'muted', id: 'fcount', 'aria-live': 'polite', style: 'font-size:12px' }));
    p.appendChild(tb);
    var list = h('div', { id: 'flist' });
    A.findings.forEach(function (f, i) { var c = fcard(f, i); st.cards[f.id] = c; list.appendChild(c); });
    list.appendChild(h('div', { class: 'empty-note', id: 'fnone', hidden: true, text: 'No findings match the current filters.' }));
    p.appendChild(list);
    syncFilters();
  }
  function syncFilters() {
    var any = Object.keys(st.sev).length > 0;
    document.querySelectorAll('#sevFilters .filter').forEach(function (b) { b.setAttribute('aria-pressed', st.sev[b.dataset.sev] ? 'true' : 'false'); });
    var c = $('fclear'); if (c) c.hidden = !(any || st.q || st.group);
    var gs = $('fgroup'); if (gs) gs.value = st.group || '';
    var q = $('fq'); if (q && q.value.trim().toLowerCase() !== st.q) q.value = st.q;
    applyFilters();
  }
  function applyFilters() {
    var any = Object.keys(st.sev).length > 0, shown = 0;
    A.findings.forEach(function (f) {
      var ok = (!any || st.sev[f.severity]) && (!st.group || f.rootCauseGroup === st.group) && (!st.q || hay(f).indexOf(st.q) >= 0);
      var c = st.cards[f.id]; if (c) c.hidden = !ok; if (ok) shown++;
    });
    var fc = $('fcount'); if (fc) fc.textContent = 'Showing ' + shown + ' of ' + A.findings.length + ' findings';
    var fn = $('fnone'); if (fn) fn.hidden = shown > 0;
  }
  function setTech(on) {
    st.tech = on;
    $('segPlain').setAttribute('aria-pressed', on ? 'false' : 'true'); $('segTech').setAttribute('aria-pressed', on ? 'true' : 'false');
    st.techBtns.forEach(function (fn) { fn(on); });
  }
  function setOpen(card, id, open) {
    card.classList.toggle('open', open); st.open[id] = open;
    var b = card.querySelector('.fhead'); b.setAttribute('aria-expanded', open ? 'true' : 'false');
  }
  function setAllOpen(open) { A.findings.forEach(function (f) { var c = st.cards[f.id]; if (c) setOpen(c, f.id, open); }); }
  function gotoFinding(id) {
    activateTab('findings', false);
    var any = Object.keys(st.sev).length > 0;
    var f = A.findings.filter(function (x) { return x.id === id; })[0];
    if (f && ((any && !st.sev[f.severity]) || (st.group && f.rootCauseGroup !== st.group) || (st.q && hay(f).indexOf(st.q) < 0))) { st.sev = {}; st.q = ''; st.group = ''; syncFilters(); }
    var c = st.cards[id]; if (!c) return;
    setOpen(c, id, true);
    setTimeout(function () { c.scrollIntoView({ block: 'start', behavior: 'smooth' }); c.classList.remove('flash'); void c.offsetWidth; c.classList.add('flash'); }, 60);
  }
  function showChart(id) {
    st.chartsView = 'curated'; activateTab('charts', false);
    setTimeout(function () {
      var el = document.querySelector('.ccard[data-chart="' + (window.CSS && CSS.escape ? CSS.escape(id) : id.replace(/"/g, '')) + '"]');
      if (el) { el.scrollIntoView({ block: 'center', behavior: 'smooth' }); el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash'); }
    }, 120);
  }

  function catalogByUrl(u) {
    if (!u) return null;
    if (!A._byUrl) { A._byUrl = {}; A.docs.forEach(function (d) { if (d.url && !A._byUrl[d.url]) A._byUrl[d.url] = d; }); }
    return A._byUrl[u] || null;
  }
  /* Doc links: the export opens them in a new tab; the desktop host opens them in the default browser ({type:'openUrl'}, https only,
     validated again by the host). Without a host (or with an old one) the link is copied instead. file:/// is never a link. */
  function docAnchor(title, url, tip) {
    var u = safeUrl(url); if (!u) return null;
    var a = h('a', { href: u, target: '_blank', rel: 'noopener noreferrer', title: tip ? tip + '\n' + u : u, text: title || u });
    if (MODE === 'desktop') a.addEventListener('click', function (e) {
      e.preventDefault();
      if (!host({ type: 'openUrl', url: u })) copyText(u, 'Link copied: paste it in a browser');
    });
    return a;
  }
  /* A link plus a small "copy link" button: open it in the browser, or copy it to use elsewhere. */
  function docLink(title, url, tip) {
    var a = docAnchor(title, url, tip); if (!a) return null;
    var u = safeUrl(url);
    var b = h('button', { type: 'button', class: 'btn sm linkcopy', title: 'Copy link', 'aria-label': 'Copy link: ' + (title || u) }, ico('copy'));
    b.addEventListener('click', function (e) { e.stopPropagation(); copyText(u, 'Link copied'); });
    return h('span', { class: 'doclink' }, a, b);
  }
  function showLearn(tableId) {
    var box = clear($('learn')), ids = (A.sectionDocs && A.sectionDocs[tableId]) || [], n = 0;
    box.appendChild(document.createTextNode('Learn more: '));
    ids.forEach(function (id) {
      var d = docsById[id]; if (!d) return;
      var a = d.local ? h('span', { class: 'tag', title: d.local, text: d.title }) : docLink(d.title, d.url, d.summary);
      if (!a) return;
      if (n++) box.appendChild(document.createTextNode(' \u00b7 '));
      box.appendChild(a);
    });
    box.hidden = !n;
  }

  /* ================= documentation tab ================= */
  function buildDocs() {
    var p = clear($('panel-docs'));
    var docs = A.docs, cited = A._refs.list.length;
    if (cited) p.appendChild(h('p', { class: 'refjump' }, h('button', { type: 'button', class: 'chipbtn', id: 'toRefs', onclick: function () { gotoReference(0); } },
      window.AwrReport.referencesTitle + ' (' + cited + ')')));
    if (!docs.length) { p.appendChild(h('div', { class: 'empty-note', text: 'No documentation catalog is embedded in this analysis.' })); return; }
    var books = {}, tags = {};
    docs.forEach(function (d) { if (d.book) books[d.book] = (books[d.book] || 0) + 1; (d.tags || []).forEach(function (t) { tags[t] = (tags[t] || 0) + 1; }); });
    var q = h('input', { type: 'search', placeholder: 'Search documentation (title, summary, tags)\u2026', 'aria-label': 'Search documentation', id: 'dq' });
    var bsel = h('select', { 'aria-label': 'Filter by book', id: 'dbook' }, h('option', { value: '', text: 'All books' }));
    Object.keys(books).sort().forEach(function (b) { bsel.appendChild(h('option', { value: b, text: b + ' (' + books[b] + ')' })); });
    var chips = h('div', { class: 'toolrow', role: 'group', 'aria-label': 'Filter by tag' });
    Object.keys(tags).sort(function (a, b) { return tags[b] - tags[a] || (a < b ? -1 : 1); }).slice(0, 24).forEach(function (t) {
      chips.appendChild(h('button', { type: 'button', class: 'filter', 'data-tag': t, 'aria-pressed': 'false', style: '--sev:var(--accent)', onclick: function (e) {
        if (st.dtags[t]) delete st.dtags[t]; else st.dtags[t] = true;
        e.currentTarget.setAttribute('aria-pressed', st.dtags[t] ? 'true' : 'false'); apply();
      } }, t, h('span', { class: 'n', text: String(tags[t]) })));
    });
    var count = h('p', { class: 'meta', role: 'status', 'aria-live': 'polite', style: 'margin:8px 0 0;color:var(--muted);font-size:12px' });
    var list = h('div', { class: 'doclist' });
    p.appendChild(h('div', { class: 'toolrow' }, q, bsel)); p.appendChild(h('div', { style: 'margin-top:8px' }, chips)); p.appendChild(count); p.appendChild(list);
    p.appendChild(h('p', { style: 'margin-top:14px;color:var(--muted);font-size:12px', text: 'This application never fetches links itself. ' + (MODE === 'desktop' ? 'Selecting a link opens it in your default browser; “Copy link” copies it instead.' : 'Selecting a link opens it in a new browser tab.') }));
    function row(d) {
      var title = d.local ? h('h4', { text: d.title }) : h('h4', null, docAnchor(d.title, d.url, '') || d.title);
      var meta = [d.book, d.section ? 'Section: ' + d.section : '', (d.releases || []).join(', ')].filter(Boolean).join('  \u00b7  ');
      var r = h('div', { class: 'row' });
      (d.tags || []).forEach(function (t) { r.appendChild(h('span', { class: 'tag', text: t })); });
      if (d.local) r.appendChild(h('span', { class: 'localref', text: 'Local reference (on the database host): ' + d.local }));
      else if (safeUrl(d.url)) r.appendChild(copyBtn(function () { return d.url; }, 'Copy link'));
      return h('article', { class: 'doc' }, title, meta ? h('div', { class: 'meta', text: meta }) : null, d.summary ? h('p', { class: 'sum', text: d.summary }) : null, r);
    }
    /* "Relevant to this report": docs linked from findings (most severe first), then docs of the tables present in this report */
    var relRank = {}, relN = 0;
    function addRel(id) { if (id && docsById[id] && relRank[id] === undefined) relRank[id] = relN++; }
    (A.findings || []).forEach(function (f) { (f.docs || []).forEach(function (x) { var m = catalogByUrl(x.url); if (m) addRel(m.id); }); });
    Object.keys(A.sectionDocs || {}).forEach(function (tid) { if (tablesById[tid] || !Object.keys(tablesById).length) (A.sectionDocs[tid] || []).forEach(addRel); });
    var BOOK_FIRST = ['Performance Tuning Guide', 'Database Reference'];
    function bookRank(b) { var i = BOOK_FIRST.indexOf(b); return i < 0 ? BOOK_FIRST.length : i; }
    function apply() {
      clear(list);
      var qs = q.value.trim().toLowerCase().split(/\s+/).filter(Boolean), tg = Object.keys(st.dtags), b = bsel.value, last = null;
      var sel = docs.filter(function (d) {
        if (b && d.book !== b) return false;
        for (var i = 0; i < tg.length; i++) if ((d.tags || []).indexOf(tg[i]) < 0) return false;
        if (qs.length) { var s = (d.title + ' ' + (d.summary || '') + ' ' + (d.tags || []).join(' ') + ' ' + (d.book || '')).toLowerCase(); for (var j = 0; j < qs.length; j++) if (s.indexOf(qs[j]) < 0) return false; }
        return true;
      });
      sel.sort(function (x, y) {
        var rx = relRank[x.id], ry = relRank[y.id];
        if (rx !== undefined || ry !== undefined) return rx === undefined ? 1 : ry === undefined ? -1 : rx - ry;
        var bx = bookRank(x.book), by = bookRank(y.book);
        if (bx !== by) return bx - by;
        return (x.book || '') < (y.book || '') ? -1 : (x.book || '') > (y.book || '') ? 1 : 0;
      });
      var LIM = 150;
      sel.slice(0, LIM).forEach(function (d) {
        var grp = relRank[d.id] !== undefined ? 'Relevant to this report' : (d.book || 'Other');
        if (grp !== last) { last = grp; list.appendChild(h('h3', { class: 'doc-group', text: grp })); }
        list.appendChild(row(d));
      });
      count.textContent = sel.length + ' of ' + docs.length + ' entries' + (sel.length > LIM ? ' (showing the first ' + LIM + ', refine the search)' : '');
    }
    q.addEventListener('input', apply); bsel.addEventListener('change', apply);
    apply();
  }

  function section(title, node) { return h('div', null, h('h4', { text: title }), node); }
  function fcard(f, i) {
    var open = st.open[f.id] !== undefined ? st.open[f.id] : (f.severity === 'Critical' || f.severity === 'High');
    var imp = Math.max(0, Math.min(100, +f.impact || 0));
    var tech = f.technical ? h('div', { class: 'tech', hidden: true }, h('h4', { text: 'Technical details' }), h('p', { text: f.technical })) : null;
    var head = h('button', { type: 'button', class: 'fhead', 'aria-expanded': open ? 'true' : 'false', 'aria-controls': 'fb-' + f.id, id: 'fh-' + f.id, onclick: function () { setOpen(card, f.id, !card.classList.contains('open')); } },
      h('div', { class: 'top' }, h('span', { class: 'badge', text: SEV_LABEL[f.severity] || f.severity }), f.category ? h('span', { class: 'cat', text: f.category }) : null,
        imp > 0 ? h('span', { class: 'impact', title: rtype() === 'ASH' ? 'Approximate share of active session time involved' : 'Approximate share of database time involved' }, h('i', { style: '--w:' + imp + '%' }), num(imp, 0) + '% of ' + timeLabel()) : null),
      h('span', { class: 'title', text: f.title }), (f.confidence || f.rootCauseGroup) ? h('span', { class: 'fmeta' }, f.confidence ? confBadge(f.confidence, f.dataQuality) : null, f.rootCauseGroup ? h('span', { class: 'rcg', title: 'Root cause group', text: f.rootCauseGroup }) : null) : null, (function () { var c = ico('chev'); c.setAttribute('class', 'chev'); return c; })());
    var inner = h('div', { class: 'finner' });
    inner.appendChild(h('p', { class: 'plain', text: f.plain }));
    if (f.dataQuality) inner.appendChild(h('p', { class: 'dqnote', text: 'Data quality: ' + f.dataQuality }));
    if (f.whyItMatters) inner.appendChild(h('div', { class: 'why' }, h('h4', { text: 'Why it matters' }), h('p', { text: f.whyItMatters })));
    if (tech) inner.appendChild(tech);
    if (f.evidence && f.evidence.length) {
      var tbody = h('tbody');
      f.evidence.forEach(function (e) { tbody.appendChild(h('tr', null, h('th', { scope: 'row', text: e.label }), h('td', null, h('b', { text: e.value }), e.note ? h('span', { class: 'note', text: e.note }) : null))); });
      inner.appendChild(section('Evidence from the report', h('table', { class: 'ev' }, tbody)));
    }
    if (f.recommendations && f.recommendations.length) inner.appendChild(section('What to do', h('ol', { class: 'recs' }, f.recommendations.map(function (r) { return h('li', { text: r }); }))));
    if (f.sql && f.sql.length) {
      var sb = h('div');
      f.sql.forEach(function (s) {
        sb.appendChild(h('div', { class: 'sqlblock' }, h('div', { class: 'sqlhead' }, h('span', { text: s.title || 'SQL' }), copyBtn(function () { return s.code; }, 'Copy')),
          h('pre', { tabindex: '0', 'aria-label': 'SQL: ' + (s.title || '') }, h('code', { text: s.code })), s.caution ? h('div', { class: 'caution', text: s.caution }) : null));
      });
      inner.appendChild(section('SQL to run', sb));
    }
    if (f.docs && f.docs.length) {
      var lk = h('div', { class: 'links' });
      f.docs.forEach(function (d) {
        var cat = catalogByUrl(d.url), r = A._refs.byUrl[d.url];
        var n = docLink(d.title, d.url, cat && cat.summary) || h('span', { class: 'tag', title: cat && cat.local ? cat.local : '', text: d.title || d.url });
        var mark = r ? h('button', { type: 'button', class: 'refbtn', title: 'Show reference ' + r.n + ' in the list of referenced documentation', 'aria-label': 'Reference ' + r.n + ' in the list of referenced documentation',
          onclick: function () { gotoReference(r.n); } }, '[' + r.n + ']') : null;
        lk.appendChild(h('span', { class: 'docref' }, mark, n));
      });
      inner.appendChild(section('Oracle documentation', lk));
    }
    if (f.sourceViews && f.sourceViews.length) inner.appendChild(section('Data comes from', h('div', { class: 'links' }, f.sourceViews.map(function (v) { return h('span', { class: 'tag', text: v }); }))));
    var rel = (f.relatedIds || []).map(function (id) { return A.findings.filter(function (x) { return x.id === id; })[0]; }).filter(Boolean);
    if (rel.length) inner.appendChild(section('Related findings', h('div', { class: 'relchips' }, rel.map(function (r) {
      return h('button', { type: 'button', class: 'chipbtn', title: 'Jump to this finding', onclick: function () { gotoFinding(r.id); } }, r.title);
    }))));
    var acts = h('div', { class: 'actions' });
    var tb = h('button', { type: 'button', class: 'linkbtn', 'aria-pressed': 'false' }, 'Technical details');
    function applyTech(on) { if (tech) tech.hidden = !on; tb.setAttribute('aria-pressed', on ? 'true' : 'false'); tb.textContent = on ? 'Hide technical details' : 'Technical details'; }
    if (tech) { tb.addEventListener('click', function () { applyTech(tech.hidden); }); acts.appendChild(tb); st.techBtns.push(applyTech); }
    (f.tableRefs || []).forEach(function (id) {
      var t = tablesById[id]; if (!t) return;
      acts.appendChild(h('button', { type: 'button', class: 'linkbtn', title: 'Scroll the original report to this table', onclick: function () { showTable(id); } }, ico('table'), 'Show in report: ' + (t.title.length > 34 ? t.title.slice(0, 33) + '\u2026' : t.title)));
    });
    (f.chartRefs || []).forEach(function (id) {
      var c = chartsById[id]; if (!c) return;
      acts.appendChild(h('button', { type: 'button', class: 'linkbtn', onclick: function () { showChart(id); } }, ico('chart'), 'Show chart: ' + (c.title.length > 34 ? c.title.slice(0, 33) + '\u2026' : c.title)));
    });
    if (acts.childNodes.length) inner.appendChild(acts);
    var card = h('article', { class: 'fcard sev-' + f.severity + (open ? ' open' : ''), id: 'f-' + f.id, style: '--i:' + Math.min(i, 12) },
      h('h3', { style: 'font-size:inherit;margin:0' }, head), h('div', { class: 'fbody', id: 'fb-' + f.id, role: 'region', 'aria-labelledby': 'fh-' + f.id }, h('div', null, inner)));
    return card;
  }

  /* ================= grids ================= */
  function sortRows(rows, idx, dir, isNum) {
    var out = rows.slice();
    out.sort(function (a, b) {
      var x = a[idx], y = b[idx];
      if (isNum) { x = x === null || x === undefined ? -Infinity : x; y = y === null || y === undefined ? -Infinity : y; return dir * (x - y); }
      return dir * String(x == null ? '' : x).localeCompare(String(y == null ? '' : y), undefined, { numeric: true, sensitivity: 'base' });
    });
    return out;
  }
  /* Pager: first / previous / next / last plus a "per page" choice. onChange(page, size) is called after a user action.
     set(total, page, size) refreshes the status text and the disabled state and returns {start, end, pages, page}. */
  function makePager(sizes, size, noun, onChange) {
    var status = h('span', { class: 'pgstatus', 'aria-live': 'polite' }), state = { page: 0, size: size };
    function btn(label, title, go) { return h('button', { type: 'button', class: 'pgbtn', title: title, 'aria-label': title, text: label, onclick: function () { state.page = go(); onChange(state.page, state.size); } }); }
    var b = { first: btn('\u00ab', 'First page', function () { return 0; }), prev: btn('\u2039', 'Previous page', function () { return state.page - 1; }),
              next: btn('\u203a', 'Next page', function () { return state.page + 1; }), last: btn('\u00bb', 'Last page', function () { return 1e9; }) };
    var sel = h('select', { 'aria-label': noun + ' per page', onchange: function () { state.size = parseInt(sel.value, 10) || size; state.page = 0; onChange(state.page, state.size); } });
    sizes.forEach(function (n) { sel.appendChild(h('option', { value: String(n), text: n + ' ' + (n === 1 ? noun.replace(/s$/, '') : noun), selected: n === size })); });
    var el = h('div', { class: 'pager', role: 'navigation', 'aria-label': 'Pages' }, status, h('span', { class: 'pgctl' }, b.first, b.prev, b.next, b.last, sel));
    return {
      el: el, state: state,
      set: function (total, page, sz) {
        state.size = sz; var pages = Math.max(1, Math.ceil(total / sz));
        state.page = Math.min(Math.max(page, 0), pages - 1);
        var start = state.page * sz, end = Math.min(total, start + sz);
        status.textContent = total ? noun.charAt(0).toUpperCase() + noun.slice(1) + ' ' + (start + 1) + '\u2013' + end + ' of ' + total + '  \u00b7  page ' + (state.page + 1) + ' of ' + pages : 'Nothing to show';
        b.first.disabled = b.prev.disabled = state.page <= 0; b.next.disabled = b.last.disabled = state.page >= pages - 1;
        el.hidden = total <= sizes[0];
        return { start: start, end: end, pages: pages, page: state.page };
      }
    };
  }
  /* cols: [{label, num, fmt(v,row), idx}]; rows: arrays. opts: {limit, pageSize, onPageSize(n), onRow(tr,row), cls}.
     With pageSize the grid shows that many rows at a time with a pager underneath (first/previous/next/last and a rows-per-page choice). */
  function makeGrid(cols, rows, opts) {
    opts = opts || {};
    var state = { idx: opts.sortIdx === undefined ? -1 : opts.sortIdx, dir: opts.sortDir || -1, page: 0, size: opts.pageSize || 0 };
    var wrap = h('div', { class: 'gridwrap' + (opts.cls ? ' ' + opts.cls : '') });
    var tbl = h('table', { class: 'grid' }), thead = h('thead'), tbody = h('tbody'), trh = h('tr');
    var ths = cols.map(function (c, ci) {
      var th = h('th', { scope: 'col', class: c.num ? 'num' : '', 'aria-sort': 'none' }, h('button', { type: 'button', title: 'Sort by ' + c.label, text: c.label }));
      th.querySelector('button').addEventListener('click', function () {
        if (state.idx === ci) state.dir = -state.dir; else { state.idx = ci; state.dir = c.num ? -1 : 1; }
        state.page = 0; paint();
      });
      trh.appendChild(th); return th;
    });
    thead.appendChild(trh); tbl.appendChild(thead); tbl.appendChild(tbody); wrap.appendChild(tbl);
    var note = h('div', { class: 'muted', style: 'font-size:12px;padding:6px 10px', hidden: true });
    var pager = null;
    if (state.size) pager = makePager([10, 25, 50, 100], state.size, 'rows', function (pg, sz) { state.page = pg; state.size = sz; if (opts.onPageSize) opts.onPageSize(sz); paint(); });
    function paint() {
      ths.forEach(function (th, ci) { th.setAttribute('aria-sort', state.idx === ci ? (state.dir > 0 ? 'ascending' : 'descending') : 'none'); });
      var data = state.idx >= 0 ? sortRows(rows, cols[state.idx].idx === undefined ? state.idx : cols[state.idx].idx, state.dir, !!cols[state.idx].num) : rows;
      var lim = state.size || opts.limit || 400, pages = state.size ? Math.max(1, Math.ceil(data.length / state.size)) : 1;
      if (state.page > pages - 1) state.page = pages - 1;
      if (state.page < 0) state.page = 0;
      var start = state.size ? state.page * state.size : 0, show = data.slice(start, start + lim);
      clear(tbody);
      show.forEach(function (r) {
        var tr = h('tr');
        cols.forEach(function (c, ci) {
          var v = r[c.idx === undefined ? ci : c.idx];
          var td = h('td', { class: c.num ? 'num' : (c.cls || '') });
          var out = c.fmt ? c.fmt(v, r) : (v == null ? '' : String(v));
          if (out && out.nodeType) td.appendChild(out); else { td.textContent = out; if (!c.num && String(out).length > 40) td.title = String(out); }
          tr.appendChild(td);
        });
        if (opts.onRow) opts.onRow(tr, r, tbody, cols.length);
        tbody.appendChild(tr);
      });
      if (pager) {
        pager.set(data.length, state.page, state.size);
        wrap.scrollTop = 0;
        note.hidden = true;
      } else { note.hidden = data.length <= lim; note.textContent = 'Showing the first ' + lim + ' of ' + data.length + ' rows.'; }
    }
    paint();
    return h('div', { class: opts.fit ? 'gridbox' : null }, wrap, pager ? pager.el : null, note);
  }

  /* ================= SQL tab ================= */
  function buildSqlTab() {
    var p = clear($('panel-sql'));
    p.classList.toggle('fit', A.topSql.length > 0);
    if (!A.topSql.length) { p.appendChild(h('div', { class: 'empty-note', text: 'No SQL statements were found in this report.' })); return; }
    var q = h('input', { type: 'search', placeholder: 'Filter by SQL ID, module or text\u2026', 'aria-label': 'Filter SQL' });
    var wrapHost = h('div', { class: 'fitbody' }), sqlPageSize = 25;
    p.appendChild(h('div', { class: 'toolrow', style: 'margin-bottom:10px' }, h('label', { class: 'search' }, ico('search'), q),
      h('span', { class: 'muted', style: 'font-size:12px', text: 'Click a row to see the full SQL text. Click a column header to sort.' })));
    var maxPct = Math.max.apply(null, A.topSql.map(function (s) { return s.pctDbTime || 0; }).concat([1]));
    var cols = [
      { label: 'SQL ID', idx: 0 }, { label: 'Module', idx: 1, cls: 'mod' },
      { label: rtype() === 'ASH' ? '% active session time' : '% DB time', num: true, idx: 2, fmt: function (v) { return v == null ? '' : h('span', { class: 'bar-in', style: '--w:' + Math.min(100, v / maxPct * 100) + '%' }, num(v, 1)); } },
      { label: 'Elapsed (s)', num: true, idx: 3, fmt: function (v) { return num(v, 1); } },
      { label: 'CPU (s)', num: true, idx: 4, fmt: function (v) { return num(v, 1); } },
      { label: 'I/O wait (s)', num: true, idx: 5, fmt: function (v) { return num(v, 1); } },
      { label: 'Executions', num: true, idx: 6, fmt: function (v) { return num(v, 0); } },
      { label: 'Sec / exec', num: true, idx: 7, fmt: function (v) { return num(v, 3); } },
      { label: 'Buffer gets', num: true, idx: 8, fmt: function (v) { return num(v, 0); } },
      { label: 'Disk reads', num: true, idx: 9, fmt: function (v) { return num(v, 0); } },
      { label: '% CPU', num: true, idx: 10, fmt: function (v) { return num(v, 1); } },
      { label: '% I/O', num: true, idx: 11, fmt: function (v) { return num(v, 1); } },
      { label: 'Parses', num: true, idx: 12, fmt: function (v) { return num(v, 0); } },
      { label: 'Rows', num: true, idx: 13, fmt: function (v) { return num(v, 0); } },
      { label: 'Versions', num: true, idx: 14, fmt: function (v) { return num(v, 0); } },
      { label: 'SQL text', idx: 15, cls: 'txt', fmt: function (v) { return String(v || '').replace(/\s+/g, ' ').slice(0, 90); } },
      { label: 'Seen in', num: true, idx: 16, fmt: function (v) { return num(v, 0); } }
    ];
    function toRow(s) { return [s.sqlId, s.module, s.pctDbTime, s.elapsedSec, s.cpuSec, s.ioWaitSec, s.executions, s.elapsedPerExecSec, s.bufferGets, s.physicalReads, s.pctCpu, s.pctIo, s.parseCalls, s.rowsProcessed, s.versionCount, s.text, (s.appearsIn || []).length, s.appearsIn || []]; }
    /* "Seen in" sorts by list length: store the length in a parallel field */
    var all = A.topSql.map(toRow);
    function render() {
      var f = q.value.trim().toLowerCase();
      var rows = all.filter(function (r) { return !f || (r[0] + ' ' + r[1] + ' ' + r[15]).toLowerCase().indexOf(f) >= 0; });
      clear(wrapHost);
      var g = makeGrid(cols, rows, {
        sortIdx: 2, sortDir: -1, cls: 'sqlgrid', fit: true, pageSize: sqlPageSize, onPageSize: function (n) { sqlPageSize = n; },
        onRow: function (tr, r, tbody, ncols) {
          tr.className = 'sqlrow'; tr.tabIndex = 0; tr.setAttribute('aria-expanded', 'false'); tr.setAttribute('role', 'button');
          var detail = null;
          function toggle() {
            if (detail) { tbody.removeChild(detail); detail = null; tr.setAttribute('aria-expanded', 'false'); return; }
            var gw = tr.closest('.gridwrap'); detail = h('tr', { class: 'sqldetail' }, h('td', { colspan: String(ncols) }, h('div', { class: 'in', style: 'position:sticky;left:0;width:' + Math.max(300, (gw ? gw.clientWidth : 600) - 2) + 'px' },
              h('div', { class: 'actions' }, copyBtn(function () { return r[15]; }, 'Copy SQL'), r[17] && r[17].length ? h('span', { class: 'muted', text: 'Appears in: ' + r[17].join(' \u00b7 ') }) : null),
              h('pre', { class: 'code', tabindex: '0' }, h('code', { text: r[15] || '(no text captured)' })))));
            tbody.insertBefore(detail, tr.nextSibling); tr.setAttribute('aria-expanded', 'true');
          }
          tr.addEventListener('click', toggle);
          tr.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
        }
      });
      wrapHost.appendChild(g);
    }
    var t = null; q.addEventListener('input', function () { clearTimeout(t); t = setTimeout(render, 120); });
    p.appendChild(wrapHost); render();
  }

  /* ================= script tab ================= */
  function buildScript() {
    var p = clear($('panel-script')), m = A.meta, lines = [], n = 0, fcount = 0, groups = [], head = [];
    lines.push('-- Apodofos Report Analyzer: suggested diagnostic and tuning statements');
    lines.push('-- Database: ' + (m.dbName || '?') + '   Instance: ' + (m.instance || '?') + '   Report window: ' + (m.beginTime || '?') + ' to ' + (m.endTime || '?'));
    lines.push('-- Generated: ' + (m.analyzedAt || ''));
    lines.push('-- IMPORTANT: nothing here has been run. Review every statement, test outside production first,');
    lines.push('-- and check the CAUTION notes. Some statements need extra licences (Diagnostics / Tuning Pack).');
    lines.push('');
    head = lines.slice();
    A.findings.forEach(function (f) {
      if (!f.sql || !f.sql.length) return;
      fcount++;
      var g0 = lines.length;
      lines.push('-- ' + new Array(70).join('='));
      lines.push('-- [' + f.id + '] ' + (SEV_LABEL[f.severity] || f.severity) + ': ' + f.title);
      lines.push('-- ' + new Array(70).join('='));
      f.sql.forEach(function (s) {
        n++;
        lines.push('-- ' + (s.title || 'Statement'));
        if (s.caution) lines.push('-- CAUTION: ' + s.caution);
        lines.push(s.code.replace(/\s+$/, ''));
        lines.push('');
      });
      groups.push(lines.slice(g0));
    });
    var text = lines.join('\n'); st.scriptText = text;
    p.classList.toggle('fit', n > 0);
    if (!n) { p.appendChild(h('div', { class: 'empty-note', text: 'No findings include SQL statements for this report.' })); return; }
    var fname = (m.dbName || 'awr') + '-recommended.sql';
    p.appendChild(h('div', { class: 'scriptbar' },
      copyBtn(function () { return st.scriptText; }, 'Copy script', 'btn primary'),
      h('button', { type: 'button', class: 'btn', onclick: function () { if (!host({ type: 'exportScript', text: st.scriptText })) { download(fname, st.scriptText); toast('Script downloaded'); } } }, ico('download'), h('span', { text: MODE === 'desktop' ? 'Save script\u2026' : 'Download .sql' })),
      h('span', { class: 'muted', text: n + ' statement(s) from ' + fcount + ' finding(s), grouped by finding.' })));
    /* the on-screen script is shown a few findings at a time (copy and download always use the whole script) */
    var code = h('code'), pre = h('pre', { class: 'code scriptpre', tabindex: '0', 'aria-label': 'Combined SQL script' }, code);
    var pg = makePager([1, 3, 5, 10], 3, 'findings', function (page, size) { showPage(page, size); });
    function addLines(arr) {
      arr.forEach(function (l) {
        var cls = /^-- =+$/.test(l) ? 'cm' : /^-- \[/.test(l) ? 'hd' : /^--/.test(l) ? 'cm' : '';
        code.appendChild(cls ? h('span', { class: cls, text: l }) : document.createTextNode(l));
        code.appendChild(document.createTextNode('\n'));
      });
    }
    function showPage(page, size) {
      var r = pg.set(groups.length, page, size);
      clear(code);
      if (r.page === 0) addLines(head);
      groups.slice(r.start, r.end).forEach(addLines);
      pre.scrollTop = 0;
    }
    p.appendChild(pg.el); p.appendChild(pre); showPage(0, 3);
  }

  /* ================= charts tab ================= */
  function trackChart(el, spec, opts) {
    chartInstances = chartInstances.filter(function (x) { if (x.el.isConnected) return true; try { x.destroy(); } catch (e) { /* ignore */ } return false; });
    var c = window.AwrCharts.render(el, spec, opts); chartInstances.push(c); return c; }
  function ensureCharts() {
    var p = $('panel-charts');
    if (!st.chartsBuilt) {
      st.chartsBuilt = true;
      var seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Chart view' },
        h('button', { type: 'button', id: 'cvCur', 'aria-pressed': 'true', text: 'Key charts', onclick: function () { setChartsView('curated'); } }),
        h('button', { type: 'button', id: 'cvAll', 'aria-pressed': 'false', text: 'All metrics explorer', onclick: function () { setChartsView('explorer'); } }));
      clear(p); p.appendChild(h('div', { class: 'subnav' }, seg));
      p.appendChild(h('div', { id: 'curated' })); p.appendChild(h('div', { id: 'explorer', class: 'explorer', hidden: true }));
      buildCurated();
    }
    setChartsView(st.chartsView);
  }
  function setChartsView(v) {
    st.chartsView = v;
    $('cvCur').setAttribute('aria-pressed', v === 'curated' ? 'true' : 'false'); $('cvAll').setAttribute('aria-pressed', v === 'explorer' ? 'true' : 'false');
    $('curated').hidden = v !== 'curated'; $('explorer').hidden = v !== 'explorer';
    if (v === 'explorer' && !st.explorerBuilt) { st.explorerBuilt = true; buildExplorer(); }
    nextFrame(function () { chartInstances.forEach(function (c) { if (c.el.clientWidth && Math.abs(c.el.clientWidth - c.lastW) > 2) c.draw(); }); });
  }
  function chartPositive(c) { return c.series.every(function (s) { return (s.values || []).every(function (v) { return v === null || v === undefined || v > 0; }); }); }
  function chartCard(c) {
    var box = h('div', { class: 'cbox' }), inst = null, dataBox = null;
    var foot = h('div', { class: 'cfoot' });
    var spec = JSON.parse(JSON.stringify(c)); spec.title = c.title;
    var card = h('article', { class: 'ccard' + (c.type === 'line' || (c.labels || []).length > 14 ? ' wide' : ''), 'data-chart': c.id },
      h('div', { class: 'chead' }, h('div', null, h('h3', { text: c.title }), c.subtitle ? h('div', { class: 'csub', text: c.subtitle }) : null)),
      c.explain ? h('p', { class: 'explain', text: c.explain }) : null, box, foot);
    if (c.tableRef && tablesById[c.tableRef]) foot.appendChild(h('button', { type: 'button', class: 'linkbtn', onclick: function () { showTable(c.tableRef); } }, ico('table'), 'Show source table in report'));
    if (c.type !== 'donut' && c.type !== 'stacked' && chartPositive(c)) {
      var lb = h('button', { type: 'button', class: 'linkbtn', 'aria-pressed': c.logScale ? 'true' : 'false', text: 'Log scale' });
      lb.addEventListener('click', function () { spec.logScale = !spec.logScale; lb.setAttribute('aria-pressed', spec.logScale ? 'true' : 'false'); inst.update(spec, true); });
      foot.appendChild(lb);
    }
    var db = h('button', { type: 'button', class: 'linkbtn', 'aria-expanded': 'false', text: 'Show data' });
    db.addEventListener('click', function () {
      if (dataBox) { card.removeChild(dataBox); dataBox = null; db.textContent = 'Show data'; db.setAttribute('aria-expanded', 'false'); return; }
      var cols = [{ label: 'Label', idx: 0 }].concat(c.series.map(function (s, i) { return { label: s.name || ('Series ' + (i + 1)), num: true, idx: i + 1, fmt: function (v) { return num(v, 3); } }; }));
      var rows = c.labels.map(function (l, i) { return [l].concat(c.series.map(function (s) { return s.values[i]; })); });
      dataBox = h('div', { style: 'margin-top:10px' }, makeGrid(cols, rows, { limit: 200 })); card.appendChild(dataBox);
      db.textContent = 'Hide data'; db.setAttribute('aria-expanded', 'true');
    });
    foot.appendChild(db);
    card._mount = function () { inst = trackChart(box, spec, {}); };
    return card;
  }
  function buildCurated() {
    var host_ = clear($('curated'));
    if (!A.charts.length) { host_.appendChild(h('div', { class: 'empty-note', text: 'No curated charts for this report. Try the All metrics explorer.' })); return; }
    var groups = {}, order = [];
    A.charts.forEach(function (c) { var g = c.group || 'Other'; if (!groups[g]) { groups[g] = []; order.push(g); } groups[g].push(c); });
    var nav = h('div', { class: 'groupnav', role: 'navigation', 'aria-label': 'Chart groups' });
    var body = h('div');
    var cards = [];
    order.forEach(function (g, gi) {
      var hid = 'cg-' + gi;
      nav.appendChild(h('button', { type: 'button', class: 'pill', 'aria-pressed': 'false', text: g + ' (' + groups[g].length + ')', onclick: function () { var t = $(hid); if (t) t.scrollIntoView({ behavior: 'smooth', block: 'start' }); } }));
      body.appendChild(h('h2', { class: 'group-h', id: hid, text: g }));
      var grid = h('div', { class: 'charts' });
      groups[g].forEach(function (c) { var cc = chartCard(c); cards.push(cc); grid.appendChild(cc); });
      body.appendChild(grid);
    });
    host_.appendChild(nav); host_.appendChild(body);
    /* render charts as they scroll into view; fall back to rendering everything */
    if ('IntersectionObserver' in window) {
      var io = new IntersectionObserver(function (es) { es.forEach(function (e) { if (e.isIntersecting) { io.unobserve(e.target); if (e.target._mount) { e.target._mount(); e.target._mount = null; } } }); }, { root: $('panel-charts'), rootMargin: '300px' });
      cards.forEach(function (c) { io.observe(c); });
      cards.forEach(function (c) { c._io = io; });
    } else cards.forEach(function (c) { c._mount(); });
  }

  /* ---- All-metrics explorer ---- */
  function parseNum(s) {
    if (s === null || s === undefined) return null;
    var t = String(s).replace(/\u00a0/g, ' ').trim();
    if (!t) return null;
    t = t.replace(/,/g, '').replace(/%$/, '');
    return /^[-+]?(\d+\.?\d*|\.\d+)(e[-+]?\d+)?$/i.test(t) ? parseFloat(t) : null;
  }
  var ID_LIKE = /^(snap|snap id|id|#|rank|no\.?|sql id|sqlid|sid|serial#?|instance|inst|begin snap|end snap|obj#|object id|dbid|child number|plan hash value|hash value)$/i;
  function analyzeTable(t) {
    var nC = t.headers.length, rows = t.rows.slice(0, 2000), info = { cols: [], numeric: [], labelCol: 0 };
    for (var c = 0; c < nC; c++) {
      var ne = 0, nn = 0, distinct = {};
      for (var r = 0; r < rows.length; r++) {
        var v = rows[r][c]; if (v === undefined || v === null || String(v).trim() === '') continue;
        ne++; distinct[v] = 1; if (parseNum(v) !== null) nn++;
      }
      info.cols.push({ nonEmpty: ne, isNum: ne > 0 && nn / ne >= 0.6, distinct: Object.keys(distinct).length });
    }
    var firstText = -1;
    for (var c2 = 0; c2 < nC; c2++) if (!info.cols[c2].isNum && info.cols[c2].nonEmpty && firstText < 0) {
      if (info.cols[c2].distinct >= 0.5 * info.cols[c2].nonEmpty) { firstText = c2; break; }
    }
    if (firstText < 0) for (var c3 = 0; c3 < nC; c3++) if (!info.cols[c3].isNum && info.cols[c3].nonEmpty) { firstText = c3; break; }
    info.labelCol = firstText >= 0 ? firstText : 0;
    for (var c4 = 0; c4 < nC; c4++) if (c4 !== info.labelCol && info.cols[c4].isNum) info.numeric.push(c4);
    return info;
  }
  function looksTemporal(labels) {
    var n = 0; labels.forEach(function (l) { if (/\d{1,2}[-\/ ][A-Za-z]{3}|\d{4}-\d\d-\d\d|\d\d:\d\d/.test(String(l))) n++; });
    return labels.length >= 3 && n / labels.length > 0.6;
  }
  function buildExplorer() {
    var root = clear($('explorer'));
    if (!A.tables.length) { root.appendChild(h('div', { class: 'empty-note', text: 'No tables were extracted from this report.' })); return; }
    root.appendChild(h('p', { class: 'muted', style: 'margin:0 0 12px;font-size:13px', text: 'Pick any table from the report and it is charted automatically. Change the label column, the numbers to plot or the chart type if you like.' }));
    var infos = {};
    function hdr(t, i) { var x = t.headers[i]; x = x == null ? '' : String(x).trim(); return x || (i === 0 ? 'Item' : 'Column ' + (i + 1)); }
    function info(t) { return infos[t.id] || (infos[t.id] = analyzeTable(t)); }
    var tsearch = h('input', { type: 'search', placeholder: 'Find a table\u2026', 'aria-label': 'Find a table' });
    var tsel = h('select', { 'aria-label': 'Table' });
    function fillTables() {
      var f = tsearch.value.trim().toLowerCase(), cur = tsel.value; clear(tsel);
      var groups = {}, order = [], first = null;
      A.tables.forEach(function (t) {
        if (f && (t.title + ' ' + (t.section || '')).toLowerCase().indexOf(f) < 0) return;
        var g = t.section || 'Report header';
        if (!groups[g]) { groups[g] = h('optgroup', { label: g }); order.push(g); }
        groups[g].appendChild(h('option', { value: t.id, text: t.title + '  (' + t.rows.length + ' rows)' })); if (!first) first = t.id;
      });
      order.forEach(function (g) { tsel.appendChild(groups[g]); });
      if (!order.length) tsel.appendChild(h('option', { value: '', text: 'No table matches' }));
      else if (!cur && !f) { var good = A.tables.filter(function (t) { return t.rows.length >= 5 && info(t).numeric.length; })[0]; tsel.value = good ? good.id : first; }
      else tsel.value = (cur && tsel.querySelector('option[value="' + cur.replace(/"/g, '') + '"]')) ? cur : first;
    }
    var lsel = h('select', { 'aria-label': 'Label column' });
    var typeSel = h('select', { 'aria-label': 'Chart type' },
      h('option', { value: 'auto', text: 'Auto' }), h('option', { value: 'hbar', text: 'Horizontal bars' }), h('option', { value: 'bar', text: 'Columns' }), h('option', { value: 'line', text: 'Line' }), h('option', { value: 'donut', text: 'Donut' }));
    var topSel = h('select', { 'aria-label': 'Rows to chart' }, [10, 15, 25, 40, 60].map(function (n) { return h('option', { value: String(n), text: 'Top ' + n }); }));
    topSel.value = '15';
    var sortSel = h('select', { 'aria-label': 'Order' }, h('option', { value: 'desc', text: 'Largest first' }), h('option', { value: 'asc', text: 'Smallest first' }), h('option', { value: 'report', text: 'Report order' }));
    var logCb = h('input', { type: 'checkbox', id: 'exlog' });
    var chips = h('div', { class: 'valchips', role: 'group', 'aria-label': 'Numbers to plot' });
    var field = function (label, ctl) { return h('label', { class: 'field' }, h('span', { text: label }), ctl); };
    root.appendChild(h('div', { class: 'controls' }, field('Find table', tsearch), field('Table', tsel), field('Label column', lsel), field('Chart', typeSel), field('Rows', topSel), field('Order', sortSel),
      h('label', { class: 'field', style: 'flex-direction:row;gap:6px;align-items:center;padding-bottom:6px' }, logCb, h('span', { text: 'Log scale' }))));
    root.appendChild(chips);
    var out = h('div');
    root.appendChild(out);
    var sel = { vals: [] };

    function curTable() { return tablesById[tsel.value]; }
    function onTable() {
      var t = curTable(); if (!t) { clear(out); return; }
      var inf = info(t);
      clear(lsel); t.headers.forEach(function (hd, i) { lsel.appendChild(h('option', { value: String(i), text: hdr(t, i) })); });
      lsel.value = String(inf.labelCol);
      var pref = inf.numeric.filter(function (i) { return !ID_LIKE.test((t.headers[i] || '').trim()); });
      sel.vals = (pref.length ? pref : inf.numeric).slice(0, 1);
      var temporal = looksTemporal(t.rows.map(function (r) { return r[inf.labelCol]; }));
      sortSel.value = temporal ? 'report' : 'desc';
      typeSel.value = 'auto'; logCb.checked = false;
      drawChips(); draw();
    }
    function drawChips() {
      var t = curTable(); clear(chips); if (!t) return;
      var inf = info(t), lc = +lsel.value;
      var cand = t.headers.map(function (x, i) { return i; }).filter(function (i) { return i !== lc && info(t).cols[i].isNum; });
      sel.vals = sel.vals.filter(function (i) { return cand.indexOf(i) >= 0; });
      if (!cand.length) { chips.appendChild(h('span', { class: 'muted', text: 'This table has no numeric columns to chart. The data is shown below.' })); return; }
      chips.appendChild(h('span', { class: 'muted', style: 'font-size:12px;align-self:center;margin-right:4px', text: 'Plot:' }));
      cand.forEach(function (i) {
        var cb = h('input', { type: 'checkbox', checked: sel.vals.indexOf(i) >= 0 });
        cb.addEventListener('change', function () {
          if (cb.checked) { sel.vals.push(i); if (sel.vals.length > 4) { sel.vals.shift(); } } else { sel.vals = sel.vals.filter(function (x) { return x !== i; }); if (!sel.vals.length) { sel.vals = [i]; } }
          drawChips(); draw();
        });
        chips.appendChild(h('label', null, cb, hdr(t, i)));
      });
    }
    function draw() {
      var t = curTable(); clear(out); if (!t) return;
      var lc = +lsel.value, vals = sel.vals.slice();
      out.appendChild(h('div', { class: 'tsum' }, h('strong', { text: t.title }), t.summary ? ' \u2014 ' + t.summary : '', ' ',
        h('button', { type: 'button', class: 'linkbtn', onclick: function () { showTable(t.id); } }, ico('table'), 'Show in report')));
      if (vals.length) {
        var topN = +topSel.value, order = sortSel.value, idx = t.rows.map(function (r, i) { return i; });
        var parsed = t.rows.map(function (r) { return vals.map(function (c) { return parseNum(r[c]); }); });
        var keep = idx.filter(function (i) { return t.rows[i][lc] !== undefined && String(t.rows[i][lc]).trim() !== '' && parsed[i].some(function (v) { return v !== null; }); });
        if (order !== 'report') keep.sort(function (a, b) { var x = parsed[a][0], y = parsed[b][0]; x = x === null ? -Infinity : x; y = y === null ? -Infinity : y; return order === 'desc' ? y - x : x - y; });
        keep = keep.slice(0, topN);
        var labels = keep.map(function (i) { return String(t.rows[i][lc]); });
        var type = typeSel.value;
        if (type === 'auto') {
          var sum = parsed.reduce(function (a, r) { return a + (r[0] || 0); }, 0);
          type = looksTemporal(labels) ? 'line' : (vals.length === 1 && keep.length >= 2 && keep.length <= 8 && /%|percent|pct/i.test(t.headers[vals[0]] || '') && sum > 95 && sum < 105 ? 'donut' : 'hbar');
        }
        var spec = { type: type, title: t.title, labels: labels, unit: '', logScale: logCb.checked && type !== 'donut',
          series: vals.map(function (c, k) { return { name: hdr(t, c), values: keep.map(function (i) { return parsed[i][k]; }) }; }) };
        var box = h('div', { class: 'ccard', style: 'margin-bottom:12px' }, h('h3', { text: spec.title }),
          h('div', { class: 'csub', text: 'Plotting ' + vals.map(function (c) { return hdr(t, c); }).join(', ') + ' by ' + (hdr(t, lc)) }), h('div', { class: 'cbox', style: 'margin-top:8px' }));
        out.appendChild(box);
        trackChart(box.querySelector('.cbox'), spec, { maxRows: topN });
      }
      var inf = info(t);
      var cols = t.headers.map(function (hd, i) { return { label: hdr(t, i), idx: i, num: inf.cols[i].isNum, fmt: inf.cols[i].isNum ? function (v) { var n = parseNum(v); return n === null ? String(v == null ? '' : v) : String(v).trim(); } : null }; });
      out.appendChild(h('div', { class: 'panel-title', text: 'Table data' }));
      out.appendChild(makeGrid(cols, t.rows, { limit: 300 }));
    }
    tsearch.addEventListener('input', function () { fillTables(); onTable(); });
    tsel.addEventListener('change', onTable);
    lsel.addEventListener('change', function () { var t = curTable(); sel.vals = []; var inf = info(t); sel.vals = inf.numeric.filter(function (i) { return i !== +lsel.value; }).slice(0, 1); drawChips(); draw(); });
    [typeSel, topSel, sortSel, logCb].forEach(function (c) { c.addEventListener('change', draw); });
    fillTables();
    /* start on the first table that has numeric data */
    var firstGood = A.tables.filter(function (t) { return t.rows.length >= 3 && info(t).numeric.length; })[0];
    if (firstGood) tsel.value = firstGood.id;
    onTable();
  }

  /* ================= layout: splitter, collapse, drop ================= */
  function initLayout() {
    var sp = $('split'), bar = $('splitter');
    function setSplit(pct, save) {
      pct = Math.max(15, Math.min(85, pct));
      sp.style.setProperty('--split', pct + '%'); bar.setAttribute('aria-valuenow', String(Math.round(pct)));
      if (save) { try { localStorage.setItem('awr.split', String(pct)); } catch (e) { /* ignore */ } }
    }
    var saved = 50; try { saved = parseFloat(localStorage.getItem('awr.split')) || 50; } catch (e) { saved = 50; }
    setSplit(saved);
    bar.addEventListener('pointerdown', function (e) {
      e.preventDefault(); bar.setPointerCapture(e.pointerId); bar.classList.add('drag'); document.body.classList.add('dragging');
    });
    bar.addEventListener('pointermove', function (e) {
      if (!bar.hasPointerCapture(e.pointerId)) return;
      var r = sp.getBoundingClientRect(), pad = 10;
      setSplit((e.clientX - r.left - pad) / (r.width - pad * 2) * 100, false);
    });
    function end(e) { if (bar.hasPointerCapture(e.pointerId)) bar.releasePointerCapture(e.pointerId); bar.classList.remove('drag'); document.body.classList.remove('dragging'); setSplit(parseFloat(bar.getAttribute('aria-valuenow')), true); }
    bar.addEventListener('pointerup', end); bar.addEventListener('pointercancel', end);
    bar.addEventListener('dblclick', function () { setSplit(50, true); });
    bar.addEventListener('keydown', function (e) {
      var v = parseFloat(bar.getAttribute('aria-valuenow'));
      if (e.key === 'ArrowLeft') { setSplit(v - 3, true); e.preventDefault(); }
      else if (e.key === 'ArrowRight') { setSplit(v + 3, true); e.preventDefault(); }
      else if (e.key === 'Home') { setSplit(50, true); e.preventDefault(); }
    });
    function collapse(which) {
      var cls = which === 'rep' ? 'rep-collapsed' : 'ana-collapsed', other = which === 'rep' ? 'ana-collapsed' : 'rep-collapsed';
      var on = !sp.classList.contains(cls); sp.classList.toggle(cls, on); if (on) sp.classList.remove(other);
      $('btnLayoutReport').setAttribute('aria-pressed', sp.classList.contains('rep-collapsed') ? 'true' : 'false');
      $('btnLayoutAnalysis').setAttribute('aria-pressed', sp.classList.contains('ana-collapsed') ? 'true' : 'false');
      nextFrame(function () { chartInstances.forEach(function (c) { if (c.el.clientWidth) c.draw(); }); });
    }
    $('btnLayoutReport').addEventListener('click', function () { collapse('rep'); });
    $('btnLayoutAnalysis').addEventListener('click', function () { collapse('ana'); });
    $('btnTheme').addEventListener('click', toggleTheme);
    var dlg = $('aboutDlg');
    function openAbout() { if (dlg.showModal) dlg.showModal(); else dlg.setAttribute('open', ''); $('aboutClose').focus(); }
    function closeAbout() { if (dlg.close) dlg.close(); else dlg.removeAttribute('open'); $('btnAbout').focus(); }
    $('btnAbout').addEventListener('click', openAbout); $('btnAboutFoot').addEventListener('click', openAbout); $('aboutClose').addEventListener('click', closeAbout);
    $('btnCopySrc').addEventListener('click', function () { copyText($('srcUrl').textContent, 'Link copied'); });
    dlg.addEventListener('click', function (e) { if (e.target === dlg) closeAbout(); });
    $('btnInvert').addEventListener('click', function () {
      var b = $('btnInvert'), on = b.getAttribute('aria-pressed') !== 'true'; b.dataset.user = '1';
      b.setAttribute('aria-pressed', on ? 'true' : 'false'); $('reportFrame').classList.toggle('invert', on);
    });
    function openReport() { if (!host({ type: 'open' })) toast('Open is only available in the desktop app'); }
    $('btnOpen').addEventListener('click', openReport); $('btnOpenBig').addEventListener('click', openReport);
    $('btnClearRecent').addEventListener('click', function () { host({ type: 'clearRecent' }); });
    $('noticeClose').addEventListener('click', function () { notify(null); });
    initTypeMenu();
    document.addEventListener('keydown', function (e) {
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'o' || e.key === 'O')) {
        e.preventDefault();
        if (MODE === 'desktop') openReport();
      }
    });
    $('btnSave').addEventListener('click', function () { if (!host({ type: 'exportHtml' })) toast('Save is only available in the desktop app'); });
    /* Print and PDF always use the complete report (report.js), never the tab that happens to be open. */
    $('btnPrint').addEventListener('click', function () {
      if (!buildReport()) { toast('Open a report first'); return; }
      if (!host({ type: 'print' })) window.print();
    });
    $('btnPdf').addEventListener('click', function () {
      if (!buildReport()) { toast('Open a report first'); return; }
      if (!host({ type: 'exportPdf' })) window.print();
    });
    window.addEventListener('beforeprint', function () { buildReport(); }); /* Ctrl+P or a browser menu: same complete report */
    $('reportFrame').addEventListener('load', function () { if (REPORT || $('reportFrame').srcdoc) onFrameLoad(); });
    /* file drop anywhere: full-window overlay (the desktop host reads the dropped file's path) */
    var dz = $('dropzone'), ov = $('dropOverlay'), depth = 0;
    function dragHas(e) { var t = e.dataTransfer && e.dataTransfer.types; return !!t && Array.prototype.indexOf.call(t, 'Files') >= 0; }
    function endDrag() { depth = 0; ov.hidden = true; dz.classList.remove('over'); }
    window.addEventListener('dragenter', function (e) { e.preventDefault(); if (!dragHas(e)) return; depth++; ov.hidden = false; dz.classList.add('over'); });
    window.addEventListener('dragover', function (e) { e.preventDefault(); });
    window.addEventListener('dragleave', function (e) { e.preventDefault(); depth = Math.max(0, depth - 1); if (!depth) endDrag(); });
    window.addEventListener('drop', function (e) {
      e.preventDefault(); endDrag();
      if (!(e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0])) return;
      if (MODE !== 'desktop') toast('Drag and drop needs the desktop app');
    });
    window.addEventListener('ql:dropped', endDrag);
    if (window.matchMedia) {
      var mq = window.matchMedia('(prefers-color-scheme: dark)');
      var fn = function () { syncFrameTheme(); };
      if (mq.addEventListener) mq.addEventListener('change', fn);
    }
  }

  /* a cloned inline SVG must not repeat the ids of the original (gradients, title) */
  function uniqueIds(svg) {
    var els = svg.querySelectorAll('[id]'), all = svg.querySelectorAll('*');
    if (svg.id) svg.removeAttribute('id');
    for (var i = 0; i < els.length; i++) {
      var o = els[i].id, n = o + '-2';
      els[i].id = n;
      for (var j = 0; j < all.length; j++) {
        ['fill', 'stroke', 'filter', 'aria-labelledby', 'href'].forEach(function (a) {
          var v = all[j].getAttribute(a);
          if (v && (v === o || v.indexOf('#' + o + ')') >= 0 || v === '#' + o)) all[j].setAttribute(a, v === o ? n : v.replace('#' + o, '#' + n));
        });
      }
    }
    return svg;
  }

  /* ================= boot ================= */
  window.AwrApp = { load: load, showTable: showTable, setSource: setSource, setAppVersion: setAppVersion, notify: notify, setRecent: setRecent, version: '1.1' };
  function boot() {
    var logo = document.querySelector('.logo svg');
    if (logo) { var big = uniqueIds(logo.cloneNode(true)); var dl = document.querySelector('.drop-logo'); if (dl) dl.appendChild(big); }
    st = freshState();
    initTabs(); initLayout();
    var d = window.__AWR__ || {};
    load(d);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
