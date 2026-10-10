// Owner, 25 Sep and again 9 Oct 2026 ("the attachment icon or lock icon shall not displace the text"):
// in every mail list every subject starts at the same place - the lock has its own slot, kept empty
// when there is no lock, and the paper clip sits in its own column - with nothing open, with a
// message open, and on a phone.
'use strict';
const L = require('../lib.js');

const PHONE = [390, 844];

module.exports = {
  name: 'lineup',
  title: 'Every subject in a list starts at the same place, lock or no lock',
  async run(screens) {
    const found = new Map();
    let lists = 0;
    for (const size of [L.SMALL, L.MID, L.LARGE, PHONE]) {
      const w = await L.open(size);
      try {
        for (const url of screens) {
          if (!(await L.visit(w, url, 300))) { continue; }
          const r = await w.page.evaluate(() => {
            const rows = [...document.querySelectorAll('[data-list] .mrow')].filter((m) => m.getBoundingClientRect().height > 0);
            if (rows.length < 2) { return null; }
            const at = rows.map((m) => { const s = m.querySelector('.subj'); return s ? [Math.round(s.getBoundingClientRect().left - m.getBoundingClientRect().left), !!m.querySelector('.unenc'), s.textContent.trim().slice(0, 30)] : null; }).filter(Boolean);
            const first = at[0][0];
            const odd = at.find((a) => Math.abs(a[0] - first) > 1);
            return { n: at.length, odd: odd ? `"${odd[2]}" starts at ${odd[0]}px, "${at[0][2]}" at ${first}px (lock: ${odd[1] ? 'yes' : 'no'} / ${at[0][1] ? 'yes' : 'no'})` : null };
          });
          if (!r) { continue; }
          lists++;
          if (r.odd && !found.has(url)) { found.set(url, `${size.join('x')} ${url}: ${r.odd}`); }
        }
      } finally {
        await w.close();
      }
    }
    if (lists === 0) { return ['no mail list was found to check']; }
    return [...found.values()];
  },
};
