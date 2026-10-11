// Owner, 7 Oct 2026 (the Details panel jumped 14px): nothing inside a scrolling column is
// squeezed smaller than its content - with every inline section open and closed.
'use strict';
const L = require('../lib.js');

module.exports = {
  name: 'shrink',
  title: 'Nothing squeezed inside scrolling columns',
  async run(screens) {
    const found = new Map();
    const w = await L.open(L.SMALL);
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url, 300))) { continue; }
        for (const state of ['closed', 'open']) {
          if (state === 'open') {
            await w.page.evaluate(() => document.querySelectorAll('details').forEach((d) => {
              const b = [...d.children].find((c) => c.tagName !== 'SUMMARY');
              if (b && !['absolute', 'fixed'].includes(getComputedStyle(b).position)) { d.open = true; }
            }));
            await w.page.waitForTimeout(150);
          }
          const r = await w.page.evaluate(() => {
            const out = [];
            for (const c of document.querySelectorAll('*')) {
              const cs = getComputedStyle(c);
              if (!cs.display.includes('flex') || !cs.flexDirection.startsWith('column') || !/auto|scroll|hidden/.test(cs.overflowY)) { continue; }
              for (const k of c.children) {
                const ks = getComputedStyle(k);
                // A part that scrolls by itself (the letter being written) is meant to take the room left.
                if (ks.position === 'absolute' || ks.position === 'fixed' || ks.display === 'none' || parseFloat(ks.flexShrink) === 0 || /auto|scroll/.test(ks.overflowY)) { continue; }
                const h1 = k.getBoundingClientRect().height; const old = k.style.flexShrink; k.style.flexShrink = '0';
                const h2 = k.getBoundingClientRect().height; k.style.flexShrink = old;
                if (h2 - h1 > 0.5) { out.push([String(c.className).split(' ')[0] + ' > ' + k.tagName.toLowerCase() + '.' + [...k.classList].join('.'), h1.toFixed(1) + 'px instead of ' + h2.toFixed(1) + 'px']); }
              }
            }
            return out;
          });
          for (const [who, how] of r) {
            if (!found.has(who)) { found.set(who, `${url} [${state}]: ${who} squeezed to ${how}`); }
          }
        }
      }
    } finally {
      await w.close();
    }
    return [...found.values()];
  },
};
