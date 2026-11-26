/* SPDX-License-Identifier: AGPL-3.0-only
   Copyright (c) 2026 Wissam HANNA

   Print / PDF report. Builds a complete, formatted document from the analysis payload, independent of the tab that is open:
   cover, health, data quality, key numbers, fix-first plan, root causes, every finding (evidence, steps, SQL, documentation),
   charts, top SQL, the referenced Oracle documentation and report facts. Pure DOM construction with textContent only (report text is never parsed as HTML).
   No network, no dependencies besides the optional window.AwrCharts for the charts. */
(function () {
  'use strict';
  var SEV = ['Critical', 'High', 'Medium', 'Low', 'Info', 'Ok'];
  var SEV_LABEL = { Critical: 'Critical', High: 'High', Medium: 'Medium', Low: 'Low', Info: 'Info', Ok: 'Healthy' };
  var PROPS = { id: 1, hidden: 1, className: 1, textContent: 1 };

  function h(tag, props) {
    var e = document.createElement(tag);
    if (props) for (var k in props) {
      var v = props[k];
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') e.className = v; else if (k === 'text') e.textContent = v; else e.setAttribute(k, v === true ? '' : v);
    }
    for (var i = 2; i < arguments.length; i++) add(e, arguments[i]);
    return e;
  }
  function add(p, c) {
    if (c === null || c === undefined || c === false) return;
    if (Array.isArray(c)) { c.forEach(function (x) { add(p, x); }); return; }
    p.appendChild(c.nodeType ? c : document.createTextNode(String(c)));
  }
  function num(v, d) {
    if (v === null || v === undefined || v === '' || isNaN(v)) return '-';
    try { return Number(v).toLocaleString(undefined, { maximumFractionDigits: d === undefined ? 2 : d }); } catch (e) { return String(v); }
  }
  function clip(s, n) { s = String(s || ''); return s.length > n ? s.slice(0, n - 1) + '…' : s; }
  function safeUrl(u) { return /^https:\/\//i.test(u || '') ? u : null; }
  function extLink(url, text, cls) { return h('a', { class: cls, href: url, target: '_blank', rel: 'noopener noreferrer', text: text }); }

  /* ---- Oracle documentation referenced by the findings (shared with the dashboard, see app.js) ---- */
  var REF_TITLE = 'Oracle documentation referenced by this analysis';
  var REF_NOTE = 'These are the official Oracle documentation pages related to the findings, numbered in the order they are first cited. ' +
    'They are for checking and further reading: not every threshold or recommendation in this analysis comes from Oracle documentation. ' +
    'Opening a link needs internet access in your web browser; the application itself fetches nothing.';
  /* One entry per URL, in order of first citation (the findings arrive most severe first). Title, book and section come from the
     catalogue entry (analysis.docs[]) with the same URL when there is one. Only https addresses get an href. */
  function references(A) {
    var cat = Object.create(null), byUrl = Object.create(null), list = [];
    (A.docs || []).forEach(function (d) { if (d.url && !cat[d.url]) cat[d.url] = d; });
    (A.findings || []).forEach(function (f) {
      (f.docs || []).forEach(function (d) {
        var u = d.url; if (!u) return;
        var r = byUrl[u];
        if (!r) {
          var c = cat[u] || {};
          r = byUrl[u] = { n: list.length + 1, url: u, href: safeUrl(u), title: c.title || d.title || u, book: c.book || '', section: c.section || '', local: c.local || '', findings: [] };
          list.push(r);
        }
        if (r.findings.indexOf(f) < 0) r.findings.push(f);
      });
    });
    return { list: list, byUrl: byUrl };
  }
  function typ(A) { var t = A.meta && A.meta.reportType; return t === 'ASH' || t === 'Statspack' ? t : 'AWR'; }
  function timeLabel(A) { var t = typ(A); return t === 'ASH' ? 'active session time' : t === 'Statspack' ? 'DB time (Statspack)' : 'DB time'; }
  var TM = 'Apodofos\u2122 is a trademark of Wissam HANNA (French trade mark application no. 5322937, not yet registered).';
  function product(A) { return (A.meta && A.meta.product) || 'Apodofos Report Analyzer'; }
  /* ---- the application version shown on screen and on every printed report: never a made-up number ---- */
  function cleanVersion(v) { v = String(v || '').split('+')[0]; return /^\d+(\.\d+){1,3}(-[0-9A-Za-z.\-]+)?$/.test(v) ? v : ''; }
  /* app: what the running application reported (desktop host only); engine: meta.analyzerVersion, which saved pages carry too */
  function versionText(app, engine) {
    app = cleanVersion(app); engine = cleanVersion(engine);
    if (app && engine && app !== engine) return app + ' (analysis engine ' + engine + ')';
    return app || engine;
  }
  function sec(title, cls) { return h('section', { class: 'rpt-sec ' + (cls || '') }, h('h2', { text: title })); }

  function ring(A) {
    var NS = 'http://www.w3.org/2000/svg', hl = A.health, unreliable = hl.reliable === false;
    var score = Math.max(0, Math.min(100, +hl.score || 0)), C = 2 * Math.PI * 52;
    var col = score >= 85 ? '#15803d' : score >= 70 ? '#0e7690' : score >= 50 ? '#b45309' : '#c81e1e';
    var s = document.createElementNS(NS, 'svg'); s.setAttribute('viewBox', '0 0 120 120'); s.setAttribute('class', 'rpt-ring'); s.setAttribute('role', 'img');
    s.setAttribute('aria-label', unreliable ? 'Health score not reliable' : 'Health score ' + score + ' out of 100');
    function circle(cls, stroke, dash) {
      var c = document.createElementNS(NS, 'circle');
      c.setAttribute('cx', 60); c.setAttribute('cy', 60); c.setAttribute('r', 52); c.setAttribute('fill', 'none'); c.setAttribute('stroke', stroke); c.setAttribute('stroke-width', 10);
      if (dash) { c.setAttribute('stroke-dasharray', dash + ' ' + C); c.setAttribute('stroke-linecap', 'round'); c.setAttribute('transform', 'rotate(-90 60 60)'); }
      s.appendChild(c);
    }
    circle('t', '#e2e8f0');
    if (!unreliable && score > 0) circle('a', col, (C * score / 100).toFixed(1));
    var t = document.createElementNS(NS, 'text'); t.setAttribute('x', 60); t.setAttribute('y', 66); t.setAttribute('text-anchor', 'middle'); t.setAttribute('class', 'rpt-ring-score');
    t.textContent = unreliable ? '—' : String(score); s.appendChild(t);
    var g = document.createElementNS(NS, 'text'); g.setAttribute('x', 60); g.setAttribute('y', 84); g.setAttribute('text-anchor', 'middle'); g.setAttribute('class', 'rpt-ring-grade');
    g.textContent = unreliable ? 'NOT RELIABLE' : 'GRADE ' + (hl.grade || '-'); s.appendChild(g);
    return s;
  }

  function cover(A, o) {
    var m = A.meta, name = [m.dbName, m.instance && m.instance !== m.dbName ? m.instance : ''].filter(Boolean).join(' / ') || m.sourceFile || (typ(A) + ' report');
    var facts = [['Oracle release', m.release], ['Host', m.host], ['Window', m.beginTime && m.endTime ? m.beginTime + '  →  ' + m.endTime : ''],
      ['Elapsed', m.elapsedMinutes ? num(m.elapsedMinutes, 1) + ' min' : ''], ['Source file', m.sourceFile]].filter(function (r) { return r[1]; });
    return h('header', { class: 'rpt-cover' },
      h('div', { class: 'rpt-brand' }, o.logo ? h('span', { class: 'rpt-logo' }, o.logo) : null, h('span', { class: 'rpt-product', text: product(A) }),
        h('span', { class: 'rpt-kicker', text: typ(A) + ' performance analysis' })),
      h('h1', { text: name }),
      h('dl', { class: 'rpt-facts' }, facts.map(function (r) { return h('div', null, h('dt', { text: r[0] }), h('dd', { text: r[1] })); })),
      h('p', { class: 'rpt-tm', text: TM }),
      h('p', { class: 'rpt-gen', text: 'Report generated ' + o.now.toLocaleString() + (o.version ? '  ·  Apodofos ' + o.version : '') + '  ·  analysed offline on this computer' }));
  }

  function health(A) {
    var hl = A.health, s = sec('Overall health', 'rpt-health');
    var chips = h('div', { class: 'rpt-chips' });
    SEV.forEach(function (v) { var n = (hl.counts && hl.counts[v]) || 0; if (n) chips.appendChild(h('span', { class: 'rpt-chip sev-' + v }, h('b', { text: String(n) }), ' ' + SEV_LABEL[v])); });
    s.appendChild(h('div', { class: 'rpt-hero' }, ring(A), h('div', { class: 'rpt-hero-t' },
      h('h3', { text: hl.headline || 'Analysis complete' }),
      h('p', { class: 'rpt-muted', text: 'Score from 0 (urgent attention) to 100 (healthy), based on the issues found in this report.' }), chips)));
    (hl.paragraphs || []).forEach(function (t) { s.appendChild(h('p', { text: t })); });
    return s;
  }

  function quality(A) {
    var dq = A.dataQuality, out = [];
    if (dq.alerts.length) out.push(h('div', { class: 'rpt-alert' }, h('strong', { text: 'Warning about this report' }), h('ul', null, dq.alerts.map(function (n) { return h('li', { text: n }); }))));
    if (dq.level && dq.level !== 'Good') out.push(h('div', { class: 'rpt-note dq-' + dq.level }, h('strong', { text: 'Data quality: ' + dq.level }), ' Treat the conclusions with care.',
      dq.notes.length ? h('ul', null, dq.notes.map(function (n) { return h('li', { text: n }); })) : null));
    return out;
  }

  function kpis(A) {
    if (!A.kpis.length) return null;
    var s = sec('Key numbers', 'rpt-kpis'), g = h('div', { class: 'rpt-kpigrid' });
    A.kpis.forEach(function (k) {
      var st = String(k.status || 'neutral').toLowerCase();
      st = /good|ok|healthy/.test(st) ? 'good' : /warn/.test(st) ? 'warn' : /bad|crit|danger/.test(st) ? 'bad' : 'neutral';
      var stw = { good: 'OK', warn: 'Watch', bad: 'Attention' }[st];
      g.appendChild(h('div', { class: 'rpt-kpi k-' + st }, h('div', { class: 'l' }, h('span', { text: k.label }), stw ? h('span', { class: 'st', text: stw }) : null),
        h('div', { class: 'v' }, String(k.value === null || k.value === undefined ? '-' : k.value), k.unit ? h('small', { text: ' ' + k.unit }) : null),
        k.hint ? h('div', { class: 'hint', text: k.hint }) : null));
    });
    s.appendChild(g); return s;
  }

  function plan(A) {
    if (!A.actionPlan.length) return null;
    var s = sec('Fix first', 'rpt-plan'), ol = h('ol', { class: 'rpt-planlist' });
    A.actionPlan.forEach(function (a, i) {
      var tags = [];
      if (a.expectedImpact || a.expectedImpactPct) tags.push('Impact: ' + (a.expectedImpact || (num(a.expectedImpactPct, 0) + '% of ' + timeLabel(A))));
      if (a.effort) tags.push('Effort: ' + a.effort); if (a.risk) tags.push('Risk: ' + a.risk); if (a.confidence) tags.push('Confidence: ' + a.confidence);
      ol.appendChild(h('li', null, h('span', { class: 'rk', text: String(a.rank || i + 1) }), h('div', null, h('div', { class: 'pt', text: a.action }),
        a.why ? h('div', { class: 'rpt-muted', text: a.why }) : null, h('div', { class: 'pm', text: tags.join('   ·   ') }))));
    });
    s.appendChild(ol); return s;
  }

  function causes(A) {
    if (!A.rootCauses.length) return null;
    var s = sec('Root causes', 'rpt-causes'), ul = h('ul', { class: 'rpt-causelist' });
    A.rootCauses.forEach(function (r) {
      var titles = (r.findingIds || []).map(function (id) { var f = A.findings.filter(function (x) { return x.id === id; })[0]; return f ? f.title : null; }).filter(Boolean);
      ul.appendChild(h('li', null, h('b', { text: r.title }), r.impactPct ? h('span', { class: 'rpt-muted', text: '  (about ' + num(r.impactPct, 0) + '% of ' + timeLabel(A) + ')' }) : null,
        r.summary ? h('div', { text: r.summary }) : null, titles.length ? h('div', { class: 'rpt-muted', text: 'Related findings: ' + titles.join('; ') }) : null));
    });
    s.appendChild(ul); return s;
  }

  function sub(title, node) { return h('div', { class: 'rpt-sub' }, h('h4', { text: title }), node); }

  /* the "[3]" in front of a citation: a link to that entry of the reference list (it stays a link in the PDF) */
  function refMark(r) { return h('a', { class: 'rpt-refn', href: '#rpt-ref-' + r.n, text: '[' + r.n + ']' }); }

  function finding(A, f, n, refs) {
    var imp = Math.max(0, Math.min(100, +f.impact || 0));
    var card = h('article', { class: 'rpt-f sev-' + f.severity, id: 'rpt-f-' + n },
      h('header', { class: 'rpt-f-head' },
        h('span', { class: 'rpt-num', text: String(n) }),
        h('div', { class: 'rpt-f-title' }, h('h3', { text: f.title }),
          h('div', { class: 'rpt-f-top' }, f.category ? h('span', { class: 'rpt-cat', text: f.category }) : null,
            imp > 0 ? h('span', { class: 'rpt-imp', text: num(imp, 0) + '% of ' + timeLabel(A) }) : null, f.confidence ? h('span', { class: 'rpt-conf', text: 'Confidence: ' + f.confidence }) : null)),
        h('span', { class: 'rpt-badge sev-' + f.severity, text: SEV_LABEL[f.severity] || f.severity })),
      f.plain ? h('p', { class: 'rpt-plain', text: f.plain }) : null,
      f.dataQuality ? h('p', { class: 'rpt-muted', text: 'Data quality: ' + f.dataQuality }) : null,
      (f.whyItMatters || f.technical) ? h('div', { class: 'rpt-two' + (f.whyItMatters && f.technical ? '' : ' one') },
        f.whyItMatters ? sub('Why it matters', h('p', { text: f.whyItMatters })) : null,
        f.technical ? sub('Technical details', h('p', { text: f.technical })) : null) : null);
    if (f.evidence && f.evidence.length) {
      var tb = h('tbody');
      f.evidence.forEach(function (e) { tb.appendChild(h('tr', { class: (String(e.value).length + String(e.note || '').length) > 48 ? 'wide' : '' }, h('th', { scope: 'row', text: e.label }), h('td', null, h('b', { text: e.value }), e.note ? h('span', { class: 'rpt-muted', text: '  ' + e.note }) : null))); });
      card.appendChild(sub('Evidence from the report', h('table', { class: 'rpt-ev' }, tb)));
    }
    if (f.recommendations && f.recommendations.length) card.appendChild(sub('What to do', h('ol', { class: 'rpt-recs' }, f.recommendations.map(function (r) { return h('li', { text: r }); }))));
    if (f.sql && f.sql.length) {
      var sb = h('div');
      f.sql.forEach(function (s) { sb.appendChild(h('div', { class: 'rpt-sql' }, h('div', { class: 'rpt-sqlh', text: s.title || 'SQL' }), h('pre', null, h('code', { text: s.code })), s.caution ? h('div', { class: 'rpt-caution', text: 'Caution: ' + s.caution }) : null)); });
      card.appendChild(sub('SQL to run', sb));
    }
    if (f.docs && f.docs.length) {
      var ul = h('ul', { class: 'rpt-docs' });
      f.docs.forEach(function (d) {
        var u = safeUrl(d.url), r = refs.byUrl[d.url], title = d.title || d.url;
        ul.appendChild(h('li', null, r ? [refMark(r), ' '] : null, u ? extLink(u, title) : h('span', { text: title }), u ? ['  ', extLink(u, u, 'rpt-url')] : null));
      });
      card.appendChild(sub('Oracle documentation', ul));
    }
    if (f.sourceViews && f.sourceViews.length) card.appendChild(h('p', { class: 'rpt-muted rpt-src', text: 'Data comes from: ' + f.sourceViews.join(', ') }));
    return card;
  }

  function findings(A, refs) {
    var real = A.findings.filter(function (f) { return f.severity !== 'Ok'; }), ok = A.findings.filter(function (f) { return f.severity === 'Ok'; });
    var s = sec('Findings', 'rpt-findings');
    if (!A.findings.length) { s.appendChild(h('p', { text: 'No findings were produced for this report.' })); return [s]; }
    var counts = {}; A.findings.forEach(function (f) { counts[f.severity] = (counts[f.severity] || 0) + 1; });
    s.appendChild(h('p', { class: 'rpt-muted', text: A.findings.length + ' findings, most severe first: ' + SEV.filter(function (v) { return counts[v]; }).map(function (v) { return counts[v] + ' ' + SEV_LABEL[v].toLowerCase(); }).join(', ') + '.' }));
    if (real.length > 1) {
      var ib = h('tbody');
      real.forEach(function (f, i) {
        var imp = Math.max(0, Math.min(100, +f.impact || 0));
        ib.appendChild(h('tr', null, h('td', { class: 'n', text: String(i + 1) }), h('td', null, h('span', { class: 'rpt-badge sev-' + f.severity, text: SEV_LABEL[f.severity] || f.severity })),
          h('td', { text: f.title }), h('td', { class: 'rpt-muted', text: f.category || '' }), h('td', { class: 'n', text: imp > 0 ? num(imp, 0) + '%' : '' })));
      });
      s.appendChild(h('table', { class: 'rpt-idx' }, h('thead', null, h('tr', null, h('th', { class: 'n', text: '#' }), h('th', { text: 'Severity' }), h('th', { text: 'Finding' }), h('th', { text: 'Area' }), h('th', { class: 'n', text: 'Share of ' + timeLabel(A) }))), ib));
    }
    real.forEach(function (f, i) { s.appendChild(finding(A, f, i + 1, refs)); });
    var out = [s];
    if (ok.length) {
      var s2 = sec('Checked and healthy', 'rpt-okset'), ul = h('ul', { class: 'rpt-oklist' });
      ok.forEach(function (f, i) {
        var marks = (f.docs || []).map(function (d) { var r = refs.byUrl[d.url]; return r ? [' ', refMark(r)] : null; });
        ul.appendChild(h('li', { id: 'rpt-ok-' + (i + 1) }, h('b', { text: f.title }), f.plain ? h('span', { class: 'rpt-muted', text: '  ' + clip(f.plain, 220) }) : null, marks));
      });
      s2.appendChild(ul); out.push(s2);
    }
    return out;
  }

  function charts(A, o) {
    if (!A.charts.length || !window.AwrCharts || o.charts === false) return null;
    var s = sec('Charts', 'rpt-charts'), groups = {}, order = [];
    A.charts.forEach(function (c) { var g = c.group || 'Other'; if (!groups[g]) { groups[g] = []; order.push(g); } groups[g].push(c); });
    var toDraw = [];
    order.forEach(function (g) {
      s.appendChild(h('h3', { class: 'rpt-grp', text: g }));
      var grid = h('div', { class: 'rpt-chartgrid' });
      groups[g].forEach(function (c) {
        var box = h('div', { class: 'rpt-chartbox' });
        grid.appendChild(h('figure', { class: 'rpt-chart' + (c.type === 'line' || (c.labels || []).length > 14 ? ' wide' : '') },
          h('figcaption', null, h('b', { text: c.title }), c.subtitle ? h('span', { class: 'rpt-muted', text: '  ' + c.subtitle }) : null), box,
          c.explain ? h('p', { class: 'rpt-muted rpt-explain', text: c.explain }) : null));
        toDraw.push([box, c]);
      });
      s.appendChild(grid);
    });
    s._draw = function () {
      toDraw.forEach(function (x) {
        try { var spec = JSON.parse(JSON.stringify(x[1])); window.AwrCharts.render(x[0], spec, { animate: false, tooltip: false, height: 170 }); }
        catch (e) { x[0].appendChild(h('p', { class: 'rpt-muted', text: 'This chart could not be drawn.' })); }
      });
    };
    return s;
  }

  function topSql(A) {
    if (!A.topSql.length) return null;
    var s = sec('Top SQL statements', 'rpt-sqlset'), rows = A.topSql.slice(0, 15);
    var cols = [['SQL ID', function (r) { return r.sqlId; }], ['Module', function (r) { return clip(r.module, 28); }], ['Elapsed (s)', function (r) { return num(r.elapsedSec, 1); }, 1],
      ['CPU (s)', function (r) { return num(r.cpuSec, 1); }, 1], ['I/O wait (s)', function (r) { return num(r.ioWaitSec, 1); }, 1], ['Executions', function (r) { return num(r.executions, 0); }, 1],
      ['% ' + timeLabel(A), function (r) { return r.pctDbTime === null || r.pctDbTime === undefined ? '-' : num(r.pctDbTime, 1); }, 1]];
    var th = h('tr'); cols.forEach(function (c) { th.appendChild(h('th', { class: c[2] ? 'n' : '', text: c[0] })); });
    var tb = h('tbody'); rows.forEach(function (r) { var tr = h('tr'); cols.forEach(function (c) { tr.appendChild(h('td', { class: c[2] ? 'n' : '', text: c[1](r) })); }); tb.appendChild(tr); });
    s.appendChild(h('table', { class: 'rpt-grid' }, h('thead', null, th), tb));
    if (A.topSql.length > rows.length) s.appendChild(h('p', { class: 'rpt-muted', text: 'The ' + rows.length + ' heaviest of ' + A.topSql.length + ' statements are shown; the dashboard lists all of them.' }));
    var withText = rows.filter(function (r) { return r.text; }).slice(0, 5);
    if (withText.length) {
      s.appendChild(h('h3', { class: 'rpt-grp', text: 'Text of the heaviest statements' }));
      withText.forEach(function (r) { s.appendChild(h('div', { class: 'rpt-sql' }, h('div', { class: 'rpt-sqlh', text: r.sqlId + (r.module ? '  ·  ' + r.module : '') }), h('pre', null, h('code', { text: clip(r.text, 1200) })))); });
    }
    return s;
  }

  /* the consolidated reference list; each entry links to the findings that cite it */
  function referenceList(A, refs) {
    if (!refs.list.length) return null;
    var s = sec(REF_TITLE, 'rpt-refs'), ol = h('ol', { class: 'rpt-reflist' });
    var ok = A.findings.filter(function (f) { return f.severity === 'Ok'; }), real = A.findings.filter(function (f) { return f.severity !== 'Ok'; });
    function cite(f) {
      var healthy = f.severity === 'Ok', n = (healthy ? ok : real).indexOf(f) + 1;
      return h('li', null, h('a', { href: (healthy ? '#rpt-ok-' : '#rpt-f-') + n, text: (healthy ? 'Healthy check: ' : 'Finding ' + n + ': ') + f.title }));
    }
    s.appendChild(h('p', { class: 'rpt-muted', text: REF_NOTE }));
    refs.list.forEach(function (r) {
      var meta = [r.book, r.section ? 'Section: ' + r.section : ''].filter(Boolean).join('  ·  ');
      ol.appendChild(h('li', { id: 'rpt-ref-' + r.n }, h('span', { class: 'rk', text: '[' + r.n + ']' }), h('div', null,
        h('div', { class: 'pt' }, r.href ? extLink(r.href, r.title) : r.title),
        meta ? h('div', { class: 'rpt-muted', text: meta }) : null,
        r.href ? h('div', null, extLink(r.href, r.href, 'rpt-url')) : null,
        r.local ? h('div', { class: 'rpt-muted', text: 'Local reference (on the database host): ' + r.local }) : null,
        h('ul', { class: 'rpt-cited', 'aria-label': 'Cited by' }, r.findings.map(cite)))));
    });
    s.appendChild(ol); return s;
  }

  function facts(A) {
    var m = A.meta, s = sec('About this report', 'rpt-about');
    var rows = [['Database', [m.dbName, m.dbId ? '(' + m.dbId + ')' : ''].filter(Boolean).join(' ')], ['Instance', m.instance ? m.instance + (m.rac ? ' (RAC)' : '') : ''],
      ['Oracle release', m.release], ['Host', m.host], ['Platform', m.platform], ['CPUs / cores', m.cpus ? m.cpus + (m.cores ? ' / ' + m.cores : '') : ''],
      ['Memory', m.memoryGb ? num(m.memoryGb, 1) + ' GB' : ''], ['Container DB', m.cdb === true ? 'Yes' : m.cdb === false ? 'No' : ''],
      ['Snapshots', m.beginSnap && m.endSnap ? m.beginSnap + ' → ' + m.endSnap : ''], ['From', m.beginTime], ['To', m.endTime],
      ['Elapsed', m.elapsedMinutes ? num(m.elapsedMinutes, 1) + ' min' : ''], [typ(A) === 'ASH' ? 'Active session time' : 'DB time', m.dbTimeMinutes ? num(m.dbTimeMinutes, 1) + ' min' : ''],
      ['Avg active sessions', m.averageActiveSessions ? num(m.averageActiveSessions, 1) : ''], ['Number format', m.numberFormat], ['Source file', m.sourceFile],
      ['Analysed', [m.analyzedAt, m.analyzerVersion ? 'v' + m.analyzerVersion : ''].filter(Boolean).join('  ')]].filter(function (r) { return r[1]; });
    s.appendChild(h('dl', { class: 'rpt-meta' }, rows.map(function (r) { return h('div', null, h('dt', { text: r[0] }), h('dd', { text: r[1] })); })));
    if (A.warnings.length) s.appendChild(sub('Analysis notes', h('ul', null, A.warnings.map(function (w) { return h('li', { text: w }); }))));
    return s;
  }

  function foot(A, o) {
    return h('footer', { class: 'rpt-foot' },
      h('p', { text: 'Generated by ' + product(A) + (o.version ? ' ' + o.version : '') + '. The analysis is rule-based guidance computed from the report you supplied; verify important findings against your system before acting. ' +
        'Nothing in this document was sent anywhere: the report was analysed offline on this computer.' }),
      h('p', { class: 'rpt-muted', text: TM + ' Oracle, AWR, ASH and Statspack are trademarks of Oracle Corporation. ' + product(A) + ' is not an Oracle product and is not affiliated with, sponsored by or endorsed by Oracle.' + (A.meta.edition === 'Community' || !A.meta.edition ? ' Licensed AGPL-3.0-only, without warranty.' : '') }));
  }

  /** Builds the report element. analysis: the dashboard payload's analysis object (already normalised by the app). */
  function build(analysis, o) {
    o = o || {}; o.now = o.now || new Date();
    var A = analysis;
    A.meta = A.meta || {}; A.health = A.health || { score: 0, grade: '', headline: '', paragraphs: [], counts: {} };
    ['kpis', 'findings', 'charts', 'topSql', 'warnings', 'rootCauses', 'actionPlan', 'docs'].forEach(function (k) { A[k] = A[k] || []; });
    A.dataQuality = A.dataQuality || { level: 'Good', notes: [] }; A.dataQuality.notes = A.dataQuality.notes || []; A.dataQuality.alerts = A.dataQuality.alerts || [];
    var root = h('div', { class: 'rpt', lang: 'en' });
    /* running footer of every printed page: product name and version (build constants, never report text) beside the page counter */
    o.version = versionText(o.version, A.meta.analyzerVersion);
    root.appendChild(h('style', { text: '@page { @bottom-left { content: "' + (product(A) + (o.version ? ' ' + o.version : '')).replace(/["\\]/g, '') + '"; } }' }));
    add(root, cover(A, o)); add(root, health(A)); add(root, quality(A)); add(root, kpis(A)); add(root, plan(A)); add(root, causes(A));
    var refs = references(A);
    add(root, findings(A, refs));
    var ch = charts(A, o); add(root, ch); add(root, topSql(A)); add(root, referenceList(A, refs)); add(root, facts(A)); add(root, foot(A, o));
    root._afterAttach = function () { if (ch && ch._draw) ch._draw(); };
    return root;
  }

  window.AwrReport = { build: build, versionText: versionText, references: references, referencesTitle: REF_TITLE, referencesNote: REF_NOTE, version: '1.0' };
})();
