// Owner, 7 Oct 2026: every menu closes when you click anywhere outside it, and with Escape.
// Each kind of menu is tried once (the same menu on many screens behaves the same), on the
// first screen it appears on, clicking empty places around the screen.
'use strict';
const L = require('../lib.js');

module.exports = {
  name: 'close',
  title: 'Every menu closes on a click outside and on Escape',
  async run(screens) {
    const found = [];
    const tried = new Set();
    const w = await L.open(L.SMALL);
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url))) { continue; }
        const menus = await w.page.evaluate(() => [...document.querySelectorAll('details')].map((d, i) => {
          const s = d.querySelector(':scope > summary');
          const r = s ? s.getBoundingClientRect() : { width: 0 };
          return { i, key: (d.className || 'details') + '|' + (s ? s.textContent.trim().replace(/\s+/g, ' ').slice(0, 30) : ''), shown: r.width > 0 };
        }).filter((m) => m.shown));
        for (const m of menus) {
          if (tried.has(m.key)) { continue; }
          for (const how of ['click', 'Escape']) {
            if (!(await L.visit(w, url))) { break; }
            const opened = await w.page.evaluate(async (i) => {
              const d = document.querySelectorAll('details')[i];
              const s = d && d.querySelector(':scope > summary');
              if (!s) { return false; }
              s.click();
              await new Promise((r) => setTimeout(r, 120));
              const b = [...d.children].find((c) => c.tagName !== 'SUMMARY');
              if (!b || !['absolute', 'fixed'].includes(getComputedStyle(b).position)) { if (d.open) { s.click(); } return false; }
              return d.open;
            }, m.i);
            if (!opened) { break; }
            tried.add(m.key);
            if (how === 'Escape') {
              await w.page.keyboard.press('Escape');
            } else {
              // An empty spot outside the menu: not a control, not inside the menu.
              const spot = await w.page.evaluate((i) => {
                const d = document.querySelectorAll('details')[i];
                for (const [fx, fy] of [[0.5, 0.6], [0.85, 0.75], [0.2, 0.8], [0.5, 0.95], [0.95, 0.5], [0.3, 0.4]]) {
                  const x = innerWidth * fx; const y = innerHeight * fy;
                  const e = document.elementFromPoint(x, y);
                  if (e && !d.contains(e) && !e.closest('a,button,input,select,label,summary,textarea,[contenteditable],iframe,[data-open]')) { return [x, y]; }
                }
                return null;
              }, m.i);
              if (!spot) { continue; }
              await w.page.mouse.click(spot[0], spot[1]);
            }
            await w.page.waitForTimeout(200);
            const still = await w.page.evaluate((i) => { const d = document.querySelectorAll('details')[i]; return !!(d && d.open); }, m.i).catch(() => false);
            if (still) {
              found.push(`${url}: ${m.key.replace('|', ' "')}" stays open after ${how === 'click' ? 'a click outside' : 'Escape'}`);
            }
          }
        }
      }
    } finally {
      await w.close();
    }
    found.checked = tried.size;
    return found;
  },
};
