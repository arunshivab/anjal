// Owner, 10 Oct 2026 ("please check all scroll bars"): nothing is ever drawn over a scroll bar.
// Every box that scrolls on every screen is found, with real scroll bars drawn as on Windows, and
// any mark, badge, button or panel outside that box that covers its scroll bar is listed - as the
// marigold flap did over the mail list. Measured at 1366x768, 1536x730 and 1920x1080.
'use strict';
const L = require('../lib.js');

function measure() {
  const out = [];
  const shown = (e) => { const cs = getComputedStyle(e); return cs.visibility !== 'hidden' && cs.display !== 'none' && Number(cs.opacity) > 0.05; };
  const name = (e) => (e.getAttribute('aria-label') || e.getAttribute('data-tip') || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 30);
  const cls = (e) => (e.className && e.className.baseVal !== undefined ? e.className.baseVal : (e.className || '').toString()).split(/\s+/).filter(Boolean).slice(0, 3).join('.') || e.tagName.toLowerCase();
  const scrollers = [...document.querySelectorAll('*')].filter((s) => {
    if (!shown(s)) { return false; }
    const cs = getComputedStyle(s);
    const v = /auto|scroll/.test(cs.overflowY) && s.offsetWidth - s.clientWidth - s.clientLeft * 2 > 2;
    const h = /auto|scroll/.test(cs.overflowX) && s.offsetHeight - s.clientHeight - s.clientTop * 2 > 2;
    return v || h;
  });
  const all = [...document.querySelectorAll('body *')].filter((e) => !e.closest('template, .sr-only') && shown(e));
  for (const s of scrollers) {
    const q = s.getBoundingClientRect();
    const cs = getComputedStyle(s);
    const strips = [];
    if (/auto|scroll/.test(cs.overflowY) && s.offsetWidth - s.clientWidth - s.clientLeft * 2 > 2) {
      const left = q.left + s.clientLeft + s.clientWidth;
      strips.push({ kind: 'scroll bar', l: left, r: left + (s.offsetWidth - s.clientWidth - s.clientLeft * 2), t: q.top + s.clientTop, b: q.top + s.clientTop + s.clientHeight });
    }
    if (/auto|scroll/.test(cs.overflowX) && s.offsetHeight - s.clientHeight - s.clientTop * 2 > 2) {
      const top = q.top + s.clientTop + s.clientHeight;
      strips.push({ kind: 'bottom scroll bar', l: q.left + s.clientLeft, r: q.left + s.clientLeft + s.clientWidth, t: top, b: top + (s.offsetHeight - s.clientHeight - s.clientTop * 2) });
    }
    for (const st of strips) {
      // Only the part of the scroll bar on screen.
      st.t = Math.max(st.t, 0); st.b = Math.min(st.b, innerHeight); st.l = Math.max(st.l, 0); st.r = Math.min(st.r, innerWidth);
      if (st.b - st.t < 4 || st.r - st.l < 2) { continue; }
      for (const e of all) {
        if (e === s || s.contains(e) || e.contains(s)) { continue; }
        const r = e.getBoundingClientRect();
        const w = Math.min(r.right, st.r) - Math.max(r.left, st.l);
        const h = Math.min(r.bottom, st.b) - Math.max(r.top, st.t);
        if (w > 1 && h > 1) {
          // Behind the scrolling box, not over it: a background the box is laid on.
          const z = (x) => { const v = getComputedStyle(x).zIndex; return v === 'auto' ? 0 : Number(v); };
          // Laid out in the normal flow beside the box, it cannot be over it; only positioned or
          // moved marks can.
          const es = getComputedStyle(e);
          if (es.position === 'static' && es.transform === 'none' && Number(es.marginLeft.replace('px', '')) >= 0) { continue; }
          if (z(e) < z(s)) { continue; }
          out.push(`"${name(e)}" [${cls(e)}] covers the ${st.kind} of [${cls(s)}] by ${Math.round(w)}x${Math.round(h)}px`);
        }
      }
    }
  }
  return { boxes: scrollers.length, found: [...new Set(out)] };
}

module.exports = {
  name: 'scrollbars',
  title: 'Nothing is drawn over a scroll bar',
  async run(screens) {
    const found = new Map();
    let boxes = 0;
    for (const size of [L.SMALL, L.MID, L.LARGE]) {
      const w = await L.open(size);
      try {
        for (const url of screens) {
          if (!(await L.visit(w, url, 300))) { continue; }
          const r = await w.page.evaluate(measure);
          boxes += r.boxes;
          for (const f of r.found) {
            const key = L.shape(url) + ' ' + f.replace(/"[^"]*"/, '');
            if (!found.has(key)) { found.set(key, `${size.join('x')} ${url}: ${f}`); }
          }
        }
      } finally {
        await w.close();
      }
    }
    if (boxes === 0) { return ['no box with a scroll bar was found - are scroll bars drawn?']; }
    return [...found.values()];
  },
};
