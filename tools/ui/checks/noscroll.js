// Item 47 (owner, 7 Oct 2026): mail screens never scroll as a whole page on the smallest laptop
// (1366x768), nor on the owner's own (1536x730); only their lists and the message itself scroll
// inside their own areas. Dashboards, the consoles, Contacts and Settings may scroll their middle
// area (accepted), so are not checked.
'use strict';
const L = require('../lib.js');

const MAIL = /^\/($|\?|folder\/|search|compose|outbox|no-reply|draft\/|message\/)/;

function measure() {
  const d = document.scrollingElement || document.documentElement;
  return { down: d.scrollHeight - innerHeight, across: d.scrollWidth - innerWidth };
}

module.exports = {
  name: 'noscroll',
  title: 'Mail screens: no page scrolling at 1366x768 and 1536x730 (item 47)',
  async run(screens) {
    const found = [];
    for (const size of [L.SMALL, L.MID]) {
      const w = await L.open(size);
      try {
        // A message laid out for printing is a document, read by scrolling: it is not a mail screen.
        for (const url of screens.filter((u) => MAIL.test(u) && !/\/print(\?|$)/.test(u))) {
          if (!(await L.visit(w, url, 500))) {
            continue;
          }
          const states = [['', null]];
          if (await w.page.$('.mlist input[type=checkbox]')) {
            states.push([' (a mail ticked)', async () => w.page.click('.mlist input[type=checkbox]', { force: true })]);
          }
          for (const [label, act] of states) {
            if (act) {
              await act().catch(() => {});
              await w.page.waitForTimeout(250);
            }
            const m = await w.page.evaluate(measure);
            if (m.down > 1) {
              found.push(`${size.join('x')} ${url}${label}: the page scrolls ${m.down}px down`);
            }
            if (m.across > 1) {
              found.push(`${size.join('x')} ${url}${label}: the page scrolls ${m.across}px sideways`);
            }
          }
        }
      } finally {
        await w.close();
      }
    }
    return found;
  },
};
