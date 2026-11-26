// rc.31 (D9, owner, 26 Nov 2026): the editor's Save showed a text cursor and its word could be selected,
// because it is a menu (a summary) drawn as a button. Every control a person presses - a button, a link,
// a menu's summary, a label or anything drawn as a button - shows the hand, and a button's words cannot
// be selected by dragging over it. Fields, and controls that are switched off, are left as they are.
'use strict';
const L = require('../lib.js');

module.exports = {
  name: 'cursor',
  title: 'Every control shows the hand, and a button\'s words cannot be selected (D9)',
  async run(screens) {
    const found = new Map();
    let checked = 0;
    const w = await L.open(L.MID);
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url))) {
          continue;
        }
        checked++;
        const wrong = await w.page.evaluate(() => {
          const out = [];
          const controls = 'button, a[href], summary, label.btn, [role="button"], .btn';
          for (const c of document.querySelectorAll(controls)) {
            if (c.matches('[disabled], [aria-disabled="true"], input, select, textarea') || c.closest('[disabled], fieldset[disabled]')) {
              continue;
            }
            const r = c.getBoundingClientRect();
            const s = getComputedStyle(c);
            if (!r.width || !r.height || s.visibility === 'hidden' || s.display === 'none') {
              continue;
            }
            const words = (c.getAttribute('aria-label') || c.getAttribute('title') || c.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40)
              || c.tagName.toLowerCase();
            const what = c.tagName.toLowerCase() + (c.className && typeof c.className === 'string' ? '.' + c.className.trim().split(/\s+/)[0] : '');
            if (s.cursor !== 'pointer') {
              out.push(`${what} "${words}" shows the ${s.cursor} cursor, not the hand`);
            }
            const pressable = c.matches('button, summary, label.btn, [role="button"], .btn');
            if (pressable && s.userSelect !== 'none' && s.webkitUserSelect !== 'none' && (c.textContent || '').trim()) {
              out.push(`${what} "${words}": its words can be selected`);
            }
          }
          return out;
        });
        for (const line of wrong) {
          if (!found.has(line)) {
            found.set(line, `${line} (first seen on ${url})`);
          }
        }
      }
    } finally {
      await w.close();
    }
    if (checked === 0) { return ['no screen could be opened to check']; }
    const list = [...found.values()];
    list.checked = checked;
    return list;
  },
};
