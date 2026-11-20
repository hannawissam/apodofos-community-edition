// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
// Injected into the page: returns a list of layout / usability defects for what is currently visible.
(function () {
  var out = [], W = innerWidth, H = innerHeight;
  function vis(e) {
    var r = e.getBoundingClientRect(); if (!r.width || !r.height) return false;
    for (var p = e; p; p = p.parentElement) { if (p.hidden) return false; var s = getComputedStyle(p); if (s.display === 'none' || s.visibility === 'hidden') return false; }
    return true;
  }
  function nm(e) { return e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : ''); }
  function add(kind, e, extra) { out.push(kind + ' ' + nm(e) + (extra ? ' ' + extra : '')); }
  var all = Array.prototype.slice.call(document.querySelectorAll('body *')).filter(function (e) { return !e.closest('svg') || e.tagName.toLowerCase() === 'svg'; });
  var vs = all.filter(vis);
  if (document.documentElement.scrollWidth > document.documentElement.clientWidth) out.push('page-hscroll ' + document.documentElement.scrollWidth + '>' + document.documentElement.clientWidth);
  if (document.body.scrollHeight > H + 1 && getComputedStyle(document.body).overflow === 'hidden') out.push('body-clipped-vertical ' + document.body.scrollHeight + '>' + H);
  // a scroller whose own scrollbar cannot be seen at the same time as its top (taller than the area it is clipped by)
  function clipBox(e) { var top = 0, bottom = H; for (var p = e.parentElement; p && p !== document.documentElement; p = p.parentElement) { var o = getComputedStyle(p); if (/(auto|scroll|hidden)/.test(o.overflowY)) { var q = p.getBoundingClientRect(); top = Math.max(top, q.top); bottom = Math.min(bottom, q.bottom); } } return { top: top, bottom: bottom }; }
  vs.forEach(function (e) {
    var cs = getComputedStyle(e), r = e.getBoundingClientRect();
    var sy = /(auto|scroll)/.test(cs.overflowY) && e.scrollHeight > e.clientHeight + 1, sx = /(auto|scroll)/.test(cs.overflowX) && e.scrollWidth > e.clientWidth + 1;
    var cb = clipBox(e);
    if ((sx || sy) && e.matches('.gridwrap,pre.scriptpre') && cb.bottom - cb.top > 330 && r.bottom > cb.bottom + 1 && r.top < cb.bottom - 60) add('scroller-bottom-cut-off', e, 'bottom=' + Math.round(r.bottom) + ' visible-until=' + Math.round(cb.bottom));
    if (sy && r.right > W + 1) add('vscrollbar-right-of-viewport', e, 'right=' + Math.round(r.right));
  });
  // visible elements poking outside the viewport horizontally (and not inside a clipping ancestor)
  vs.forEach(function (e) {
    var r = e.getBoundingClientRect();
    if (r.right > W + 1 || r.left < -1) {
      for (var p = e.parentElement; p && p !== document.body; p = p.parentElement) { if (getComputedStyle(p).overflowX !== 'visible') return; }
      if (e.closest('.drop-overlay,.toast,.skip,.sr')) return;
      add('outside-viewport-x', e, Math.round(r.left) + '..' + Math.round(r.right));
    }
  });
  // clipped text without ellipsis or a title
  vs.forEach(function (e) {
    var cs = getComputedStyle(e);
    if (e.scrollWidth > e.clientWidth + 1 && cs.overflowX === 'hidden' && e.tagName.toLowerCase() !== 'svg' && e.children.length < 6 && !e.closest('.fbody,.tabs,.sr') && !e.classList.contains('sr')) {
      var ell = cs.textOverflow === 'ellipsis';
      var t = e.title || (e.closest('[title]') && e.closest('[title]').title);
      if (!ell) add('text-clipped-no-ellipsis', e, e.scrollWidth + '>' + e.clientWidth + ' "' + (e.textContent || '').trim().slice(0, 40) + '"');
      else if (!t && !e.closest('.grid')) add('ellipsis-without-title', e, '"' + (e.textContent || '').trim().slice(0, 40) + '"');
    }
  });
  // overlapping interactive controls
  var ctl = vs.filter(function (e) { var t = e.tagName.toLowerCase(); return /^(button|input|select|a|textarea)$/.test(t) || e.getAttribute('role') === 'tab'; }).filter(function (e) {
    var r = e.getBoundingClientRect(), cb = clipBox(e), cy = (r.top + r.bottom) / 2;
    return r.bottom > 0 && r.top < H && r.right > 0 && r.left < W && cy >= cb.top && cy <= cb.bottom; // controls scrolled out of their pane are not on screen
  });
  function vrect(e) { var r = e.getBoundingClientRect(), cb = clipBox(e); return { left: r.left, right: r.right, top: Math.max(r.top, cb.top), bottom: Math.min(r.bottom, cb.bottom) }; } // the part of the control that can actually be seen
  for (var i = 0; i < ctl.length; i++) for (var j = i + 1; j < ctl.length; j++) {
    var a = vrect(ctl[i]), b = vrect(ctl[j]);
    var ox = Math.min(a.right, b.right) - Math.max(a.left, b.left), oy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    if (ox > 3 && oy > 3 && !ctl[i].contains(ctl[j]) && !ctl[j].contains(ctl[i]) && !(ctl[i].closest('label') && ctl[i].closest('label') === ctl[j].closest('label'))) add('controls-overlap', ctl[i], 'with ' + nm(ctl[j]) + ' ' + Math.round(ox) + 'x' + Math.round(oy));
  }
  ctl.forEach(function (e) { var r = e.getBoundingClientRect(); if ((r.width < 20 || r.height < 24) && e.type !== 'checkbox' && e.type !== 'radio' && !e.closest('.sr,.appfoot')) add('small-target', e, Math.round(r.width) + 'x' + Math.round(r.height)); });
  // accessible names
  ctl.forEach(function (e) {
    var n = e.getAttribute('aria-label') || e.getAttribute('aria-labelledby') || e.title || (e.textContent || '').trim() || (e.labels && e.labels.length && e.labels[0].textContent.trim()) || (e.closest('label') && e.closest('label').textContent.trim()) || e.getAttribute('placeholder');
    if (!n) add('no-accessible-name', e);
  });
  // zero-size containers
  document.querySelectorAll('.panel:not([hidden]),.ccard,.fcard:not([hidden]),.kpi,.cbox').forEach(function (e) { for (var q = e.parentElement; q; q = q.parentElement) { if (q.hidden || getComputedStyle(q).display === 'none') return; } if (getComputedStyle(e).display === 'none') return; var r = e.getBoundingClientRect(); if (r.width < 4 || r.height < 4) add('empty-container', e); });
  /* an icon is an svg with only a viewBox: without a CSS size it stretches to the width of its row (a huge arrow). Charts, rings and logos are sized on purpose. */
  document.querySelectorAll('svg[viewBox="0 0 24 24"]').forEach(function (e) { if (e.closest('.ring,.logo,.drop-logo,.cbox,.ccard,.chart')) return; var r = e.getBoundingClientRect(); if (r.width > 48 || r.height > 48) add('oversized-icon', e, Math.round(r.width) + 'x' + Math.round(r.height)); });
  var seen = {}; document.querySelectorAll('[id]').forEach(function (e) { if (seen[e.id]) out.push('duplicate-id #' + e.id); seen[e.id] = 1; });
  // contrast
  function parse(c) {
    var m = /^rgba?\(([^)]+)\)$/.exec(c); if (m) { var p = m[1].split(/[ ,\/]+/).filter(Boolean).map(parseFloat); return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1]; }
    m = /^color\(srgb ([^)]+)\)$/.exec(c); if (m) { var q = m[1].split(/[ \/]+/).filter(Boolean).map(parseFloat); return [q[0] * 255, q[1] * 255, q[2] * 255, q.length > 3 ? q[3] : 1]; }
    return null;
  }
  function over(fg, bg) { var a = fg[3]; return [fg[0] * a + bg[0] * (1 - a), fg[1] * a + bg[1] * (1 - a), fg[2] * a + bg[2] * (1 - a), 1]; }
  function lum(c) { var f = function (v) { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); }; return .2126 * f(c[0]) + .7152 * f(c[1]) + .0722 * f(c[2]); }
  function bgOf(e) {
    var layers = [];
    for (var p = e; p; p = p.parentElement) {
      var cs = getComputedStyle(p); if (/gradient/.test(cs.backgroundImage)) return null;
      var c = parse(cs.backgroundColor); if (c && c[3] > 0) { layers.push(c); if (c[3] >= .99) break; }
    }
    var base = [255, 255, 255, 1]; for (var i = layers.length - 1; i >= 0; i--) base = over(layers[i], base); return base;
  }
  var seenPair = {};
  vs.forEach(function (e) {
    var direct = ''; for (var n = e.firstChild; n; n = n.nextSibling) if (n.nodeType === 3) direct += n.nodeValue; direct = direct.trim(); if (!direct) return;
    if (e.closest('.sqlblock,pre.code,.topbar,.toast') || e.disabled || e.closest('button:disabled')) return;
    var cs = getComputedStyle(e), fg = parse(cs.color), bg = bgOf(e); if (!fg || !bg) return;
    var op = 1; for (var p2 = e; p2; p2 = p2.parentElement) op *= parseFloat(getComputedStyle(p2).opacity);
    fg = over([fg[0], fg[1], fg[2], fg[3] * op], bg);
    var l1 = lum(fg), l2 = lum(bg), cr = (Math.max(l1, l2) + .05) / (Math.min(l1, l2) + .05), size = parseFloat(cs.fontSize), bold = parseInt(cs.fontWeight, 10) >= 700;
    var need = (size >= 24 || (size >= 18.66 && bold)) ? 3 : 4.5;
    if (cr < need) { var key = nm(e) + cr.toFixed(1); if (!seenPair[key]) { seenPair[key] = 1; add('low-contrast', e, cr.toFixed(2) + ' (need ' + need + ') "' + direct.slice(0, 30) + '"'); } }
  });
  return out;
})()
