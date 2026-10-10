// Owner, 7 Oct 2026 ("From a template goes to a page not working... same with Small window"):
// every link opens its page and every button does its job - no error page, no refused request,
// no script error. Each distinct button is pressed once, after showing it as a person would
// (ticking a mail, opening its menu or window). It presses Delete too, so this check runs last
// and only on a throwaway copy.
'use strict';
const L = require('../lib.js');

const CTL = 'button, input[type=submit], [role=menuitem]';
const pat = (u) => L.shape(u).split('?')[0];

async function reveal(page, i) {
  return page.evaluate(async ([CTL, i]) => {
    const b = document.querySelectorAll(CTL)[i];
    const wait = (ms) => new Promise((r) => setTimeout(r, ms));
    // Inside a closed menu a button still has a size, but cannot be seen.
    const closed = () => { for (let d = b.closest('details'); d; d = d.parentElement && d.parentElement.closest('details')) { const s = d.querySelector(':scope>summary'); if (!d.open && !(s && s.contains(b))) { return true; } } return false; };
    const vis = () => { const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(b).visibility !== 'hidden' && !closed(); };
    if (vis()) { return 'shown'; }
    if (b.closest('[data-selbar]')) { const cb = document.querySelector('.mlist input[type=checkbox]'); if (cb) { cb.click(); await wait(200); } if (vis()) { return 'ticked a mail'; } }
    const row = b.closest('.mrow'); if (row) { row.classList.add('hover'); }
    const dl = b.closest('dialog'); if (dl && !dl.open) { try { dl.showModal(); } catch (e) { dl.show(); } await wait(150); if (vis()) { return 'window opened'; } }
    const chain = []; let d = b.closest('details'); while (d) { chain.unshift(d); d = d.parentElement.closest('details'); }
    for (const dd of chain) { if (!dd.open) { const s = dd.querySelector(':scope>summary'); s.scrollIntoView({ block: 'center' }); await wait(80); s.click(); await wait(200); } }
    if (chain.length) { b.scrollIntoView({ block: 'nearest' }); await wait(80); if (vis()) { return 'menu opened'; } }
    let e = b; while (e && e !== document.body) { if (e.hidden) { e.hidden = false; } e = e.parentElement; }
    await wait(100);
    return vis() ? 'unhidden' : 'hidden';
  }, [CTL, i]);
}

module.exports = {
  name: 'reach',
  title: 'Every link opens and every button works',
  async run(screens) {
    const found = [];
    let w = await L.open(L.SMALL);
    const done = new Set();
    let clicks = 0;
    try {
      // 1. Every link.
      const links = new Set();
      for (const u of screens) {
        if (!(await L.visit(w, u, 100))) { continue; }
        (await w.page.$$eval('a[href]', (as) => as.map((a) => a.getAttribute('href')))).forEach((h) => { if (h && h.startsWith('/') && !/sign-out|\/auth\//i.test(h)) { links.add(h.split('#')[0]); } });
      }
      for (const h of links) {
        const r = await w.page.request.get(L.base + h, { maxRedirects: 5 }).catch(() => null);
        if (!r || r.status() >= 400) { found.push(`link ${h}: ${r ? 'answers ' + r.status() : 'does not answer'}`); }
      }
      // 2. Every button, once per kind of screen: what a screen holds is read once, and the page
      //    is opened again only for a button not pressed before.
      for (const u of screens) {
        if (!(await L.visit(w, u, 300))) { continue; }
        const all = await w.page.evaluate((CTL) => [...document.querySelectorAll(CTL)].map((b, i) => {
          const f = b.form || b.closest('form');
          const txt = (b.getAttribute('aria-label') || b.title || b.getAttribute('data-tip') || b.textContent || b.value || '').trim().replace(/\s+/g, ' ').slice(0, 40);
          const sig = (f ? (f.getAttribute('action') || '') + '|' : 'js|') + (b.getAttribute('formaction') || '') + '|' + (b.name || '') + '=' + (b.value || '') + '|' + txt + '|' + (b.className || '');
          return { i, txt, sig, file: b.type === 'file' };
        }), CTL);
        const todo = [];
        for (const info of all) {
          const key = pat(u) + '::' + info.sig.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/g, '{id}');
          if (info.file || done.has(key) || /sign out/i.test(info.txt)) { continue; }
          done.add(key);
          todo.push(info);
        }
        let fresh = true;
        for (const info of todo) {
          if (!fresh && !(await L.visit(w, u, 300))) { break; }
          // An earlier press may have deleted what this screen showed (a message): its buttons are
          // then not this screen's, and the screen itself is checked with the links.
          if (w.status >= 400) { break; }
          fresh = false;
          if (!w.page.url().includes(u.split('?')[0])) { break; }
          const i = info.i;
          const same = await w.page.evaluate(([CTL, i, sig]) => {
            const b = document.querySelectorAll(CTL)[i]; if (!b) { return false; }
            const f = b.form || b.closest('form');
            const txt = (b.getAttribute('aria-label') || b.title || b.getAttribute('data-tip') || b.textContent || b.value || '').trim().replace(/\s+/g, ' ').slice(0, 40);
            return ((f ? (f.getAttribute('action') || '') + '|' : 'js|') + (b.getAttribute('formaction') || '') + '|' + (b.name || '') + '=' + (b.value || '') + '|' + txt + '|' + (b.className || '')) === sig;
          }, [CTL, i, info.sig]);
          if (!same) { continue; }
          const how = await reveal(w.page, i);
          if (how === 'hidden') { continue; }
          if (await w.page.evaluate(([CTL, i]) => document.querySelectorAll(CTL)[i].disabled, [CTL, i])) { continue; }
          const bad = [];
          const onResp = (r) => { const st = r.status(); if (st >= 400 && !/\/api\/ping|favicon/.test(r.url())) { bad.push(st + ' ' + r.request().method() + ' ' + r.url().replace(L.base, '')); } };
          const onErr = (e) => bad.push('script error: ' + e.message.slice(0, 80));
          w.page.on('response', onResp);
          w.page.on('pageerror', onErr);
          clicks++;
          await w.page.locator(CTL).nth(i).click({ timeout: 3000, force: true }).catch((e) => bad.push('could not be pressed: ' + e.message.split('\n')[0].slice(0, 80)));
          await w.page.waitForTimeout(700);
          await w.page.waitForLoadState('load').catch(() => {});
          const at = w.page.url();
          if (at.startsWith('chrome-error')) { bad.push('the browser showed an error page'); }
          if (/\/null(\?|$)/.test(at)) { bad.push('went to /null'); }
          const html = await w.page.content().catch(() => '');
          if (/An unhandled error has occurred|Something went wrong/.test(html)) { bad.push('an error page was shown'); }
          w.page.off('response', onResp);
          w.page.off('pageerror', onErr);
          if (bad.length) { found.push(`${u}: "${info.txt}" (${how}): ${bad.join('; ')}`); }
          if (at.includes('/sign-in') || at.includes('/signed-out')) {
            await w.close();
            w = await L.open(L.SMALL);
          }
        }
      }
    } finally {
      await w.close();
    }
    found.checked = clicks;
    return found;
  },
};
