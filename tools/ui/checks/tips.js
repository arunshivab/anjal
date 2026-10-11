// Item 5 and UX-09 (owner, 7 Oct 2026): every text button carries a short tip saying what it
// does, in the person's language - in the mail screens and in the organisation and Anjal
// consoles alike. Buttons inside menus and windows are already explained by their place.
'use strict';
const L = require('../lib.js');

module.exports = {
  name: 'tips',
  title: 'Every text button has a tip (item 5, UX-09)',
  async run(screens) {
    const found = new Map();
    const w = await L.open(L.SMALL);
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url))) {
          continue;
        }
        const missing = await w.page.evaluate(() => {
          const out = [];
          for (const b of document.querySelectorAll('.btn, .eb, .pbtn, .linkbtn, .ctempl, .eback')) {
            if (b.closest('.pmenu-body, .emenu, dialog, .modal')) {
              continue;
            }
            const r = b.getBoundingClientRect();
            if (!r.width || !r.height || getComputedStyle(b).visibility === 'hidden') {
              continue;
            }
            const c = b.cloneNode(true);
            c.querySelectorAll('[aria-hidden=true]').forEach((h) => h.remove());
            const words = (c.textContent || '').replace(/\s+/g, ' ').trim();
            if (words && !b.hasAttribute('data-tip') && !b.hasAttribute('title')) {
              out.push(words);
            }
          }
          return out;
        });
        for (const words of missing) {
          if (!found.has(words)) {
            found.set(words, `"${words}" has no tip (first seen on ${url})`);
          }
        }
      }
    } finally {
      await w.close();
    }
    return [...found.values()];
  },
};
