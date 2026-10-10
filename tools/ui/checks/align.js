// Owner, 7 Oct 2026 ("the arrow in the compose button is not aligned and it moves up and down"):
// every button's icon and words sit centred, buttons side by side line up, and nothing moves
// when the pointer rests on it or when its menu opens. Each distinct button is tried once.
'use strict';
const L = require('../lib.js');

const SEL = 'button, summary, a.btn, a.pbtn, a.iconbtn, a.eb, a.rail-item, a.rail-compose, a.pgbtn, a.linkbtn, a.eback, a.ctempl, a.pill, .seg a, a.chip';

function still(SEL) {
  const out = [];
  const vis = (e) => { const r = e.getBoundingClientRect(); const cs = getComputedStyle(e); return r.width > 4 && r.height > 4 && cs.visibility !== 'hidden' && cs.display !== 'none' && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; };
  const name = (e) => e.tagName.toLowerCase() + '.' + [...e.classList].filter((c) => !/menu-lift|^on$/.test(c)).join('.') + ' "' + (e.getAttribute('aria-label') || e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 28) + '"';
  // A count placed on a corner of the button (the folded rail's unread count) is not its words.
  const badge = (x, e) => { for (; x && x !== e; x = x.parentElement) { if (['absolute', 'fixed'].includes(getComputedStyle(x).position)) { return true; } } return false; };
  const els = [...document.querySelectorAll(SEL)].filter(vis);
  for (const e of els) {
    const r = e.getBoundingClientRect(); const cy = r.top + r.height / 2; const cx = r.left + r.width / 2;
    const svgs = [...e.querySelectorAll('svg')].filter((s) => s.getBoundingClientRect().width > 0 && !s.closest('.pmenu-body,.rte-menu') && s.closest(SEL) === e);
    let tr = null;
    const walker = document.createTreeWalker(e, NodeFilter.SHOW_TEXT, { acceptNode: (n) => (n.textContent.trim() && !n.parentElement.closest('.pmenu-body,.rte-menu,[aria-hidden=true]') && n.parentElement.closest(SEL) === e && getComputedStyle(n.parentElement).display !== 'none' && !badge(n.parentElement, e) ? 1 : 2) });
    const rng = document.createRange(); const rects = []; let n;
    while ((n = walker.nextNode())) { rng.selectNodeContents(n); for (const q of rng.getClientRects()) { if (q.width > 0) { rects.push(q); } } }
    if (rects.length) { const t = Math.min(...rects.map((q) => q.top)); const b = Math.max(...rects.map((q) => q.bottom)); tr = { t, b, c: (t + b) / 2 }; }
    if (svgs.length === 1 && r.height >= 16) {
      const s = svgs[0].getBoundingClientRect(); const sy = s.top + s.height / 2; const sx = s.left + s.width / 2;
      if (!tr) {
        if (Math.abs(sy - cy) > 1.5 || Math.abs(sx - cx) > 1.5) { out.push(['icon off centre', name(e), 'by ' + (sx - cx).toFixed(1) + ', ' + (sy - cy).toFixed(1) + 'px']); }
      } else if (tr.b - tr.t < r.height * 0.8 && rects.length <= 2 && Math.abs(sy - tr.c) > 2) {
        out.push(['icon not level with its words', name(e), (sy - tr.c).toFixed(1) + 'px']);
      }
    }
    if (tr && !svgs.length && rects.length <= 2 && r.height >= 20 && (tr.b - tr.t) < r.height * 0.8 && Math.abs(tr.c - cy) > 2.5) {
      out.push(['words off centre', name(e), (tr.c - cy).toFixed(1) + 'px']);
    }
  }
  for (const p of new Set(els.map((e) => e.parentElement))) {
    const cs = getComputedStyle(p);
    if (!(cs.display.includes('flex') && cs.flexDirection.startsWith('row'))) { continue; }
    const kids = [...p.children].filter((k) => els.includes(k));
    const rs = kids.map((k) => k.getBoundingClientRect());
    for (let i = 1; i < kids.length; i++) {
      const a = rs[i - 1]; const b = rs[i];
      if (Math.abs(a.top - b.top) > a.height) { continue; }
      const d = (a.top + a.height / 2) - (b.top + b.height / 2);
      if (Math.abs(d) > 2) { out.push(['side by side but not level', name(kids[i - 1]) + ' / ' + name(kids[i]), d.toFixed(1) + 'px']); }
    }
  }
  return out;
}

