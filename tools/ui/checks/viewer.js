// Owner, 16 Nov 2026: a file opened from Today did not show the viewer, and Search had the same gap,
// while every folder showed it. Every screen that opens a file ("preview=" in its address) must show
// the viewer with its label; and every file found that way must also open from Today and from Search.
'use strict';
const L = require('../lib.js');

const ID = /[?&]open=([0-9a-f-]{36})/;
const AT = /[?&]preview=(\d+)/;

module.exports = {
  name: 'viewer',
  title: 'A file opens in the viewer from every screen that shows a mail (folders, Today, Search), and a DICOM series scrolls',
  async run(screens) {
    const targets = new Set(screens.filter((u) => AT.test(u)));
    for (const url of [...targets]) {
      const id = ID.exec(url) || /^\/message\/([0-9a-f-]{36})/.exec(url);
      if (!id) { continue; }
      const at = AT.exec(url)[1];
      targets.add(`/dashboard/today?open=${id[1]}&preview=${at}`);
      targets.add(`/search?q=the&open=${id[1]}&preview=${at}`);
    }
    const found = [];
    let checked = 0;
    const w = await L.open(L.MID);
    try {
      for (const url of targets) {
        if (!(await L.visit(w, url, 400))) { continue; }
        checked++;
        const shown = await w.page.evaluate(() => {
          const tab = document.querySelector('.pvtab');
          return tab && tab.getBoundingClientRect().height > 0 ? tab.textContent.trim().replace(/\s+/g, ' ') : null;
        });
        if (!shown) { found.push(`${L.MID.join('x')} ${url}: the file was asked for, but no viewer is shown`); }
        // rc.30 (DES-22 §21.4): a DICOM series scrolls - End and ← move the slice wherever the focus is, the words
        // follow, the picture is asked for and drawn, and the page stays on the series (← is not the next file).
        if (/[?&]series=\d+/.test(url) && (await w.page.$('[data-dicom-series]'))) {
          const total = await w.page.$eval('[data-dicom-series]', (b) => JSON.parse(b.getAttribute('data-entries')).length);
          await w.page.keyboard.press('End');
          await w.page.keyboard.press('ArrowLeft');
          await L.sleep(900);
          const after = await w.page.evaluate(() => {
            const img = document.querySelector('[data-dicom-img]');
            return { at: document.querySelector('[data-dicom-at]').textContent.trim(), drawn: !!img && img.complete && img.naturalWidth > 0, href: location.href };
          });
          const want = total > 1 ? total - 1 : 1;
          if (!after.at.includes(String(want)) || !after.drawn || !/[?&]series=\d+/.test(after.href) || !after.href.includes('&slice=' + want)) {
            found.push(`${L.MID.join('x')} ${url}: the series did not move to slice ${want} of ${total} (${after.at}; drawn ${after.drawn}; ${after.href})`);
          }
        }
      }
    } finally {
      await w.close();
    }
    if (checked === 0) { return ['no screen that opens a file was found to check']; }
    found.checked = checked;
    return found;
  },
};
