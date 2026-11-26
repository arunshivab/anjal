// rc.31 (owner, 25 Nov 2026). D6: "I selected page one to turn left ... 1 and 2 became blank" - after turns, every
// page of a six-page PDF must come back drawn (the viewer was answering "busy" as "not found"). D8: "when i scroll the
// options scroll away" - the editor's bar and the chosen tool's row stay together at the top while the pages scroll,
// for each of the four tools, down to the last page.
'use strict';
const L = require('../lib.js');

const WIDE = [1440, 900];

// The first PDF of six pages or more with an Edit link, read from the views of the mail's files: those the crawl
// found, then the sample's six-page theatre list found by Search (the crawl does not open every mail).
async function sixPages(w, screens) {
  const views = screens.filter((u) => /^\/message\/[^?]+\?preview=\d+$/.test(u));
  if (await L.visit(w, '/search?q=' + encodeURIComponent('Theatre list'), 800)) {
    const ids = await w.page.evaluate(() => [...new Set([...document.querySelectorAll('a[href*="open="]')].map((a) => (/[?&]open=([0-9a-f-]{36})/.exec(a.getAttribute('href')) || [])[1]).filter(Boolean))]);
    ids.forEach((id) => [0, 1, 2].forEach((n) => views.push(`/message/${id}?preview=${n}`)));
  }
  for (const url of views) {
    if (!(await L.visit(w, url, 1200))) { continue; }
    const r = await w.page.evaluate(() => {
      const a = [...document.querySelectorAll('a[href*="edit=1"]')].find((x) => x.getAttribute('href').startsWith('/message/'));
      const pages = document.querySelectorAll('.pdfview .pdfpage').length;
      return a ? { href: a.getAttribute('href'), pages } : null;
    });
    if (r && r.pages >= 6) { return r.href; }
  }
  return null;
}

// Every page brought into view in turn; the pages whose picture is not drawn.
async function blankPages(page) {
  return await page.evaluate(async () => {
    const pages = [...document.querySelectorAll('.editpage')];
    for (const p of pages) {
      p.scrollIntoView({ block: 'center' });
      await new Promise((r) => setTimeout(r, 400));
    }
    const until = Date.now() + 12000;
    let blank;
    do {
      blank = pages.map((p, i) => ({ i, img: p.querySelector('img') })).filter((x) => !x.img || !x.img.complete || x.img.naturalWidth === 0).map((x) => x.i + 1);
      if (!blank.length) { break; }
      await new Promise((r) => setTimeout(r, 500));
    } while (Date.now() < until);
    return blank;
  });
}

module.exports = {
  name: 'editor',
  title: 'The PDF editor: every page drawn again after turns (six pages), and the bar with its tool row kept at the top for all four tools',
  async run(screens) {
    const found = [];
    let checked = 0;
    const w = await L.open(WIDE);
    try {
      const editor = await sixPages(w, screens);
      if (!editor) { return ['no PDF of six pages with the editor was found to check']; }

      // D6: tick page 1 and turn it left and right as a person does, quickly and slowly; every page must be drawn
      // after each round. Each turn redraws the pages and asks for their text at once - more than the viewer runs for
      // one person (four) - and over HTTP/2 on the server all of them arrive together; a page asked for while the
      // viewer was busy came back blank. Here (HTTP/1.1, six at a time) the viewer is seldom that busy, so each
      // page's picture is first answered "busy" (503), as the server answers it: it must still come back drawn.
      await L.visit(w, editor + '&tool=pages', 1500);
      const once = new Set();
      const busy = /\/page\/\d+\?w=/;
      await w.page.route(busy, (route) => {
        const url = route.request().url().replace(/[?&]again=\d+/, '');
        if (once.has(url)) { return route.continue(); }
        once.add(url);
        return route.fulfill({ status: 503, headers: { 'Retry-After': '1' }, body: '' });
      });
      for (const [round, pause] of [[1, 150], [2, 150], [3, 1200]]) {
        await w.page.click('.editpage .editchoose input');
        for (const way of ['270', '90', '270', '90']) {
          await w.page.click(`[data-edit-turn="${way}"]`);
          await L.sleep(pause);
        }
        await w.page.click('.editpage .editchoose input');
        const blank = await blankPages(w.page);
        checked++;
        if (blank.length) { found.push(`${WIDE.join('x')} ${editor}: after turns (round ${round}), page(s) ${blank.join(', ')} of 6 came back blank`); }
      }
      await w.page.unroute(busy);

      // D8: for each tool, scroll to the last page; the bar and the tool's row must be at the top, together.
      const tools = await w.page.$$eval('[data-edit-tool]', (bs) => bs.map((b) => b.getAttribute('data-edit-tool')));
      for (const tool of tools) {
        if (!(await L.visit(w, editor + '&tool=' + tool, 1500))) { continue; }
        const r = await w.page.evaluate(async (t) => {
          const stick = document.querySelector('.editstick');
          const bar = stick && stick.querySelector('.editbar');
          const panel = document.querySelector(`[data-edit-panel="${t}"]`);
          const pages = document.querySelectorAll('.editpage');
          if (!stick || !bar || !panel || !pages.length) { return { missing: true }; }
          pages[pages.length - 1].scrollIntoView({ block: 'end' });
          await new Promise((res) => setTimeout(res, 500));
          let box = stick.parentElement;
          while (box && box !== document.body) {
            const cs = getComputedStyle(box);
            if (/(auto|scroll)/.test(cs.overflowY) && box.scrollHeight > box.clientHeight) { break; }
            box = box.parentElement;
          }
          const top = box && box !== document.body ? box.getBoundingClientRect().top : 0;
          const b = bar.getBoundingClientRect();
          const p = panel.getBoundingClientRect();
          return {
            inside: stick.contains(panel),
            barTop: Math.round(b.top - top),
            gap: Math.round(p.top - b.bottom),
            panelShown: p.height > 0 && p.bottom > top && p.top < window.innerHeight,
            scrolled: box ? Math.round(box.scrollTop || window.scrollY) : 0,
          };
        }, tool);
        checked++;
        if (r.missing) { found.push(`${WIDE.join('x')} ${editor}&tool=${tool}: the editor's bar, the tool's row or the pages were not found`); continue; }
        if (!r.inside || !r.panelShown || r.barTop < -2 || r.barTop > 24 || r.gap > 16) {
          found.push(`${WIDE.join('x')} ${editor}&tool=${tool}: at the last page the bar is ${r.barTop}px from the top and the tool's row ${r.panelShown ? r.gap + 'px below it' : 'is off screen'}${r.inside ? '' : ' (not kept with the bar)'} (scrolled ${r.scrolled}px)`);
        }
      }
    } finally {
      await w.close();
    }
    found.checked = checked;
    return found;
  },
};
