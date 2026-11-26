// rc.31 (D1, owner, 25 Nov 2026: "pdf is small and csv is long"): every file label is one size, wherever it is
// shown - the mail's files, compose, a draft's file and the dashboards - whatever the name beside it.
'use strict';
const L = require('../lib.js');

module.exports = {
  name: 'tags',
  title: 'Every file label is one size (42x28), whatever the name beside it',
  async run(screens) {
    const found = [];
    let checked = 0;
    const w = await L.open(L.MID);
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url, 300))) { continue; }
        const tags = await w.page.evaluate(() => [...document.querySelectorAll('.ftag')].map((t) => {
          const q = t.getBoundingClientRect();
          return { w: Math.round(t.offsetWidth), h: Math.round(t.offsetHeight), text: t.textContent.trim(), shown: q.width > 0 && q.height > 0 };
        }).filter((t) => t.shown));
        checked += tags.length;
        tags.filter((t) => t.w !== 42 || t.h !== 28).forEach((t) => found.push(`${L.MID.join('x')} ${url}: the label "${t.text}" is ${t.w}x${t.h}, not 42x28`));
      }
    } finally {
      await w.close();
    }
    if (checked === 0) { return ['no file label was found to check']; }
    found.checked = checked;
    return found;
  },
};
