// Owner, 8 Oct 2026 ("when collapsed the position of icons should remain exactly same"), system-wide:
// what frames every screen stays exactly in place - the rail's icons, with folder names showing and
// folded, and the status bar - on every screen, whatever is open beside it.
'use strict';
const L = require('../lib.js');

function frame() {
  const out = {};
  const pos = (e) => { const q = e.getBoundingClientRect(); return q.width ? Math.round(q.left) + ',' + Math.round(q.top) : null; };
  document.querySelectorAll('nav.rail a.rail-item, nav.rail .rail-mark .mark-tile').forEach((a) => {
    const s = a.matches('.mark-tile') ? a : a.querySelector('svg');
    const k = a.matches('.mark-tile') ? 'mark' : (a.getAttribute('href') || a.getAttribute('title'));
    const p = s && pos(s);
    if (p) { out['rail ' + k] = p; }
  });
  document.querySelectorAll('.statusbar > *').forEach((e, i) => { const p = pos(e); if (p) { out['status bar ' + (e.className || i)] = p; } });
  // DEF-106: with each status bar item, what it said and how wide it was, so a report names the
  // text that changed width rather than only the items it pushed.
  const said = {};
  document.querySelectorAll('.statusbar > *').forEach((e) => { const q = e.getBoundingClientRect(); if (q.width) { said[e.className || 'item'] = '"' + e.textContent.trim().replace(/\s+/g, ' ').slice(0, 40) + '" ' + q.width.toFixed(1) + 'px'; } });
  return { out, said };
}

async function setFold(w, folded) {
  await L.visit(w, '/folder/INBOX', 300);
  const b = await w.page.$(folded ? 'form.rail-fold button.rf-fold' : 'form.rail-fold button.rf-open');
  if (b && await b.isVisible()) { await Promise.all([w.page.waitForNavigation(), b.click()]); }
}

module.exports = {
  name: 'steady',
  title: 'The rail and the status bar stay exactly in place on every screen',
  async run(screens) {
    const found = [];
    let checked = 0;
    for (const size of [L.SMALL, L.MID, L.LARGE]) {
      const w = await L.open(size);
      try {
        const ref = {};
        for (const folded of [false, true]) {
          await setFold(w, folded);
          const state = folded ? 'folded' : 'with names';
          for (const url of screens) {
            if (!(await L.visit(w, url, 250))) { continue; }
            const { out: f, said } = await w.page.evaluate(frame);
            for (const [k, p] of Object.entries(f)) {
              checked++;
              // Folded and open, each rail icon keeps the same place; the status bar too.
              if (ref[k] === undefined) { ref[k] = { p, url, state, said }; continue; }
              const [ax, ay] = ref[k].p.split(',').map(Number); const [bx, by] = p.split(',').map(Number);
              if (Math.abs(ax - bx) > 1 || Math.abs(ay - by) > 1) {
                const was = ref[k].said || {};
                const changed = Object.keys(said).filter((c) => was[c] !== undefined && was[c] !== said[c]).map((c) => `${c} ${was[c]} -> ${said[c]}`);
                found.push(`${size.join('x')} ${state} ${url}: ${k} at ${p}, but at ${ref[k].p} on ${ref[k].url} (${ref[k].state})` + (k.startsWith('status bar') && changed.length ? `; status bar text that changed: ${changed.join('; ')}` : ''));
                ref[k] = { p, url, state, said };
              }
            }
          }
        }
        await setFold(w, false);
      } finally {
        await w.close();
      }
    }
    // One report per thing that moves is enough to find it.
    const seen = new Set();
    const list = found.filter((x) => { const k = x.replace(/ at .*$/, '').replace(/^\S+ \S+( names)? \S+: /, ''); if (seen.has(k)) { return false; } seen.add(k); return true; });
    list.checked = checked;
    return list;
  },
};
