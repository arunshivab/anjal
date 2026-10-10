// Owner, 8 Oct 2026 ("similar components should look exactly the same"): each kind of control and
// text looks the same on every screen - text boxes, choice boxes, buttons of each size, headings,
// labels, notes, tables and pills. Each kind may have only one look; every other look is listed with
// where it was seen. Measured at the owner's own screen size (1536 x 730).
'use strict';
const L = require('../lib.js');

// Each kind, and what it is: the same words size and weight, height and corners wherever it is.
const KINDS = {
  'text box': ':is(.inp, .railinp, .settings3 input:not([type]), .settings3 input[type=text], .settings3 input[type=email], .settings3 input[type=password], .settings3 input[type=search], .settings3 input[type=number]):not(textarea):not(.codebox):not([contenteditable])',
  'choice box': 'select:not([multiple]):not(.rte-size):not(.bulkcat .scope)',
  // Owner, 10 Oct 2026 (sizes): a choice box in a toolbar is small, like the buttons beside it.
  'small choice box': 'select.rte-size, .bulkcat select.scope',
  // A form's own action is full size wherever it sits (Settings marks some as small; they are drawn full size).
  // And beside a text box or a choice box, a small button is drawn full size (owner, 10 Oct 2026: one row, one size).
  'button': '.btn:not(.btn-sm), :is(.setactions, .twobtns, .orghead, .orgheadbtns) .btn.btn-sm, :has(> :is(input.inp, select, .sbox, .rinput)) > .btn.btn-sm',
  'small button': '.btn.btn-sm:not(:is(.setactions, .twobtns, .orghead, .orgheadbtns) .btn):not(:has(> :is(input.inp, select, .sbox, .rinput)) > .btn)',
  'page heading': '.settings3 .formcard > h2, .settings3 .formcard .orghead h2',
  'section heading': '.settings3 .formcard :is(h3.sethead, .rhead h3, h3)',
  'label': '.settings3 .setlbl',
  'lead text': '.settings3 .formcard > p.hint:first-of-type, .settings3 p.orglead',
  'note': '.settings3 .setaside p.hint, .settings3 .formcard p.hint:not(:first-of-type):not(.orglead)',
  'column head': 'thead th',
  'table cell': 'tbody td',
  'row head': 'tbody th',
  'pill': '.pill',
  'aside heading': '.setaside .asidehead, .setaside > h2',
};
const SIZED = new Set(['text box', 'choice box', 'small choice box', 'button', 'small button', 'pill']);

module.exports = {
  name: 'same',
  title: 'Each kind of control and text looks the same everywhere',
  async run(screens) {
    const seen = {};
    const w = await L.open(L.MID);
    try {
      for (const url of screens) {
        if (/\/print$/.test(url.split('?')[0]) || !(await L.visit(w, url, 300))) { continue; }
        const r = await w.page.evaluate(([K, S]) => {
          const out = {};
          for (const [kind, sel] of Object.entries(K)) {
            for (const e of document.querySelectorAll(sel)) {
              // Inside a menu, a button may take two lines (rc.15, 7 Oct); a menu's own look is its own.
              // Compose's send bar has its own, larger buttons; a printed page has its own sizes.
              if (e.closest('.pmenu-body, .rte-menu, .modal-card, .csend, .cdock-foot, .printpage')) { continue; }
              // Something inside a closed menu still has a size, but cannot be seen.
              let shut = false;
              for (let d = e.closest('details'); d; d = d.parentElement && d.parentElement.closest('details')) { const sm = d.querySelector(':scope > summary'); if (!d.open && !(sm && sm.contains(e))) { shut = true; } }
              if (shut) { continue; }
              const q = e.getBoundingClientRect();
              // Text for screen readers only (.sr-only) has no look to compare.
              if (!q.width || !q.height || e.closest('.sr-only')) { continue; }
              const cs = getComputedStyle(e);
              const look = [cs.fontSize, 'weight ' + cs.fontWeight].concat(S.includes(kind) ? ['height ' + Math.round(q.height) + 'px', 'corners ' + cs.borderTopLeftRadius] : []).join(', ');
              (out[kind] = out[kind] || []).push(look);
            }
          }
          return out;
        }, [KINDS, [...SIZED]]);
        for (const [kind, looks] of Object.entries(r)) {
          for (const look of looks) {
            const k = kind + '|' + look;
            if (!seen[k]) { seen[k] = { kind, look, n: 0, where: url }; }
            seen[k].n++;
          }
        }
      }
    } finally {
      await w.close();
    }
    const found = [];
    for (const kind of Object.keys(KINDS)) {
      const looks = Object.values(seen).filter((x) => x.kind === kind).sort((a, b) => b.n - a.n);
      for (const x of looks.slice(1)) {
        found.push(`${kind}: ${x.n} look "${x.look}" instead of "${looks[0].look}" (${looks[0].n}), e.g. ${x.where}`);
      }
    }
    found.checked = Object.values(seen).reduce((a, x) => a + x.n, 0);
    return found;
  },
};
