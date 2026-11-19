// Owner, 16 Nov 2026: a file opened from Today did not show the viewer, and Search had the same gap,
// while every folder showed it. Every screen that opens a file ("preview=" in its address) must show
// the viewer with its label; and every file found that way must also open from Today and from Search.
'use strict';
const L = require('../lib.js');

const ID = /[?&]open=([0-9a-f-]{36})/;
const AT = /[?&]preview=(\d+)/;

module.exports = {
  name: 'viewer',
  title: 'A file opens in the viewer from every screen that shows a mail (folders, Today, Search)',
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
      }
    } finally {
      await w.close();
    }
    if (checked === 0) { return ['no screen that opens a file was found to check']; }
    found.checked = checked;
    return found;
  },
};