module.exports = {
  name: 'align',
  title: 'Buttons centred, level, and still on hover and when opened',
  async run(screens) {
    const found = new Map();
    const moved = new Set();
    const w = await L.open(L.SMALL);
    const box = ([SEL, i]) => { const e = document.querySelectorAll(SEL)[i]; if (!e) { return null; } const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height }; };
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url, 500))) { continue; }
        for (const [what, who, how] of await w.page.evaluate(still, SEL)) {
          const k = what + '|' + who;
          if (!found.has(k)) { found.set(k, `${url}: ${what}: ${who} (${how})`); }
        }
        const n = await w.page.$$eval(SEL, (els) => els.length);
        for (let i = 0; i < n; i++) {
          const info = await w.page.evaluate(([SEL, i]) => {
            const e = document.querySelectorAll(SEL)[i]; if (!e) { return null; }
            const r = e.getBoundingClientRect(); const cs = getComputedStyle(e);
            if (!(r.width > 4 && r.height > 4 && cs.visibility !== 'hidden' && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth)) { return null; }
            const x = r.left + r.width / 2; const y = r.top + r.height / 2; const t = document.elementFromPoint(x, y);
            if (!t || !(e === t || e.contains(t))) { return null; }
            return { x, y, summary: e.tagName === 'SUMMARY', name: e.tagName.toLowerCase() + '.' + [...e.classList].filter((c) => !/menu-lift|^on$/.test(c)).join('.') + ' "' + (e.getAttribute('aria-label') || e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 28) + '"' };
          }, [SEL, i]);
          if (!info || moved.has(info.name)) { continue; }
          moved.add(info.name);
          await w.page.mouse.move(info.x, info.y);
          await w.page.waitForTimeout(260);
          const h = await w.page.evaluate(box, [SEL, i]);
          if (h && (Math.abs(h.x - info.x) > 0.75 || Math.abs(h.y - info.y) > 0.75)) {
            found.set('hover|' + info.name, `${url}: moves when the pointer rests on it: ${info.name} (${(h.x - info.x).toFixed(1)}, ${(h.y - info.y).toFixed(1)}px)`);
          }
          if (info.summary && h) {
            await w.page.evaluate(([SEL, i]) => { document.querySelectorAll(SEL)[i].parentElement.open = true; }, [SEL, i]);
            await w.page.waitForTimeout(260);
            const o = await w.page.evaluate(box, [SEL, i]);
            if (o && (Math.abs(o.x - h.x) > 0.75 || Math.abs(o.y - h.y) > 0.75 || Math.abs(o.w - h.w) > 0.75 || Math.abs(o.h - h.h) > 0.75)) {
              found.set('open|' + info.name, `${url}: moves when its menu opens: ${info.name} (${(o.x - h.x).toFixed(1)}, ${(o.y - h.y).toFixed(1)}px; size ${(o.w - h.w).toFixed(1)} x ${(o.h - h.h).toFixed(1)})`);
            }
            await w.page.evaluate(([SEL, i]) => { document.querySelectorAll(SEL)[i].parentElement.open = false; }, [SEL, i]);
            await w.page.waitForTimeout(120);
          }
        }
        await w.page.mouse.move(1, 1);
      }
    } finally {
      await w.close();
    }
    const list = [...found.values()];
    list.checked = moved.size;
    return list;
  },
};
