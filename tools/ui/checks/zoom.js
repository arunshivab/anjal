// rc.27 (item 79, brief 10e; D-313): text at 200% - every screen of a 1280x800 screen zoomed to 200%
// (640x400 to the page): no sideways scroll of the whole page, nothing pushed off the side. Listed in
// out/zoom.txt for the record of which pages pass already, not failed: these widths are the tablet
// layouts rc.28 builds, and 400% reflow (320 wide) is checked with the phone screens then.
'use strict';
const L = require('../lib.js');

module.exports = {
  name: 'zoom',
  title: 'Text at 200%: no sideways page scroll (listed; failed from rc.28)',
  reportOnly: true,
  async run(screens) {
    const found = [];
    const w = await L.open([640, 400]);
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url, 250))) {
          continue;
        }
        const over = await w.page.evaluate(() => {
          const se = document.scrollingElement || document.documentElement;
          let off = 0;
          for (const e of document.querySelectorAll('button, a.btn, input, select, textarea, h1, h2')) {
            const r = e.getBoundingClientRect();
            if (r.width > 2 && r.left >= innerWidth - 1) { off++; }
          }
          return [se.scrollWidth - innerWidth, off];
        });
        if (over[0] > 1) { found.push(`${url}: the page scrolls sideways by ${over[0]}px`); }
        if (over[1] > 0) { found.push(`${url}: ${over[1]} control(s) or heading(s) beyond the right edge`); }
      }
    } finally {
      await w.close();
    }
    return found;
  },
};
