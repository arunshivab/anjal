// Owner, 7 Oct 2026 ("system-wide check for choice box or button"): no words are cut off or
// spill out of any choice box, button or text box's example text - with every menu open too, at
// both screen sizes. Run with --lang to check a language preview (operators only).
'use strict';
const L = require('../lib.js');

function check() {
  const out = []; const cv = document.createElement('canvas').getContext('2d');
  const vis = (e) => { const r = e.getBoundingClientRect(); const cs = getComputedStyle(e); return r.width > 2 && r.height > 2 && cs.visibility !== 'hidden' && cs.display !== 'none' && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; };
  const name = (e) => e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).slice(0, 2).join('.') : '') + (e.name ? '[name=' + e.name + ']' : '');
  const textW = (t, cs) => { cv.font = cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily; return cv.measureText(t).width; };
  for (const s of document.querySelectorAll('select')) {
    if (!vis(s)) { continue; }
    const cs = getComputedStyle(s);
    const room = s.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight) - (cs.appearance === 'none' ? 0 : 18);
    const sel = s.options[s.selectedIndex];
    if (sel) { const w = textW(sel.text, cs); if (w > room + 1) { out.push(['choice box: the chosen option is cut', name(s), `"${sel.text}" needs ${Math.round(w)}px, has ${Math.round(room)}px`]); } }
    let worst = null;
    for (const o of s.options) { const w = textW(o.text, cs); if (w > room + 1 && (!worst || w > worst[1])) { worst = [o.text, w]; } }
    if (worst && (!sel || worst[0] !== sel.text)) { out.push(['choice box: an option would be cut when chosen', name(s), `"${worst[0]}" needs ${Math.round(worst[1])}px, has ${Math.round(room)}px`]); }
  }
  for (const e of document.querySelectorAll('button, summary, a.btn, a.pbtn, a.eb, a.linkbtn, a.pill, a.chip, a.tchip, .seg a, label.segopt, a.rail-item, a.pmi, button.pmi, a.eback, .lh-pills a')) {
    if (!vis(e)) { continue; }
    const r = e.getBoundingClientRect(); const rng = document.createRange(); let worst = 0; let what = '';
    const walker = document.createTreeWalker(e, NodeFilter.SHOW_TEXT); let n;
    while ((n = walker.nextNode())) {
      if (!n.textContent.trim()) { continue; }
      const pe = n.parentElement;
      if (pe !== e && pe.closest('.pmenu-body,.rte-menu') && e.contains(pe.closest('.pmenu-body,.rte-menu'))) { continue; }
      const ps = getComputedStyle(pe);
      if (ps.display === 'none' || ps.visibility === 'hidden') { continue; }
      rng.selectNodeContents(n);
      for (const q of rng.getClientRects()) {
        if (q.width < 1) { continue; }
        const over = Math.max(q.right - r.right, r.left - q.left, q.bottom - r.bottom, r.top - q.top);
        if (over > worst) { worst = over; what = n.textContent.trim().slice(0, 40); }
      }
    }
    if (worst > 1.5) { out.push([getComputedStyle(e).overflow !== 'visible' ? 'button: words cut' : 'button: words spill past its edge', name(e), `"${what}" goes ${Math.round(worst)}px past the edge`]); }
    for (const c of [e, ...e.querySelectorAll('*')]) {
      const cs = getComputedStyle(c);
      if (cs.textOverflow === 'ellipsis' && c.scrollWidth > c.clientWidth + 1 && vis(c)) { out.push(['button: words end in …', name(e), `"${c.textContent.trim().slice(0, 50)}"`]); }
    }
  }
  for (const i of document.querySelectorAll('input[placeholder]')) {
    if (!vis(i) || i.value) { continue; }
    const cs = getComputedStyle(i);
    const room = i.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight); const w = textW(i.placeholder, cs);
    if (w > room + 1) { out.push(['text box: example text cut', name(i), `"${i.placeholder}" needs ${Math.round(w)}px, has ${Math.round(room)}px`]); }
  }
  // Owner, 8 Oct (system-wide): in every table a short entry - a status, a count, a date, a word or two -
  // stays on one line; longer text may take a second.
  for (const c of document.querySelectorAll('td, th')) { if (!vis(c)) { continue; } const t = c.textContent.replace(/\s+/g, ' ').trim(); if (!t || t.length > 24 || t.split(' ').length > 2) { continue; }
    // A word or two broken over two lines (each piece of text on its own; a name over an address is meant).
    const tw = document.createTreeWalker(c, NodeFilter.SHOW_TEXT); let tn; let broken = false;
    while ((tn = tw.nextNode())) { if (!tn.textContent.trim()) { continue; } const rng = document.createRange(); rng.selectNodeContents(tn); const tops = new Set([...rng.getClientRects()].filter((q) => q.width > 0).map((q) => Math.round(q.top))); if (tops.size > 1) { broken = true; } }
    if (broken) { out.push(['table: a short entry takes two lines', name(c), `"${t}"`]); } }
  // Owner, 9 Oct (system-wide, after "Not checked" ran into the copy button): nothing in a table cell -
  // a pill, a button, an icon or words that do not wrap - reaches past the cell into its neighbour.
  // Words meant to end in "…" are clipped by design and are not counted.
  for (const c of document.querySelectorAll('td, th')) {
    if (!vis(c)) { continue; }
    const r = c.getBoundingClientRect(); let worst = 0; let what = '';
    for (const e of c.querySelectorAll('*')) {
      const cs = getComputedStyle(e);
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.position === 'absolute' || cs.position === 'fixed' || e.closest('details:not([open]) > :not(summary)')) { continue; }
      const q = e.getBoundingClientRect(); if (q.width < 1 || q.height < 1) { continue; }
      const over = Math.max(q.right - r.right, r.left - q.left);
      if (over > worst) { worst = over; what = (e.getAttribute('aria-label') || e.textContent || e.tagName).replace(/\s+/g, ' ').trim().slice(0, 40); }
    }
    const tw = document.createTreeWalker(c, NodeFilter.SHOW_TEXT); let tn;
    while ((tn = tw.nextNode())) {
      if (!tn.textContent.trim()) { continue; }
      let clipped = false;
      for (let p = tn.parentElement; p && p !== c.parentElement; p = p.parentElement) { const ps = getComputedStyle(p); if (ps.textOverflow === 'ellipsis' || ps.overflow !== 'visible' && p !== c) { clipped = true; break; } }
      if (clipped) { continue; }
      const rng = document.createRange(); rng.selectNodeContents(tn);
      for (const q of rng.getClientRects()) { if (q.width < 1) { continue; } const over = Math.max(q.right - r.right, r.left - q.left); if (over > worst) { worst = over; what = tn.textContent.trim().slice(0, 40); } }
    }
    if (worst > 1.5) { out.push(['table: something reaches past its cell', name(c), `"${what}" goes ${Math.round(worst)}px into the next cell`]); }
  }
  // The middle box of Settings and the consoles never scrolls sideways: nothing in it is wider than it.
  for (const b of document.querySelectorAll('.setmain, .formcard')) { if (vis(b) && b.scrollWidth > b.clientWidth + 1) { out.push(['box: something in it is wider than the box', name(b), `${b.scrollWidth - b.clientWidth}px too wide`]); } }
  return out;
}

