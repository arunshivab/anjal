// DEF-106 (rc.19; owner 13 Oct 2026, "do a system wide check if some other component behaves the
// same way"): text that changes while a screen is open - the connection light, "Updated 12:51", the
// draft note, the storage figure, unread badges, "N selected", "N new", the sending countdown - must
// never move what sits beside it. Each such text is set, in place, to the shortest and the longest
// thing it can say; nothing around it may move or change size by more than a pixel.
'use strict';
const L = require('../lib.js');

// What changes live, and what it can say. Each entry: where it is, and the texts to try (built from
// the words the page itself carries, so it works in every language).
function probe() {
  const W = (el, a, d) => (el && el.getAttribute(a)) || d;
  const bar = document.querySelector('[data-statusbar]');
  const times = ['00:00', '23:59', '1:00 am', '12:59 pm'];
  const slots = [
    { name: 'connection light', sel: '.statusbar [data-connectivity]',
      texts: (e) => [W(e, 'data-online', 'Connected'), W(e, 'data-offline', 'Offline')] },
    { name: 'updated time', sel: '.statusbar [data-updated]',
      texts: () => times.map((t) => W(bar, 'data-word-updated', 'Updated {time}').replace('{time}', t))
        .concat(times.map((t) => W(bar, 'data-word-offline', 'Offline: showing mail as of {time}').replace('{time}', t))) },
    { name: 'draft note', sel: '.statusbar [data-st-draft]', show: true,
      texts: () => times.map((t) => W(bar, 'data-word-draft', 'Saved as draft \u00b7 {time}').replace('{time}', t)) },
    { name: 'storage figure', sel: '.statusbar .st-store',
      texts: (e) => ['0 bytes', '1.11 MB', '999.99 MB', '1.00 GB', '1023.99 GB'].map((n) => e.textContent.trim().replace(/^[\d.,]+\s*\S+/, n)) },
    { name: 'unread badge', sel: '.rail .rail-item .n, .pfolder .n',
      texts: () => ['1', '88', '9999'] },
    { name: 'selected count', sel: '[data-bulklabel]', open: (e) => e.closest('[data-selbar]'),
      texts: (e) => [e.textContent.trim()].concat(['1', '88', '9999'].map((n) => W(e, 'data-word-selected', '{n} selected').replace('{n}', n))) },
    { name: 'new pill', sel: '[data-newpill]',
      texts: () => ['1', '88', '9999'].map((n) => W(document.querySelector('[data-newpill]'), 'data-word', '{n} new').replace('{n}', n)) },
    { name: 'sending countdown', sel: '[data-heldtext]',
      texts: () => ['1', '9', '10', '30'].map((n) => W(document.querySelector('[data-heldbar]'), 'data-word-sending', 'Sending in {n} seconds').replace('{n}', n)) },
  ];
  const seen = (e) => { const q = e.getBoundingClientRect(); return q.width > 0 && q.height > 0 && getComputedStyle(e).visibility !== 'hidden'; };
  // The text node the script writes into: the last one with words in it (a light or a meter beside
  // the words stays as it is).
  const textNode = (e) => { const w = document.createTreeWalker(e, NodeFilter.SHOW_TEXT); let last = null; for (let n = w.nextNode(); n; n = w.nextNode()) { if (n.nodeValue.trim()) { last = n; } } return last; };
  const label = (e) => (e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '') + (e.textContent.trim() ? ' "' + e.textContent.trim().slice(0, 24) + '"' : ''));
  const found = [];
  let checked = 0;
  for (const s of slots) {
    for (const el of document.querySelectorAll(s.sel)) {
      const opener = s.open ? s.open(el) : null;
      const wasOn = opener ? opener.classList.contains('on') : true;
      if (opener) { opener.classList.add('on'); }
      const wasHidden = el.hidden;
      if (wasHidden && !s.show) { continue; }
      if (wasHidden) { el.hidden = false; }
      if (!seen(el)) { el.hidden = wasHidden; if (!wasOn) { opener.classList.remove('on'); } continue; }
      const node = textNode(el);
      const before = node ? node.nodeValue : null;
      // Everything near it: the row it sits in, two levels up, and the frame (rail, status bar).
      const row = el.parentElement && el.parentElement.parentElement ? el.parentElement.parentElement : el.parentElement;
      const near = new Set([...row.querySelectorAll('*'), ...document.querySelectorAll('.statusbar *, nav.rail a, nav.rail svg')]);
      const others = [...near].filter((o) => o !== el && !el.contains(o) && !o.contains(el) && seen(o)).slice(0, 400);
      const box = (o) => { const q = o.getBoundingClientRect(); return [q.left, q.top, q.width, q.height]; };
      const texts = s.texts(el);
      let ref = null;
      for (const t of texts) {
        if (node) { node.nodeValue = t; } else { el.textContent = t; }
        const now = others.map(box);
        checked += others.length;
        if (!ref) { ref = { t, now }; continue; }
        for (let i = 0; i < others.length; i++) {
          const a = ref.now[i], b = now[i];
          const d = [b[0] - a[0], b[1] - a[1], b[2] - a[2], b[3] - a[3]];
          if (d.some((x) => Math.abs(x) > 1)) {
            const what = Math.abs(d[0]) > 1 || Math.abs(d[1]) > 1 ? `moves ${Math.round(d[0])},${Math.round(d[1])}px` : `changes size ${Math.round(d[2])}x${Math.round(d[3])}px`;
            found.push(`${s.name} (${label(el)}): from "${ref.t}" to "${t}" ${label(others[i])} ${what}`);
            break;
          }
        }
      }
      if (node) { node.nodeValue = before; }
      el.hidden = wasHidden;
      if (!wasOn) { opener.classList.remove('on'); }
    }
  }
  return { found, checked };
}

module.exports = {
  name: 'live',
  title: 'Text that changes on an open screen never moves what is beside it',
  async run(screens) {
    const found = [];
    let checked = 0;
    for (const size of [L.SMALL, L.MID, L.LARGE]) {
      const w = await L.open(size);
      try {
        for (const url of screens) {
          if (!(await L.visit(w, url, 150))) { continue; }
          const r = await w.page.evaluate(probe);
          checked += r.checked;
          r.found.forEach((f) => found.push(`${size.join('x')} ${url}: ${f}`));
        }
      } finally {
        await w.close();
      }
    }
    // One report per kind of text and neighbour is enough to find it.
    const seen = new Set();
    const list = found.filter((x) => { const k = x.replace(/^\S+ \S+: /, '').replace(/"[^"]*"/g, '').replace(/-?\d+/g, '#'); if (seen.has(k)) { return false; } seen.add(k); return true; });
    list.checked = checked;
    return list;
  },
};
