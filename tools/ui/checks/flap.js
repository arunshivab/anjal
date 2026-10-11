// DES-11 D9 (owner, 10 Oct 2026, B2): the marigold flap points from the open card into the
// letter. With three panes side by side it shows in the gap beside the open card, at the card's
// middle, never over the card or the letter; it follows the card as the list scrolls and hides
// when the card is out of view. On a phone, in Focus and with the list alone there is none.
// Owner, 10 Oct 2026: it sits beside the open card, before the list's scroll bar, never on it -
// checked with real scroll bars drawn, as on Windows: its stroke lies wholly between the card's
// edge and the inside edge of the list's scroll area.
'use strict';
const L = require('../lib.js');

const PHONE = [390, 844];

function measure() {
  const mark = document.querySelector('[data-flapmark]');
  const row = document.querySelector('.mrow.open');
  const list = document.querySelector('.listcol');
  const read = document.querySelector('.readcol');
  if (!row || !list || !read) { return null; }
  const m = mark && !mark.hidden ? mark.getBoundingClientRect() : null;
  const r = row.getBoundingClientRect();
  const l = list.getBoundingClientRect();
  const card = read.querySelector('.letter, .reader, article, .msg') || read;
  const ink = mark && !mark.hidden ? mark.querySelector('path').getBoundingClientRect() : null;
  return {
    shown: !!m,
    side: l.width > 0 && read.getBoundingClientRect().left >= l.right - 1,
    rowMiddle: r.top + r.height / 2,
    rowRight: r.right,
    rowVisible: r.top + r.height / 2 > l.top + 14 && r.top + r.height / 2 < l.bottom - 14,
    markMiddle: m ? m.top + m.height / 2 : null,
    markLeft: m ? m.left : null,
    markRight: m ? m.right : null,
    letterLeft: card.getBoundingClientRect().left,
    listInner: l.left + list.clientLeft + list.clientWidth,
    inkLeft: ink ? ink.left - 1.75 : null,
    inkRight: ink ? ink.right + 1.75 : null,
  };
}

function judge(at, where) {
  if (!at) { return null; }
  if (!at.side) { return at.shown ? `${where}: a flap shows though the panes are not side by side` : null; }
  if (at.rowVisible !== at.shown) { return `${where}: the flap is ${at.shown ? 'shown' : 'hidden'} while the open card is ${at.rowVisible ? 'in' : 'out of'} view`; }
  if (!at.shown) { return null; }
  if (Math.abs(at.markMiddle - at.rowMiddle) > 2) { return `${where}: the flap is ${Math.round(at.markMiddle - at.rowMiddle)}px off the open card's middle`; }
  if (at.inkLeft < at.rowRight - 0.5) { return `${where}: the flap is drawn over the open card by ${Math.round(at.rowRight - at.inkLeft)}px`; }
  if (at.inkRight > at.listInner + 0.5) { return `${where}: the flap is drawn over the list's scroll bar by ${Math.round(at.inkRight - at.listInner)}px`; }
  return null;
}

module.exports = {
  name: 'flap',
  title: 'The marigold flap points from the open card into the letter, and follows it',
  async run(screens) {
    const found = [];
    let checked = 0;
    const folders = screens.filter((u) => /^\/folder\/[^?]+$/.test(u)).slice(0, 3);
    for (const size of [L.SMALL, L.MID, L.LARGE, PHONE]) {
      const w = await L.open(size);
      try {
        for (const folder of folders) {
          if (!(await L.visit(w, folder, 300))) { continue; }
          const links = await w.page.$$eval('[data-list] .mrow a[href*="open="]', (a) => a.map((x) => x.getAttribute('href')));
          if (links.length === 0) { continue; }
          const pick = links[Math.min(links.length - 1, 3)];
          if (!(await L.visit(w, pick, 400))) { continue; }
          const where = `${size.join('x')} ${pick}`;
          checked++;
          const problem = judge(await w.page.evaluate(measure), where);
          if (problem) { found.push(problem); continue; }
          // It follows the card as the list scrolls, and goes when the card is out of view.
          for (const by of [60, 2000]) {
            await w.page.evaluate((y) => { const l = document.querySelector('.listcol'); if (l) { l.scrollTop += y; } }, by);
            await w.page.waitForTimeout(150);
            const after = judge(await w.page.evaluate(measure), `${where}, list scrolled ${by}px`);
            if (after) { found.push(after); break; }
          }
        }
      } finally {
        await w.close();
      }
    }
    if (checked === 0) { return ['no open message was found to check']; }
    return found;
  },
};
