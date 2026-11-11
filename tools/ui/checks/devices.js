// rc.28 (DES-18 §2-§7, §13; R9 [9a]-[9e]): every screen on phones and tablets, upright and sideways, with
// a finger instead of a mouse - 375x812, 390x844 (phones), 844x390 (a phone sideways, which gets the phone
// layout), 768x1024 and 820x1180 (tablets upright), 1024x768 and 1180x820 (tablets sideways). On each: the page never scrolls sideways; on a mail, the
// envelope is beside the letter, never above or below it (a phone shows its strip instead, and no
// envelope); and every control a finger uses is at least 44px tall (listed: the sizes check holds the
// mouse sizes, D-149).
'use strict';
const L = require('../lib.js');

const DEVICES = [
  [375, 812, { touch: true, mobile: true }],
  [390, 844, { touch: true, mobile: true }],
  [844, 390, { touch: true, mobile: true }],
  [768, 1024, { touch: true }],
  [820, 1180, { touch: true }],
  [1024, 768, { touch: true }],
  [1180, 820, { touch: true }],
];

module.exports = {
  name: 'devices',
  title: 'Phones and tablets: no sideways scroll, the envelope beside the letter, 44px for a finger',
  async run(screens) {
    const found = [];
    const small = new Map();
    for (const size of DEVICES) {
      const w = await L.open(size);
      try {
        for (const url of screens) {
          if (!(await L.visit(w, url, 300))) {
            continue;
          }
          const r = await w.page.evaluate(() => {
            const se = document.scrollingElement || document.documentElement;
            const out = { over: se.scrollWidth - innerWidth, envelope: null, tiny: [] };
            const vis = (e) => { const b = e.getBoundingClientRect(); const cs = getComputedStyle(e); return b.width > 0 && b.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
            const env = document.querySelector('aside.envelope');
            const letter = document.querySelector('.mview > .letter');
            if (env && letter && vis(env) && vis(letter)) {
              const a = env.getBoundingClientRect();
              const l = letter.getBoundingClientRect();
              // Beside: they share height on the screen, and the envelope starts where the letter ends (or before it, mirrored).
              const beside = a.top < l.bottom - 20 && l.top < a.bottom - 20 && (a.left >= l.right - 1 || a.right <= l.left + 1);
              if (!beside && !env.matches(':target')) { out.envelope = `envelope at ${Math.round(a.left)},${Math.round(a.top)}, letter at ${Math.round(l.left)},${Math.round(l.top)}-${Math.round(l.right)},${Math.round(l.bottom)}`; }
            }
            for (const e of document.querySelectorAll('main button, main a.btn, main a.iconbtn, main select, .phonenav a')) {
              if (!vis(e) || e.closest('[hidden], details:not([open]) > :not(summary)')) { continue; }
              const b = e.getBoundingClientRect();
              if (b.height < 43.5) { out.tiny.push((e.getAttribute('aria-label') || e.textContent || e.className || e.tagName).trim().replace(/\s+/g, ' ').slice(0, 40) + ` (${Math.round(b.height)}px)`); }
            }
            return out;
          });
          const where = `${size[0]}x${size[1]} ${url}`;
          if (r.over > 1) { found.push(`${where}: the page scrolls sideways by ${r.over}px`); }
          if (r.envelope) { found.push(`${where}: the envelope is not beside the letter (${r.envelope})`); }
          for (const t of r.tiny) {
            const k = `${size[0]}x${size[1]}|${t.replace(/ \(\d+px\)$/, '')}`;
            if (!small.has(k)) { small.set(k, `${where}: smaller than a finger: ${t}`); }
          }
        }
      } finally {
        await w.close();
      }
    }
    require('fs').writeFileSync(require('path').join(L.out, 'devices-touch.txt'), [...small.values()].join('\n') + '\n');
    return found;
  },
};