async function language(w, lang) {
  await L.visit(w, '/settings/language');
  await w.page.click(`input[name=language][value=${lang}]`, { force: true });
  await w.page.waitForTimeout(1200);
}

module.exports = {
  name: 'clip',
  title: 'No words cut off or spilling in choice boxes, buttons, text boxes and table cells',
  async run(screens, opts) {
    const lang = (opts && opts.lang) || 'en';
    const found = new Map();
    for (const size of [L.SMALL, L.MID, L.LARGE]) {
      const w = await L.open(size);
      try {
        if (lang !== 'en') { await language(w, lang); }
        for (const url of screens) {
          if (!(await L.visit(w, url, 350))) { continue; }
          const add = (rows, extra) => rows.forEach((r) => { const k = r.join('|'); if (!found.has(k)) { found.set(k, `${size.join('x')} ${url}${extra}: ${r[0]}: ${r[1]} - ${r[2]}`); } });
          add(await w.page.evaluate(check), '');
          // Owner, 9 Oct: on a laptop screen the rail folds itself until the person chooses; someone who
          // keeps it open (as the owner does) has less room in the middle, so that is checked too.
          if (size === L.SMALL && await w.page.evaluate(() => { const r = document.querySelector('nav.rail.auto'); if (r) { r.classList.remove('auto'); } return !!r; })) {
            await w.page.waitForTimeout(120);
            add(await w.page.evaluate(check), ' [rail open]');
            await w.page.evaluate(() => { const r = document.querySelector('nav.rail'); if (r) { r.classList.add('auto'); } });
          }
          const n = await w.page.$$eval('details', (d) => d.length);
          for (let i = 0; i < n; i++) {
            const opened = await w.page.evaluate((i) => { const d = document.querySelectorAll('details')[i]; const s = d && d.querySelector(':scope>summary'); if (!s || !s.getBoundingClientRect().width) { return false; } d.open = true; return true; }, i);
            if (!opened) { continue; }
            await w.page.waitForTimeout(80);
            add(await w.page.evaluate(check), ' [menu open]');
            await w.page.evaluate((i) => { document.querySelectorAll('details')[i].open = false; }, i);
          }
        }
        if (lang !== 'en') { await language(w, 'en'); }
      } finally {
        await w.close();
      }
    }
    return [...found.values()];
  },
};
