// UX-09 (owner, 7 Oct 2026): every control says what it is to a screen reader - every button,
// link, box, choice and picture has a name - checked with axe-core on every screen, with its
// menus closed and open.
'use strict';
const L = require('../lib.js');

const RULES = [
  'button-name', 'link-name', 'input-button-name', 'label', 'select-name', 'image-alt', 'svg-img-alt',
  'role-img-alt', 'input-image-alt', 'area-alt', 'object-alt', 'frame-title', 'summary-name',
  'aria-command-name', 'aria-input-field-name', 'aria-toggle-field-name', 'aria-meter-name',
  'aria-progressbar-name', 'aria-tooltip-name', 'aria-dialog-name', 'document-title', 'html-has-lang',
];

module.exports = {
  name: 'names',
  title: 'Every control has a name (UX-09)',
  async run(screens) {
    const found = new Map();
    const w = await L.open(L.SMALL);
    // Given to the page directly: its security rules (rightly) refuse scripts added to it.
    const axe = require('fs').readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
    try {
      for (const url of screens) {
        if (!(await L.visit(w, url))) {
          continue;
        }
        for (const state of ['', ' [menus open]']) {
          if (state) {
            await w.page.evaluate(() => document.querySelectorAll('details').forEach((d) => { d.open = true; }));
            await w.page.waitForTimeout(150);
          }
          if (!(await w.page.evaluate(() => typeof window.axe !== 'undefined'))) {
            await w.page.evaluate(axe);
          }
          const v = await w.page.evaluate(async (rules) => {
            const r = await window.axe.run(document, { runOnly: { type: 'rule', values: rules }, resultTypes: ['violations'] });
            return r.violations.flatMap((x) => x.nodes.map((n) => [x.id, n.target.join(' '), (n.html || '').slice(0, 110)]));
          }, RULES);
          for (const [rule, target, html] of v) {
            const key = rule + '|' + html;
            if (!found.has(key)) {
              found.set(key, `${url}${state}: ${rule} :: ${target} :: ${html}`);
            }
          }
        }
      }
    } finally {
      await w.close();
    }
    return [...found.values()];
  },
};
