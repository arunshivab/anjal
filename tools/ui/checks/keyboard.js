// UX-09 (owner, 7 Oct 2026): the whole webmail works from the keyboard alone. On every kind of
// screen, Tab is pressed from the top until focus comes round again, and each stop is checked:
// it can be seen, it shows that it has focus, and focus is never trapped. Every control showing
// on the screen must be reached. The result is also written as a test record (keyboard.md).
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('../lib.js');

const MAX_TABS = 400;

function mark() {
  const sel = 'a[href], button:not([disabled]), input:not([type=hidden]):not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"]), [contenteditable=true], iframe';
  const shown = (e) => {
    const r = e.getBoundingClientRect(); const cs = getComputedStyle(e);
    if (r.width < 1 || r.height < 1 || cs.visibility === 'hidden') { return false; }
    if (e.closest('[inert], [aria-hidden=true]')) { return false; }
    const d = e.closest('details:not([open])');
    return !d || (e.tagName === 'SUMMARY' && e.parentElement === d);
  };
  let n = 0;
  const list = [];
  for (const e of document.querySelectorAll(sel)) {
    if (e.tabIndex < 0 && e.tagName !== 'IFRAME') { continue; }
    e.dataset.kb = String(n);
    list.push({ id: n, shown: shown(e), frame: e.tagName === 'IFRAME', radio: e.type === 'radio' ? e.name : null, name: e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/)[0] : '') + ' "' + (e.getAttribute('aria-label') || e.textContent || e.value || e.name || '').trim().replace(/\s+/g, ' ').slice(0, 30) + '"' });
    n++;
  }
  return list;
}

function look() {
  const e = document.activeElement;
  if (!e || e === document.body || e === document.documentElement) { return { id: 'body' }; }
  const r = e.getBoundingClientRect();
  const style = (x) => { const cs = getComputedStyle(x); return [cs.outlineStyle, cs.outlineWidth, cs.outlineColor, cs.boxShadow, cs.borderColor, cs.backgroundColor, cs.color, cs.textDecorationLine].join('|'); };
  // The sign of focus may be on the control or on what holds it (a message row is outlined when
  // its subject link has focus).
  const near = [e, e.parentElement, e.parentElement && e.parentElement.parentElement, e.closest('.mrow, li, tr')].filter(Boolean);
  const all = () => near.map(style).join('#');
  const focused = all();
  const ring = getComputedStyle(e).outlineStyle !== 'none' && parseFloat(getComputedStyle(e).outlineWidth) > 0;
  e.blur();
  const plain = all();
  e.focus();
  const name = e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/)[0] : '') + ' "' + (e.getAttribute('aria-label') || e.textContent || e.value || e.name || '').trim().replace(/\s+/g, ' ').slice(0, 30) + '"';
  return {
    id: e.dataset.kb === undefined ? name : e.dataset.kb,
    name,
    iframe: e.tagName === 'IFRAME',
    seen: r.width >= 1 && r.height >= 1 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth,
    shows: ring || focused !== plain || e.tagName === 'IFRAME',
  };
}

module.exports = {
  name: 'keyboard',
  title: 'Keyboard only: every control reached, focus shown, never trapped (UX-09)',
  async run(screens) {
    const found = [];
    const record = [];
    const shapes = new Set();
    const w = await L.open(L.SMALL);
    try {
      for (const url of screens) {
        const s = L.shape(url).replace(/\?.*/, '');
        if (shapes.has(s)) { continue; }
        shapes.add(s);
        if (!(await L.visit(w, url, 500))) { continue; }
        const all = await w.page.evaluate(mark);
        await w.page.evaluate(() => { if (document.activeElement) { document.activeElement.blur(); } window.scrollTo(0, 0); });
        const reached = new Set();
        const problems = [];
        // A screen may put focus somewhere to start (compose puts it in To); Tab goes on from
        // there, past the end of the page and round from the top, until it is back where it began.
        let first = null; let last = null; let same = 0; let stops = 0; let done = false; let ends = 0;
        for (let t = 0; t < MAX_TABS; t++) {
          await w.page.keyboard.press('Tab');
          let at = await w.page.evaluate(look).catch(() => null);
          // A focus ring fades in (a short transition): look again once it has, before saying none shows.
          if (at && (!at.shows || !at.seen)) {
            await w.page.waitForTimeout(250);
            at = await w.page.evaluate(look).catch(() => null);
          }
          if (!at) { problems.push('the screen changed while moving with Tab'); break; }
          if (at.id === 'body') { ends++; if (ends > 3 || (first === null && ends > 1)) { done = true; break; } continue; }
          if (first === null) { first = at.id; } else if (at.id === first) { done = true; break; }
          if (at.id === last) {
            same++;
            if (same > (at.iframe ? 80 : 2)) { problems.push('focus is trapped on ' + at.name); break; }
            continue;
          }
          same = 0; last = at.id; stops++;
          reached.add(String(at.id));
          if (!at.seen) { problems.push('focus goes somewhere that cannot be seen: ' + at.name); }
          if (!at.shows) { problems.push('no sign of focus on ' + at.name); }
        }
        if (!done && !problems.length) { problems.push('Tab did not come round again within ' + MAX_TABS + ' presses'); }
        const groups = new Set(all.filter((c) => c.radio && reached.has(String(c.id))).map((c) => c.radio));
        // A message's body is a frame: Tab goes into it only when the mail has links of its own.
        const missed = all.filter((c) => c.shown && !c.frame && !reached.has(String(c.id)) && !(c.radio && groups.has(c.radio)));
        for (const m of missed) { problems.push('never reached with Tab: ' + m.name); }
        const unique = [...new Set(problems)];
        unique.forEach((x) => found.push(`${url}: ${x}`));
        record.push([url, stops, all.filter((c) => c.shown).length, unique.length]);
      }
    } finally {
      await w.close();
    }
    const lines = ['# Keyboard-only pass (UX-09)', '', `Checked ${new Date().toISOString().slice(0, 10)} at 1366x768 by tools/ui (check "keyboard"): on each kind of screen, Tab pressed from the top until focus came round again. Every stop must be visible and show that it has focus; focus must never be trapped; every control showing must be reached.`, '', '| Screen | Tab stops | Controls showing | Problems |', '|---|---:|---:|---:|'];
    for (const [u, n, shown, bad] of record) { lines.push(`| \`${u}\` | ${n} | ${shown} | ${bad} |`); }
    lines.push('', `Screens: ${record.length}. Problems: ${found.length}.`, '');
    fs.mkdirSync(L.out, { recursive: true });
    fs.writeFileSync(path.join(L.out, 'keyboard.md'), lines.join('\n'));
    found.checked = record.length;
    return found;
  },
};
