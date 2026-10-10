// Owner, 9 Oct 2026 ("C"): no full page reloads. A link or a form fetches the next page and
// swaps it in place: no page load happens, the address and the tab's title change, Back and
// Forward work, focus goes to the new page's heading, nothing raises a script error, and the
// page shown is the same as the one a full load gives.
'use strict';
const L = require('../lib.js');

function mainText() {
  const m = document.querySelector('#main');
  // Figures that change by themselves (times, the disk, counts) are left out of the comparison.
  return m ? m.innerText.replace(/\d+([.,:]\d+)*/g, '#').replace(/\s+/g, ' ').trim().slice(0, 4000) : '';
}

module.exports = {
  name: 'swap',
  title: 'Links and forms swap the page in place, with no full reload',
  async run(screens) {
    const found = [];
    const w = await L.open(L.SMALL);
    const p = w.page;
    try {
      let loads = 0;
      p.on('load', () => { loads++; });
      // Every request a swap makes is answered, never refused (a button's own formaction, 10 Oct).
      p.on('response', (r) => {
        if (r.status() >= 400 && r.url().startsWith(L.base) && !/\/api\//.test(r.url())) { found.push(`${r.request().method()} ${r.url().replace(L.base, '')} answered ${r.status()}`); }
      });
      if (!(await L.visit(w, '/folder/INBOX', 600))) { return ['/folder/INBOX did not open']; }
      loads = 0;
      await p.evaluate(() => { window.__swapMark = 1; });
      const kept = async (where) => {
        const still = await p.evaluate(() => window.__swapMark === 1);
        if (!still || loads > 0) { found.push(`${where}: the page was loaded again instead of swapped`); loads = 0; await p.evaluate(() => { window.__swapMark = 1; }); return false; }
        return true;
      };
      const settle = () => p.waitForFunction(() => !document.documentElement.classList.contains('swapping'), null, { timeout: 15000 }).then(() => p.waitForTimeout(200));

      // 1. Every link in the rail swaps, and the page is the one a full load shows.
      const rail = await p.$$eval('nav.rail a[href^="/"]', (a) => [...new Set(a.map((x) => x.getAttribute('href')))].filter((h) => !h.startsWith('/compose')));
      const visited = [];
      for (const href of rail) {
        const before = p.url();
        await p.click(`nav.rail a[href="${href}"]`);
        await settle();
        if (!(await kept(href))) { continue; }
        if (p.url() === before && !before.endsWith(href)) { found.push(`${href}: the address did not change`); }
        const focused = await p.evaluate(() => { const a = document.activeElement; return a && (a.tagName === 'H1' || a.id === 'main'); });
        if (!focused) { found.push(`${href}: focus did not go to the new page's heading`); }
        // The pointer rests in a corner, so no row shows its own buttons in place of its time.
        await p.mouse.move(1, 1);
        visited.push([href, p.url(), await p.title(), await p.evaluate(mainText)]);
      }
      // 2. Back and Forward walk the same pages.
      for (let i = visited.length - 2; i >= Math.max(0, visited.length - 4); i--) {
        await p.goBack();
        await settle();
        await kept('Back');
        if (p.url() !== visited[i][1]) { found.push(`Back: expected ${visited[i][1]}, got ${p.url()}`); break; }
      }
      await p.goForward();
      await settle();
      await kept('Forward');
      // 3. A search (a GET form) and a choice that applies at once (a POST form).
      await p.click('nav.rail a[href="/folder/INBOX"]');
      await settle();
      const q = await p.$('input[name=q]');
      if (q) {
        await q.fill('a');
        await q.press('Enter');
        await settle();
        if (await kept('search') && !/\/search\?/.test(p.url())) { found.push(`search: ended at ${p.url()}`); }
      }
      await p.click('nav.rail a[href="/settings"]');
      await settle();
      const mailTab = await p.$('a[href="/settings/mail"]');
      if (mailTab) {
        await mailTab.click();
        await settle();
        const toggle = await p.$('form[action="/settings/sound"] label.mtoggle');
        if (toggle) {
          for (let n = 0; n < 2; n++) {
            await toggle.click().catch(() => {});
            await settle();
            await kept('a choice that applies at once');
          }
        }
      }
      // 3b. A button with its own address (a row's Flag, formaction) in a filtered list.
      await L.visit(w, '/folder/INBOX?show=unread', 500);
      loads = 0;
      await p.evaluate(() => { window.__swapMark = 1; });
      const fav = await p.$('[data-list] .mrow button.fav');
      if (fav) {
        await fav.click({ force: true });
        await settle();
        await kept("a row's Flag");
        const again = await p.$('[data-list] .mrow button.fav');
        if (again) { await again.click({ force: true }); await settle(); }
      }
      // 4. Each swapped page matches a full load of the same address.
      for (const [href, url, title, text] of visited.slice(0, 12)) {
        await L.visit(w, url.replace(L.base, ''), 500);
        await p.mouse.move(1, 1);
        const full = await p.evaluate(mainText);
        const fullTitle = await p.title();
        if (fullTitle.replace(/^\(\d+\) /, '') !== title.replace(/^\(\d+\) /, '')) { found.push(`${href}: title "${title}" swapped, "${fullTitle}" loaded`); }
        if (full !== text) {
          let at = 0;
          while (at < full.length && full[at] === text[at]) { at++; }
          found.push(`${href}: the swapped page differs from a full load: "${text.slice(Math.max(0, at - 30), at + 50)}" swapped, "${full.slice(Math.max(0, at - 30), at + 50)}" loaded`);
        }
      }
      if (p.scriptErrors.length) { found.push('script errors: ' + [...new Set(p.scriptErrors)].slice(0, 5).join(' | ')); }
    } finally {
      await w.close();
    }
    return found;
  },
};
