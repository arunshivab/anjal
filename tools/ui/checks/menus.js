// Owner, 7 Oct 2026 ("the menu going behind is repeating... scan everywhere"): every menu opens
// fully on screen and on top of everything around it - on every screen, at both screen sizes,
// with and without a mail ticked (the selection bar has menus of its own).
'use strict';
const L = require('../lib.js');

async function probe(page, i) {
  return page.evaluate(async (i) => {
    const wait = (ms) => new Promise((r) => setTimeout(r, ms));
    const d = document.querySelectorAll('details')[i];
    const s = d && d.querySelector(':scope > summary');
    if (!s) { return null; }
    const sr = s.getBoundingClientRect();
    if (!sr.width || !sr.height || getComputedStyle(s).visibility === 'hidden') { return null; }
    s.scrollIntoView({ block: 'center' });
    await wait(60);
    if (!d.open) { s.click(); }
    await wait(150);
    const body = [...d.children].find((c) => c.tagName !== 'SUMMARY' && ['absolute', 'fixed'].includes(getComputedStyle(c).position));
    if (!body) { if (d.open) { s.click(); } return null; }
    const r = body.getBoundingClientRect();
    const issues = [];
    if (r.left < -0.5 || r.top < -0.5 || r.right > innerWidth + 0.5 || r.bottom > innerHeight + 0.5) {
      issues.push('partly off screen (' + [r.left, r.top, r.right, r.bottom].map(Math.round).join(', ') + ')');
    }
    const pts = [[r.left + 8, r.top + 8], [r.right - 8, r.top + 8], [r.left + r.width / 2, r.top + r.height / 2], [r.left + 8, r.bottom - 8], [r.right - 8, r.bottom - 8]];
    for (const [x, y] of pts) {
      if (x < 0 || y < 0 || x > innerWidth || y > innerHeight) { continue; }
      const t = document.elementFromPoint(x, y);
      if (!t || !d.contains(t)) {
        issues.push('covered at ' + Math.round(x) + ',' + Math.round(y) + ' by ' + (t ? t.tagName.toLowerCase() + '.' + String(t.className).split(' ')[0] : 'nothing'));
        break;
      }
    }
    s.click();
    return { label: (d.className || 'details').split(' ')[0] + ' "' + s.textContent.trim().replace(/\s+/g, ' ').slice(0, 30) + '"', issues };
  }, i).catch((e) => ({ label: 'menu ' + i, issues: [String(e).slice(0, 100)] }));
}

module.exports = {
  name: 'menus',
  title: 'Every menu opens fully on screen and on top',
  async run(screens) {
    const found = [];
    let checked = 0;
    for (const size of [L.SMALL, L.MID, L.LARGE]) {
      const w = await L.open(size);
      try {
        for (const url of screens) {
          for (const tick of [false, true]) {
            if (!(await L.visit(w, url))) { break; }
            if (tick) {
              const cb = await w.page.$('.mlist input[type=checkbox]');
              if (!cb) { break; }
              await cb.check({ force: true }).catch(() => {});
              await w.page.waitForTimeout(200);
            }
            const n = await w.page.$$eval('details', (ds) => ds.length);
            for (let i = 0; i < n && !w.page.isClosed(); i++) {
              const res = await probe(w.page, i);
              if (!res) { continue; }
              checked++;
              if (res.issues.length) {
                found.push(`${size.join('x')} ${url}${tick ? ' [a mail ticked]' : ''}: ${res.label} ${res.issues.join('; ')}`);
              }
            }
          }
        }
      } finally {
        await w.close();
      }
    }
    found.checked = checked;
    return found;
  },
};
