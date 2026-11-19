// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

/* AWR Analyzer - dependency-free SVG chart library.
 * window.AwrCharts.render(el, spec, opts) -> { update(spec), destroy() }
 * spec: { type: hbar|bar|donut|line|stacked, labels[], series[{name,values[]}], unit, logScale,
 *         reference, referenceLabel, xLabel, yLabel, title }
 * opts: { animate:true, height:number, maxRows:number }
 * All text is created with textContent / setAttribute - nothing is ever parsed as HTML.
 */
(function () {
  'use strict';
  var NS = 'http://www.w3.org/2000/svg';
  var CHAR_W = 6.2;

  var CSS = [
    '.awc{position:relative;width:100%;font:12px/1.35 var(--font,system-ui,"Segoe UI",sans-serif);color:var(--text,#1f2937)}',
    '.awc svg{display:block;width:100%;height:auto;overflow:visible}',
    '.awc-grid{stroke:var(--grid,rgba(100,116,139,.22));stroke-width:1;shape-rendering:crispEdges}',
    '.awc-axis{stroke:var(--axis,rgba(100,116,139,.55));stroke-width:1;shape-rendering:crispEdges}',
    '.awc-t{fill:var(--muted,#64748b);font-size:11px}',
    '.awc-lbl{fill:var(--text,#1f2937);font-size:12px}',
    '.awc-val{fill:var(--text,#1f2937);font-size:11px;font-variant-numeric:tabular-nums;font-weight:600}',
    '.awc-in{fill:#fff;font-size:10.5px;font-weight:700;paint-order:stroke;stroke:rgba(0,0,0,.5);stroke-width:2.5px;pointer-events:none}',
    '.awc-ref{stroke:var(--c-danger,#ef4444);stroke-width:1.5;stroke-dasharray:5 4}',
    '.awc-reft{fill:var(--c-danger,#ef4444);font-size:11px;font-weight:600}',
    '.awc-row{outline:none}',
    '.awc-row .awc-band{fill:transparent;transition:fill .12s}',
    '.awc-row:hover .awc-band,.awc-row:focus-visible .awc-band,.awc-row.on .awc-band{fill:var(--hover,rgba(14,118,144,.08))}',
    '.awc-row:focus-visible .awc-band{stroke:var(--focus,#0E7690);stroke-width:1.5}',
    '.awc-bar{transition:opacity .15s,filter .15s}',
    '.awc-anim .awc-hb{transform-box:fill-box;transform-origin:0 50%;animation:awc-x .65s cubic-bezier(.2,.7,.2,1) both;animation-delay:var(--d,0ms)}',
    '.awc-anim .awc-vb{transform-box:fill-box;transform-origin:50% 100%;animation:awc-y .65s cubic-bezier(.2,.7,.2,1) both;animation-delay:var(--d,0ms)}',
    '.awc-anim .awc-ln{stroke-dasharray:1;animation:awc-draw 1s cubic-bezier(.3,.6,.2,1) both}',
    '.awc-anim .awc-ar{animation:awc-fade .9s ease both}',
    '.awc-anim .awc-sl{animation:awc-d .8s cubic-bezier(.3,.6,.2,1) both;animation-delay:var(--d,0ms)}',
    '@keyframes awc-x{from{transform:scaleX(0)}}',
    '@keyframes awc-y{from{transform:scaleY(0)}}',
    '@keyframes awc-draw{from{stroke-dashoffset:1}to{stroke-dashoffset:0}}',
    '@keyframes awc-fade{from{opacity:0}}',
    '@keyframes awc-d{from{stroke-dasharray:0 100}}',
    '@media (prefers-reduced-motion:reduce){.awc-anim *{animation:none!important}}',
    '.awc-tip{position:absolute;left:0;top:0;pointer-events:none;background:var(--tip-bg,#0f172a);color:var(--tip-fg,#f8fafc);padding:7px 10px;border-radius:8px;font-size:12px;box-shadow:0 6px 20px rgba(0,0,0,.28);opacity:0;transition:opacity .12s;z-index:20;max-width:300px;white-space:normal}',
    '.awc-tip.show{opacity:1}',
    '.awc-tip b{display:block;margin-bottom:3px;font-weight:600;word-break:break-word}',
    '.awc-tip i{display:inline-block;width:9px;height:9px;border-radius:3px;margin-right:6px;vertical-align:-1px}',
    '.awc-tip span{display:flex;justify-content:space-between;gap:14px;font-variant-numeric:tabular-nums}',
    '.awc-tip em{font-style:normal;opacity:.7;font-size:11px}',
    '.awc-legend{display:flex;flex-wrap:wrap;gap:4px 6px;margin-top:8px}',
    '.awc-legend button{all:unset;cursor:pointer;display:inline-flex;align-items:center;gap:6px;padding:2px 8px;border-radius:999px;font-size:11.5px;color:var(--text,#1f2937);border:1px solid var(--line,rgba(100,116,139,.3));transition:opacity .15s,background .15s}',
    '.awc-legend button:hover{background:var(--hover,rgba(14,118,144,.08))}',
    '.awc-legend button:focus-visible{outline:2px solid var(--focus,#0E7690);outline-offset:1px}',
    '.awc-legend button.off{opacity:.45;text-decoration:line-through}',
    '.awc-legend i{width:10px;height:10px;border-radius:3px;display:inline-block}',
    '.awc-cap{margin-top:4px;color:var(--muted,#64748b);font-size:11px}',
    '.awc-empty{padding:18px;text-align:center;color:var(--muted,#64748b)}',
    '.awc-donut{display:flex;flex-wrap:wrap;gap:14px 22px;align-items:center;justify-content:center}',
    '.awc-donut svg{width:var(--s);height:var(--s);flex:none}',
    '.awc-dl{flex:1 1 180px;min-width:160px;display:flex;flex-direction:column;gap:2px;margin:0;padding:0;list-style:none}',
    '.awc-dl li{display:grid;grid-template-columns:12px 1fr auto auto;gap:8px;align-items:center;padding:3px 6px;border-radius:6px;cursor:default;transition:background .12s;font-variant-numeric:tabular-nums}',
    '.awc-dl li:hover,.awc-dl li.on{background:var(--hover,rgba(14,118,144,.08))}',
    '.awc-dl i{width:10px;height:10px;border-radius:3px}',
    '.awc-dl .n{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.awc-dl .p{color:var(--muted,#64748b);font-size:11px;min-width:38px;text-align:right}',
    '.awc-slice{transition:stroke-width .15s,opacity .15s}',
    '.awc-ctr{fill:var(--text,#1f2937);font-weight:700;text-anchor:middle}',
    '.awc-cts{fill:var(--muted,#64748b);text-anchor:middle;font-size:11px}'
  ].join('\n');

  function injectStyle() {
    if (document.getElementById('awc-style')) return;
    var s = document.createElement('style');
    s.id = 'awc-style';
    s.textContent = CSS;
    (document.head || document.documentElement).appendChild(s);
  }

  /* ---------- helpers ---------- */
  function S(tag, attrs, parent) {
    var e = document.createElementNS(NS, tag);
    if (attrs) for (var k in attrs) if (attrs[k] !== null && attrs[k] !== undefined) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  }
  function H(tag, cls, parent, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    if (parent) parent.appendChild(e);
    return e;
  }
  function T(parent, x, y, text, cls, anchor, extra) {
    var t = S('text', { x: x, y: y, 'class': cls, 'text-anchor': anchor || 'start' }, parent);
    if (extra) for (var k in extra) t.setAttribute(k, extra[k]);
    t.textContent = text;
    return t;
  }
  function trunc(s, n) {
    s = String(s == null ? '' : s);
    return s.length > n ? s.slice(0, Math.max(1, n - 1)) + '…' : s;
  }
  function fmt(v) {
    if (v === null || v === undefined || isNaN(v)) return '-';
    var a = Math.abs(v);
    if (a >= 1e12) return +(v / 1e12).toFixed(2) + 'T';
    if (a >= 1e9) return +(v / 1e9).toFixed(2) + 'B';
    if (a >= 1e6) return +(v / 1e6).toFixed(2) + 'M';
    if (a >= 1e4) return +(v / 1e3).toFixed(1) + 'K';
    if (a >= 100) return String(Math.round(v * 10) / 10);
    if (a >= 1) return String(+v.toFixed(2));
    if (a === 0) return '0';
    return String(+v.toPrecision(2));
  }
  function fmtFull(v) {
    if (v === null || v === undefined || isNaN(v)) return '-';
    try { return v.toLocaleString(undefined, { maximumFractionDigits: 3 }); } catch (e) { return String(v); }
  }
  function color(i, name) {
    if (name && /^(idle|other|others|free)$/i.test(String(name).trim())) return 'var(--c-muted,#94a3b8)';
    return 'var(--c' + ((i % 8) + 1) + ',#0E7690)';
  }
  function niceStep(range, count) {
    var raw = range / Math.max(1, count);
    var p = Math.pow(10, Math.floor(Math.log(raw) / Math.LN10));
    var f = raw / p;
    var n = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
    return n * p;
  }
  function linTicks(min, max, count) {
    if (!(max > min)) max = min + 1;
    var step = niceStep(max - min, count);
    var lo = Math.floor(min / step) * step, hi = Math.ceil(max / step) * step;
    var t = [];
    for (var v = lo; v <= hi + step * 1e-6; v += step) t.push(+v.toPrecision(12));
    return { ticks: t, min: lo, max: hi };
  }
  /* returns scale object {f(v)->0..1, ticks[], min, max} */
  function makeScale(values, opts) {
    var pos = values.filter(function (v) { return v !== null && v !== undefined && isFinite(v); });
    var max = pos.length ? Math.max.apply(null, pos) : 1;
    var min = pos.length ? Math.min.apply(null, pos) : 0;
    if (opts.reference !== null && opts.reference !== undefined && isFinite(opts.reference)) max = Math.max(max, opts.reference);
    if (opts.log) {
      var minPos = Infinity;
      pos.forEach(function (v) { if (v > 0 && v < minPos) minPos = v; });
      if (!isFinite(minPos)) minPos = 1;
      if (max <= 0) max = 1;
      var lo = Math.floor(Math.log(minPos) / Math.LN10) - 1;
      var hi = Math.ceil(Math.log(max) / Math.LN10);
      if (hi <= lo + 1) hi = lo + 2;
      var ticks = [];
      for (var d = lo; d <= hi; d++) ticks.push(Math.pow(10, d));
      var span = hi - lo;
      return {
        log: true, min: Math.pow(10, lo), max: Math.pow(10, hi), ticks: ticks,
        f: function (v) { return v > 0 ? Math.max(0, Math.min(1, (Math.log(v) / Math.LN10 - lo) / span)) : 0; }
      };
    }
    var base = opts.zero === false ? min : Math.min(0, min);
    if (max === base) max = base + 1;
    var lt = linTicks(base, max, opts.tickCount || 5);
    return {
      log: false, min: lt.min, max: lt.max, ticks: lt.ticks,
      f: function (v) { return Math.max(0, Math.min(1, (v - lt.min) / (lt.max - lt.min))); }
    };
  }

  /* ---------- instance ---------- */
  function Chart(el, spec, opts) {
    this.el = el;
    this.opts = opts || {};
    this.hidden = {};
    this.anim = this.opts.animate !== false;
    this.setSpec(spec);
    var self = this, timer = null;
    this.lastW = 0;
    if (typeof ResizeObserver !== 'undefined') {
      this.ro = new ResizeObserver(function () {
        clearTimeout(timer);
        timer = setTimeout(function () {
          var w = el.clientWidth;
          if (w && Math.abs(w - self.lastW) > 2) { self.draw(); }
        }, 80);
      });
      this.ro.observe(el);
    }
    this.draw();
  }
  Chart.prototype.setSpec = function (spec) {
    this.spec = spec || {};
    this.spec.labels = this.spec.labels || [];
    this.spec.series = this.spec.series || [];
  };
  Chart.prototype.update = function (spec, animate) {
    this.setSpec(spec);
    this.hidden = {};
    this.anim = animate !== false;
    this.draw();
  };
  Chart.prototype.destroy = function () {
    if (this.ro) this.ro.disconnect();
    this.el.textContent = '';
    this.el.__awc = null;
  };
  Chart.prototype.visibleSeries = function () {
    var self = this;
    return this.spec.series.map(function (s, i) { return { s: s, i: i }; })
      .filter(function (o) { return !self.hidden[o.i]; });
  };
  Chart.prototype.draw = function () {
    var el = this.el, spec = this.spec, type = spec.type || 'hbar';
    var W = el.clientWidth;
    if (!W) { return; } /* hidden: ResizeObserver redraws when it becomes visible */
    this.lastW = W;
    el.textContent = '';
    var root = H('div', 'awc' + (this.anim ? ' awc-anim' : ''), el);
    this.root = root;
    this.W = W;
    var hasData = spec.series.some(function (s) { return (s.values || []).some(function (v) { return v !== null && v !== undefined; }); });
    if (!hasData || !spec.labels.length) { H('div', 'awc-empty', root, 'No data to chart.'); return; }
    this.tip = H('div', 'awc-tip', root);
    this.tip.setAttribute('role', 'presentation');
    var fn = { hbar: this.hbar, bar: this.bar, line: this.line, stacked: this.stacked, donut: this.donut }[type] || this.hbar;
    try {
      fn.call(this, root, W);
    } catch (e) {
      H('div', 'awc-empty', root, 'Chart could not be drawn: ' + e.message);
    }
    this.anim = false;
  };

  /* tooltip */
  Chart.prototype.showTip = function (evt, title, rows, anchorEl) {
    var tip = this.tip;
    tip.textContent = '';
    H('b', null, tip, title);
    var unit = this.spec.unit;
    rows.forEach(function (r) {
      var line = H('span', null, tip);
      var l = H('div', null, line);
      if (r.color) { var sw = H('i', null, l); sw.style.background = r.color; }
      l.appendChild(document.createTextNode(r.name || ''));
      H('div', null, line, r.text !== undefined ? r.text : fmtFull(r.value));
    });
    if (unit) H('em', null, tip, unit);
    tip.classList.add('show');
    var rb = this.root.getBoundingClientRect();
    var x, y;
    if (evt && evt.clientX !== undefined && evt.type !== 'focus') { x = evt.clientX - rb.left; y = evt.clientY - rb.top; }
    else if (anchorEl) { var ab = anchorEl.getBoundingClientRect(); x = ab.left - rb.left + ab.width / 2; y = ab.top - rb.top + 8; }
    else { x = 20; y = 20; }
    var tw = tip.offsetWidth, th = tip.offsetHeight;
    var left = x + 14;
    if (left + tw > rb.width) left = Math.max(0, x - tw - 14);
    var top = y - th - 10;
    if (top < 0) top = y + 16;
    tip.style.transform = 'translate(' + Math.round(left) + 'px,' + Math.round(top) + 'px)';
  };
  Chart.prototype.hideTip = function () { if (this.tip) this.tip.classList.remove('show'); };

  Chart.prototype.bindRow = function (g, title, rows) {
    var self = this;
    g.addEventListener('mousemove', function (e) { self.showTip(e, title, rows); g.classList.add('on'); });
    g.addEventListener('mouseleave', function () { self.hideTip(); g.classList.remove('on'); });
    g.addEventListener('focus', function (e) { self.showTip({ type: 'focus' }, title, rows, g); });
    g.addEventListener('blur', function () { self.hideTip(); });
    g.setAttribute('tabindex', '0');
    g.setAttribute('role', 'img');
    g.setAttribute('aria-label', title + ': ' + rows.map(function (r) { return (r.name ? r.name + ' ' : '') + (r.text !== undefined ? r.text : fmtFull(r.value)); }).join(', '));
  };

  Chart.prototype.legend = function (root, names) {
    var self = this;
    if (names.length < 2) return;
    var lg = H('div', 'awc-legend', root);
    lg.setAttribute('role', 'group');
    lg.setAttribute('aria-label', 'Series (click to show or hide)');
    names.forEach(function (n, i) {
      var b = H('button', self.hidden[i] ? 'off' : '', lg);
      b.type = 'button';
      b.setAttribute('aria-pressed', self.hidden[i] ? 'false' : 'true');
      b.tabIndex = 0;
      var sw = H('i', null, b); sw.style.background = color(i, n);
      b.appendChild(document.createTextNode(n));
      b.addEventListener('click', function () {
        self.hidden[i] = !self.hidden[i];
        var vis = self.spec.series.filter(function (s, j) { return !self.hidden[j]; });
        if (!vis.length) self.hidden[i] = false;
        self.anim = false;
        self.draw();
        var nb = self.root.querySelectorAll('.awc-legend button')[i];
        if (nb) nb.focus();
      });
    });
  };
  Chart.prototype.caption = function (root, text) {
    if (text) H('div', 'awc-cap', root, text);
  };
  Chart.prototype.refLine = function (svg, scale, spec, horiz, x0, x1, y0, y1) {
    var r = spec.reference;
    if (r === null || r === undefined || !isFinite(r)) return;
    var f = scale.f(r);
    if (horiz) { /* vertical line on a horizontal bar chart */
      var x = x0 + f * (x1 - x0);
      S('line', { x1: x, x2: x, y1: y0 - 4, y2: y1, 'class': 'awc-ref' }, svg);
      T(svg, x > x0 + (x1 - x0) * 0.7 ? x - 4 : x + 4, y0 - 7, (spec.referenceLabel || 'Reference') + ' ' + fmt(r), 'awc-reft', x > x0 + (x1 - x0) * 0.7 ? 'end' : 'start');
    } else {
      var y = y1 - f * (y1 - y0);
      S('line', { x1: x0, x2: x1, y1: y, y2: y, 'class': 'awc-ref' }, svg);
      T(svg, x1, y - 4, (spec.referenceLabel || 'Reference') + ' ' + fmt(r), 'awc-reft', 'end');
    }
  };

  /* ---------- horizontal bar ---------- */
  Chart.prototype.hbar = function (root, W) {
    var spec = this.spec, vis = this.visibleSeries();
    var maxRows = this.opts.maxRows || 60;
    var labels = spec.labels.slice(0, maxRows), n = labels.length, sn = vis.length;
    var barH = sn > 1 ? 11 : 18, pad = sn > 1 ? 10 : 11, rowH = sn * barH + pad;
    var maxLen = 0; labels.forEach(function (l) { maxLen = Math.max(maxLen, String(l).length); });
    var lw = Math.min(Math.max(60, maxLen * CHAR_W + 12), Math.max(90, W * 0.4));
    var x0 = lw, x1 = Math.max(x0 + 40, W - 62), top = 20, bottom = 22;
    var H_ = top + n * rowH + bottom;
    var svg = S('svg', { viewBox: '0 0 ' + W + ' ' + H_, role: 'group', 'aria-label': spec.title || 'Bar chart' }, root);
    var all = []; vis.forEach(function (o) { all = all.concat(o.s.values.slice(0, n)); });
    var sc = makeScale(all, { log: spec.logScale, reference: spec.reference });
    sc.ticks.forEach(function (t) {
      var x = x0 + sc.f(t) * (x1 - x0);
      S('line', { x1: x, x2: x, y1: top - 2, y2: top + n * rowH, 'class': 'awc-grid' }, svg);
      T(svg, x, top + n * rowH + 15, fmt(t), 'awc-t', 'middle');
    });
    S('line', { x1: x0, x2: x0, y1: top - 2, y2: top + n * rowH, 'class': 'awc-axis' }, svg);
    var cpl = Math.floor((lw - 10) / CHAR_W);
    var self = this;
    labels.forEach(function (lab, i) {
      var y = top + i * rowH;
      var g = S('g', { 'class': 'awc-row' }, svg);
      S('rect', { 'class': 'awc-band', x: 0, y: y, width: W, height: rowH, rx: 4 }, g);
      var lt = T(g, x0 - 8, y + rowH / 2 + 4, trunc(lab, cpl), 'awc-lbl', 'end');
      if (String(lab).length > cpl) { var ti = S('title', null, lt); ti.textContent = lab; }
      var rows = [];
      vis.forEach(function (o, k) {
        var v = o.s.values[i];
        var by = y + pad / 2 + k * barH;
        if (v === null || v === undefined) return;
        var w = Math.max(v > 0 ? 2 : 0, sc.f(v) * (x1 - x0));
        var col = color(sn > 1 ? o.i : 0, sn > 1 ? o.s.name : null);
        var bh = barH - (sn > 1 ? 2 : 0);
        S('rect', { 'class': 'awc-bar awc-hb', x: x0, y: by, width: w, height: bh, rx: 3, style: 'fill:' + col + ';--d:' + Math.min(i * 22, 500) + 'ms' }, g);
        T(g, x0 + w + 5, by + bh / 2 + 4, fmt(v), 'awc-val');
        rows.push({ name: sn > 1 ? o.s.name : '', value: v, color: col });
      });
      self.bindRow(g, String(lab), rows);
    });
    this.refLine(svg, sc, spec, true, x0, x1, top, top + n * rowH);
    if (spec.labels.length > maxRows) this.caption(root, 'Showing the first ' + maxRows + ' of ' + spec.labels.length + ' rows.');
    this.legend(root, spec.series.map(function (s) { return s.name; }));
    this.caption(root, [spec.xLabel || spec.unit, sc.log ? 'logarithmic scale' : ''].filter(Boolean).join(' · '));
  };

  /* ---------- vertical bar ---------- */
  Chart.prototype.bar = function (root, W) {
    var spec = this.spec, vis = this.visibleSeries(), n = spec.labels.length, sn = vis.length;
    var maxLen = 0; spec.labels.forEach(function (l) { maxLen = Math.max(maxLen, Math.min(18, String(l).length)); });
    var left = 46, right = 10, top = 18;
    var band = (W - left - right) / n;
    var rotate = maxLen * 6 > band - 4;
    var bottom = rotate ? Math.min(86, maxLen * 4.2 + 16) : 24;
    var plotH = this.opts.height ? this.opts.height - top - bottom : 190;
    var H_ = top + plotH + bottom;
    var svg = S('svg', { viewBox: '0 0 ' + W + ' ' + H_, role: 'group', 'aria-label': spec.title || 'Column chart' }, root);
    var all = []; vis.forEach(function (o) { all = all.concat(o.s.values); });
    var sc = makeScale(all, { log: spec.logScale, reference: spec.reference });
    var y1 = top + plotH;
    sc.ticks.forEach(function (t) {
      var y = y1 - sc.f(t) * plotH;
      S('line', { x1: left, x2: W - right, y1: y, y2: y, 'class': 'awc-grid' }, svg);
      T(svg, left - 6, y + 4, fmt(t), 'awc-t', 'end');
    });
    S('line', { x1: left, x2: W - right, y1: y1, y2: y1, 'class': 'awc-axis' }, svg);
    var gw = Math.min(band * 0.78, sn * 34), bw = gw / sn;
    var step = rotate ? 1 : Math.max(1, Math.ceil((maxLen * 6 + 6) / band));
    var self = this;
    spec.labels.forEach(function (lab, i) {
      var cx = left + band * (i + 0.5);
      var g = S('g', { 'class': 'awc-row' }, svg);
      S('rect', { 'class': 'awc-band', x: left + band * i, y: top, width: band, height: plotH + 2, rx: 4 }, g);
      var rows = [];
      vis.forEach(function (o, k) {
        var v = o.s.values[i];
        if (v === null || v === undefined) return;
        var h = Math.max(v > 0 ? 2 : 0, sc.f(v) * plotH);
        var col = color(sn > 1 ? o.i : 0, sn > 1 ? o.s.name : null);
        S('rect', { 'class': 'awc-bar awc-vb', x: cx - gw / 2 + k * bw + 1, y: y1 - h, width: Math.max(1, bw - 2), height: h, rx: 3, style: 'fill:' + col + ';--d:' + Math.min(i * 18, 400) + 'ms' }, g);
        if (bw > 22 && n < 14) T(g, cx - gw / 2 + k * bw + bw / 2, y1 - h - 4, fmt(v), 'awc-val', 'middle');
        rows.push({ name: sn > 1 ? o.s.name : '', value: v, color: col });
      });
      if (i % step === 0) {
        var t = T(svg, cx, y1 + 15, trunc(lab, 18), 'awc-t', rotate ? 'end' : 'middle');
        if (rotate) t.setAttribute('transform', 'rotate(-35 ' + cx + ' ' + (y1 + 15) + ')');
      }
      self.bindRow(g, String(lab), rows);
    });
    this.refLine(svg, sc, spec, false, left, W - right, top, y1);
    this.legend(root, spec.series.map(function (s) { return s.name; }));
    this.caption(root, [spec.yLabel || spec.unit, sc.log ? 'logarithmic scale' : ''].filter(Boolean).join(' · '));
  };

  /* ---------- line ---------- */
  Chart.prototype.line = function (root, W) {
    var spec = this.spec, vis = this.visibleSeries(), n = spec.labels.length, self = this;
    var left = 48, right = 14, top = 18, bottom = 30;
    var plotH = this.opts.height ? this.opts.height - top - bottom : 200;
    var H_ = top + plotH + bottom, y1 = top + plotH;
    var svg = S('svg', { viewBox: '0 0 ' + W + ' ' + H_, role: 'group', 'aria-label': spec.title || 'Line chart' }, root);
    var all = []; vis.forEach(function (o) { all = all.concat(o.s.values); });
    var finite = all.filter(function (v) { return v !== null && v !== undefined && isFinite(v); });
    var mn = Math.min.apply(null, finite), mx = Math.max.apply(null, finite);
    var zero = !(mn > 0 && mn > 0.5 * mx && !spec.logScale && mx > mn);
    var sc = makeScale(all, { log: spec.logScale, reference: spec.reference, zero: zero ? undefined : false });
    sc.ticks.forEach(function (t) {
      var y = y1 - sc.f(t) * plotH;
      S('line', { x1: left, x2: W - right, y1: y, y2: y, 'class': 'awc-grid' }, svg);
      T(svg, left - 6, y + 4, fmt(t), 'awc-t', 'end');
    });
    S('line', { x1: left, x2: W - right, y1: y1, y2: y1, 'class': 'awc-axis' }, svg);
    var xs = function (i) { return n === 1 ? (left + W - right) / 2 : left + (W - left - right) * i / (n - 1); };
    var maxLen = 0; spec.labels.forEach(function (l) { maxLen = Math.max(maxLen, Math.min(14, String(l).length)); });
    var every = Math.max(1, Math.ceil((maxLen * 6 + 14) / ((W - left - right) / Math.max(1, n - 1))));
    spec.labels.forEach(function (l, i) {
      if (i % every === 0) {
        T(svg, xs(i), y1 + 16, trunc(l, 14), 'awc-t', i === 0 ? 'start' : 'middle');
        S('line', { x1: xs(i), x2: xs(i), y1: y1, y2: y1 + 4, 'class': 'awc-axis' }, svg);
      }
    });
    vis.forEach(function (o) {
      var col = color(o.i, o.s.name), segs = [], cur = [];
      for (var i = 0; i < n; i++) {
        var v = o.s.values[i];
        if (v === null || v === undefined || !isFinite(v) || (sc.log && v <= 0)) { if (cur.length) segs.push(cur); cur = []; continue; }
        cur.push([xs(i), y1 - sc.f(v) * plotH]);
      }
      if (cur.length) segs.push(cur);
      segs.forEach(function (sg) {
        var d = sg.map(function (p, j) { return (j ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1); }).join('');
        if (vis.length === 1 && sg.length > 1) {
          S('path', { 'class': 'awc-ar', d: d + 'L' + sg[sg.length - 1][0].toFixed(1) + ' ' + y1 + 'L' + sg[0][0].toFixed(1) + ' ' + y1 + 'Z', style: 'fill:' + col + ';opacity:.13' }, svg);
        }
        S('path', { 'class': 'awc-ln', d: d, pathLength: 1, fill: 'none', 'stroke-width': 2.2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round', style: 'stroke:' + col }, svg);
        if (n <= 60) sg.forEach(function (p) { S('circle', { cx: p[0], cy: p[1], r: sg.length > 30 ? 1.8 : 3, 'stroke-width': 1.6, style: 'fill:var(--surface,#fff);stroke:' + col }, svg); });
      });
    });
    this.refLine(svg, sc, spec, false, left, W - right, top, y1);
    /* hover guide */
    var guide = S('line', { y1: top, y2: y1, 'class': 'awc-axis', style: 'opacity:0;stroke-dasharray:3 3' }, svg);
    var hit = S('rect', { x: left, y: top, width: W - left - right, height: plotH, fill: 'transparent', tabindex: 0, role: 'img', 'aria-label': (spec.title || 'Line chart') + ', use left and right arrow keys to read values' }, svg);
    var cur = -1;
    function show(i, evt) {
      i = Math.max(0, Math.min(n - 1, i)); cur = i;
      guide.setAttribute('x1', xs(i)); guide.setAttribute('x2', xs(i)); guide.style.opacity = 1;
      self.showTip(evt, String(spec.labels[i]), vis.map(function (o) {
        var v = o.s.values[i];
        return { name: o.s.name, color: color(o.i, o.s.name), text: (v === null || v === undefined) ? '-' : fmtFull(v) };
      }), hit);
    }
    hit.addEventListener('mousemove', function (e) {
      var r = svg.getBoundingClientRect(), x = (e.clientX - r.left) * (W / r.width);
      show(n === 1 ? 0 : Math.round((x - left) / (W - left - right) * (n - 1)), e);
    });
    hit.addEventListener('mouseleave', function () { self.hideTip(); guide.style.opacity = 0; });
    hit.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowRight') { show(cur + 1, { type: 'focus' }); e.preventDefault(); }
      else if (e.key === 'ArrowLeft') { show(cur < 0 ? 0 : cur - 1, { type: 'focus' }); e.preventDefault(); }
    });
    hit.addEventListener('blur', function () { self.hideTip(); guide.style.opacity = 0; });
    this.legend(root, spec.series.map(function (s) { return s.name; }));
    this.caption(root, [spec.yLabel || spec.unit, spec.xLabel, sc.log ? 'logarithmic scale' : ''].filter(Boolean).join(' · '));
  };

  /* ---------- stacked (horizontal) ---------- */
  Chart.prototype.stacked = function (root, W) {
    var spec = this.spec, vis = this.visibleSeries(), n = spec.labels.length, self = this;
    var maxLen = 0; spec.labels.forEach(function (l) { maxLen = Math.max(maxLen, String(l).length); });
    var lw = n === 1 ? 0 : Math.min(Math.max(60, maxLen * CHAR_W + 12), Math.max(90, W * 0.36));
    var x0 = lw + (lw ? 0 : 4), x1 = W - 10, top = 6, barH = n === 1 ? 34 : 20, rowH = barH + 10, bottom = 22;
    var H_ = top + n * rowH + bottom;
    var sums = spec.labels.map(function (l, i) { return vis.reduce(function (a, o) { return a + (+o.s.values[i] || 0); }, 0); });
    var max = Math.max.apply(null, sums);
    var domain = (max > 90 && max <= 100.5) ? 100 : null;
    var sc = makeScale(domain ? [0, domain] : sums, {});
    var svg = S('svg', { viewBox: '0 0 ' + W + ' ' + H_, role: 'group', 'aria-label': spec.title || 'Stacked bar chart' }, root);
    sc.ticks.forEach(function (t) {
      var x = x0 + sc.f(t) * (x1 - x0);
      S('line', { x1: x, x2: x, y1: top, y2: top + n * rowH, 'class': 'awc-grid' }, svg);
      T(svg, x, top + n * rowH + 15, fmt(t), 'awc-t', 'middle');
    });
    var cpl = Math.floor((lw - 10) / CHAR_W);
    spec.labels.forEach(function (lab, i) {
      var y = top + i * rowH, g = S('g', { 'class': 'awc-row' }, svg);
      S('rect', { 'class': 'awc-band', x: 0, y: y, width: W, height: rowH, rx: 4 }, g);
      if (lw) { var lt = T(g, x0 - 8, y + rowH / 2 + 4, trunc(lab, cpl), 'awc-lbl', 'end'); if (String(lab).length > cpl) S('title', null, lt).textContent = lab; }
      var acc = 0, rows = [];
      vis.forEach(function (o) {
        var v = +o.s.values[i];
        if (!isFinite(v) || v <= 0) { return; }
        var xa = x0 + sc.f(acc) * (x1 - x0), xb = x0 + sc.f(acc + v) * (x1 - x0);
        S('rect', { 'class': 'awc-bar awc-hb', x: xa, y: y + 5, width: Math.max(1, xb - xa - 1), height: barH, rx: 2, style: 'fill:' + color(o.i, o.s.name) + ';--d:' + Math.min(i * 22, 400) + 'ms' }, g);
        if (xb - xa > 34) T(g, (xa + xb) / 2, y + 5 + barH / 2 + 4, fmt(v), 'awc-in', 'middle');
        rows.push({ name: o.s.name, value: v, color: color(o.i, o.s.name) });
        acc += v;
      });
      self.bindRow(g, String(lab), rows);
    });
    this.legend(root, spec.series.map(function (s) { return s.name; }));
    this.caption(root, spec.xLabel || spec.unit);
  };

  /* ---------- donut ---------- */
  Chart.prototype.donut = function (root, W) {
    var spec = this.spec, self = this, s0 = spec.series[0] || { values: [] };
    var items = [];
    spec.labels.forEach(function (l, i) { var v = +s0.values[i]; if (isFinite(v) && v > 0) items.push({ l: l, v: v, i: i }); });
    var total = items.reduce(function (a, b) { return a + b.v; }, 0);
    var SZ = Math.max(150, Math.min(230, W < 420 ? W - 20 : W * 0.42));
    var wrap = H('div', 'awc-donut', root);
    wrap.style.setProperty('--s', SZ + 'px');
    var svg = S('svg', { viewBox: '0 0 100 100', role: 'group', 'aria-label': spec.title || 'Donut chart' }, wrap);
    var R = 38, SW = 15;
    S('circle', { cx: 50, cy: 50, r: R, fill: 'none', 'stroke-width': SW, style: 'stroke:var(--grid,rgba(100,116,139,.2))' }, svg);
    var cum = 0, slices = [], lis = [];
    var ctr = S('text', { x: 50, y: 52, 'class': 'awc-ctr', 'font-size': 11 }, svg);
    var cts = S('text', { x: 50, y: 62, 'class': 'awc-cts', 'font-size': 5.2 }, svg);
    function setCenter(a, b) { ctr.textContent = a; cts.textContent = b; }
    var defU = (spec.unit || '').replace(/^%\s*of\s*/i, '');
    function reset() {
      // For percentage charts the raw sum is meaningless (shares may not add up to exactly 100): show the largest slice instead.
      var big = items.reduce(function (a, b) { return !a || b.v > a.v ? b : a; }, null);
      if (/%/.test(spec.unit || '') && big) setCenter(big.v.toFixed(1) + '%', trunc(big.l, 20));
      else setCenter(total >= 1000 ? fmt(total) : String(+total.toFixed(1)), trunc(defU || 'total', 18));
      slices.forEach(function (s) { s.style.opacity = 1; });
      lis.forEach(function (l) { l.classList.remove('on'); });
    }
    var ul = H('ul', 'awc-dl', wrap);
    items.forEach(function (it, k) {
      var pct = it.v / total * 100, col = color(it.i, it.l);
      var shown = /%/.test(spec.unit || '') ? it.v : pct; /* % charts: show the report's own figure, not a re-normalised share */
      var len = Math.max(0.2, pct - (items.length > 1 ? 0.6 : 0));
      var c = S('circle', { 'class': 'awc-slice awc-sl', cx: 50, cy: 50, r: R, fill: 'none', 'stroke-width': SW, pathLength: 100,
        'stroke-dasharray': len + ' ' + (100 - len), 'stroke-dashoffset': -cum,
        transform: 'rotate(-90 50 50)', style: 'stroke:' + col + ';--d:' + (k * 60) + 'ms', tabindex: 0, role: 'img',
        'aria-label': it.l + ' ' + fmtFull(it.v) + ' (' + shown.toFixed(1) + '%)' }, svg);
      cum += pct;
      slices.push(c);
      var li = H('li', null, ul); lis.push(li);
      var sw = H('i', null, li); sw.style.background = col;
      var nm = H('span', 'n', li, it.l); nm.title = it.l;
      H('span', null, li, fmt(it.v));
      H('span', 'p', li, shown.toFixed(shown < 10 ? 1 : 0) + '%');
      var on = function () {
        slices.forEach(function (s, j) { s.style.opacity = j === k ? 1 : 0.3; });
        lis.forEach(function (l, j) { l.classList.toggle('on', j === k); });
        setCenter(shown.toFixed(1) + '%', trunc(it.l, 20));
      };
      [c, li].forEach(function (t) {
        t.addEventListener('mouseenter', on); t.addEventListener('mouseleave', reset);
      });
      c.addEventListener('focus', on); c.addEventListener('blur', reset);
    });
    reset();
    if (spec.unit) this.caption(root, spec.unit);
  };

  /* ---------- API ---------- */
  window.AwrCharts = {
    render: function (el, spec, opts) {
      injectStyle();
      if (el.__awc) el.__awc.destroy();
      var c = new Chart(el, spec, opts);
      el.__awc = c;
      return c;
    },
    format: fmt,
    formatFull: fmtFull,
    colorFor: color
  };
})();
