// Owner, 10 Oct 2026 ("Send and Send later are of different sizes ... there needs to be standard
// button and text box sizes ... those together need to be the same"): three heights in the whole
// app - regular 36px for buttons, text boxes and choice boxes; small 28px for actions inside a row,
// a toolbar or a strip of choices; large 48px only on the sign-in pages and for the boxes a code
// is typed into - and controls side by side on one row are one height. A strip of choices counts
// as one control (its box). The phone keeps its own touch sizes, so it is not measured here.
'use strict';
const L = require('../lib.js');

const SIZES = [28, 36, 48];

function measure() {
  const sel = 'button, a.btn, .btn, input[type=submit], input[type=button], input[type=text], input[type=email], input[type=password], input[type=search], input[type=number], input[type=date], input[type=time], input[type=url], input[type=tel], input:not([type]), select, summary.btn, .eb, .rowact, .iconbtn, .pbtn, .seg';
  // Not controls of the kind measured: words that act as links, column headings that sort, a
  // sender's letter, labels, the folder rail, a box's own clear button, the parts of a strip of
  // choices (the strip is measured), colour and background tiles, the eye inside a password box,
  // and anything inside a menu or a chip.
  const skip = '.linkbtn, .sorth, .fav, .chip, .chip-x, .rail-item, .rail-compose, .seg a, .seg button, .seg .iconbtn, .pmi, .pwreveal, [role=menu] *, .rte-tile, .rte-sw, .rte-em, .rte-tilebox, .toggle, .cfile, textarea';
  const els = [...new Set(document.querySelectorAll(sel))].filter((e) => {
    const q = e.getBoundingClientRect();
    const cs = getComputedStyle(e);
    return q.width > 0 && q.height > 0 && cs.visibility !== 'hidden' && cs.opacity !== '0' && !e.matches(skip) && !e.closest('.sr-only, template, .tipfloat, .phoneonly');
  });
  const name = (e) => (e.getAttribute('aria-label') || e.textContent || e.value || e.placeholder || '').replace(/\s+/g, ' ').trim().slice(0, 28);
  const cls = (e) => (e.className.toString() || e.tagName.toLowerCase()).split(/\s+/).filter(Boolean).slice(0, 3).join('.');
  // Controls in different panes or cards are never one row, however close they sit.
  const region = (e) => e.closest('.listcol, .readcol, .rail, .ecard, .letter, .composer, aside, dialog, details') || document.body;
  const list = els.map((e) => { const q = e.getBoundingClientRect(); return { h: Math.round(q.height), n: name(e), c: cls(e), mid: q.top + q.height / 2, left: q.left, right: q.right, r: region(e) }; });
  const odd = list.filter((i) => !SIZES_.includes(i.h)).map((i) => `"${i.n}" [${i.c}] is ${i.h}px`);
  const rows = [];
  for (let a = 0; a < list.length; a++) {
    for (let b = a + 1; b < list.length; b++) {
      const x = list[a];
      const y = list[b];
      if (x.r !== y.r || Math.abs(x.mid - y.mid) > 6) { continue; }
      const gap = Math.max(x.left, y.left) - Math.min(x.right, y.right);
      if (gap < 0 || gap > 24) { continue; }
      if (Math.abs(x.h - y.h) >= 2) { rows.push(`"${x.n}" [${x.c}] ${x.h}px beside "${y.n}" [${y.c}] ${y.h}px`); }
    }
  }
  return { n: list.length, odd, rows };
}

module.exports = {
  name: 'sizes',
  title: 'Three heights in the whole app (36, 28, 48), and one height on a row',
  async run(screens) {
    const found = new Map();
    let controls = 0;
    for (const size of [L.SMALL, L.LARGE]) {
      const w = await L.open(size);
      try {
        for (const url of screens) {
          if (!(await L.visit(w, url, 300))) { continue; }
          await w.page.mouse.move(1, 1);
          const r = await w.page.evaluate(`(${measure.toString().replace('SIZES_', JSON.stringify(SIZES))})()`);
          controls += r.n;
          const at = L.shape(url);
          for (const p of r.odd.concat(r.rows)) {
            const key = at + ' ' + p.replace(/"[^"]*"/, '');
            if (!found.has(key)) { found.set(key, `${size.join('x')} ${url}: ${p}`); }
          }
        }
      } finally {
        await w.close();
      }
    }
    if (controls === 0) { return ['no control was found to measure']; }
    return [...found.values()];
  },
};
