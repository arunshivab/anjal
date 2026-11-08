// rc.27 (item 71, DES-17 §3.4; item 79): an open mail in every theme, light and dark, high contrast
// included - the envelope's labels, values, buttons and tags against what is really behind them: 4.5:1
// for text, 3:1 for large text. Then the same mail with Windows' forced colours (Chromium's emulation):
// every button keeps a visible border.
'use strict';
const L = require('../lib.js');


function measure() {
  function rgb(s) {
    const m = s.match(/rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)/);
    return m ? [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : +m[4]] : null;
  }
  function lum(c) {
    const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]);
  }
  function over(top, under) {
    const a = top[3];
    return [top[0] * a + under[0] * (1 - a), top[1] * a + under[1] * (1 - a), top[2] * a + under[2] * (1 - a), 1];
  }
  function behind(e) {
    const layers = [];
    for (let n = e; n; n = n.parentElement) {
      const cs = getComputedStyle(n);
      if (cs.backgroundImage !== 'none' && n !== e) { return null; } // a picture or gradient: not measured
      const c = rgb(cs.backgroundColor);
      if (c && c[3] > 0) {
        layers.push(c);
        if (c[3] >= 1) { break; }
      }
    }
    let colour = [255, 255, 255, 1];
    for (let i = layers.length - 1; i >= 0; i--) { colour = over(layers[i], colour); }
    return colour;
  }
  const env = document.querySelector('aside.envelope, .envelope');
  if (!env || env.getBoundingClientRect().width < 10) { return { none: true }; }
  const found = [];
  // What a closed menu holds is not drawn, though Chromium gives it a size (the rc.23 rule of "same").
  const hidden = (e) => { const d = e.closest('details:not([open])'); return d && !e.closest('summary'); };
  for (const e of env.querySelectorAll('*')) {
    if (hidden(e)) { continue; }
    const own = [...e.childNodes].some((t) => t.nodeType === 3 && t.textContent.trim().length > 0);
    const r = e.getBoundingClientRect();
    if (!own || r.width < 2 || r.height < 2) { continue; }
    const cs = getComputedStyle(e);
    if (cs.visibility === 'hidden' || +cs.opacity === 0) { continue; }
    const bg = behind(e);
    const fg = rgb(cs.color);
    if (!bg || !fg) { continue; }
    const ink = over(fg, bg);
    const a = lum(ink); const b = lum(bg);
    const ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
    const size = parseFloat(cs.fontSize);
    const large = size >= 24 || (size >= 18.66 && +cs.fontWeight >= 700);
    if (ratio < (large ? 3 : 4.5) - 0.005) {
      const name = e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/)[0] : '');
      found.push(`${name} "${e.textContent.trim().slice(0, 24)}" ${ratio.toFixed(2)}:1`);
    }
  }
  return { found };
}

function borders() {
  const env = document.querySelector('aside.envelope, .envelope');
  const out = [];
  for (const b of (env || document).querySelectorAll('button, a.btn, a.eb')) {
    const d = b.closest('details:not([open])');
    if (d && !b.closest('summary')) { continue; }
    const r = b.getBoundingClientRect();
    const cs = getComputedStyle(b);
    if (r.width < 2 || cs.visibility === 'hidden') { continue; }
    if (parseFloat(cs.borderTopWidth) < 1 && parseFloat(cs.outlineWidth) < 1) {
      out.push((b.getAttribute('aria-label') || b.textContent || b.className).trim().slice(0, 30));
    }
  }
  return out;
}

module.exports = {
  name: 'contrast',
  title: 'Contrast of the envelope in every theme, and forced colours',
  async run(screens) {
    const found = [];
    // Three different mails, so the conversation, files and checks of each are measured.
    const mails = screens.filter((u) => /^\/folder\/[A-Za-z]+\?.*open=/.test(u) && !/[?&]in=desk/.test(u)).slice(0, 3);
    if (mails.length === 0) {
      return ['no open mail among the screens to measure'];
    }
    const mail = mails[0];
    const w = await L.open(L.LARGE);
    try {
      // No transitions: a colour measured while it changes from the last theme to this one is neither.
      await w.page.emulateMedia({ reducedMotion: 'reduce' });
      await L.visit(w, mail, 400);
      let checked = 0;
      // Every light and dark theme the stylesheet has (the "auto" ones are one of these).
      const themes = await w.page.evaluate(() => {
        const names = new Set();
        for (const sheet of document.styleSheets) {
          let rules = [];
          try { rules = [...sheet.cssRules]; } catch (e) { continue; }
          for (const r of rules) {
            for (const m of (r.selectorText || '').matchAll(/data-theme="([a-z]+-(?:light|dark))"/g)) { names.add(m[1]); }
          }
        }
        return [...names].sort();
      });
      for (const each of mails) {
        if (each !== mail) {
          await L.visit(w, each, 400);
        }
        for (const name of themes) {
          await w.page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), name);
          await w.page.waitForTimeout(120);
          const r = await w.page.evaluate(measure);
          if (r.none) { found.push(`${each}: no envelope to measure`); break; }
          checked++;
          r.found.forEach((f) => { if (!found.includes(`${name}: ${f}`)) { found.push(`${name}: ${f}`); } });
        }
      }
      await w.page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
      await L.visit(w, mail, 400);
      (await w.page.evaluate(borders)).forEach((b) => found.push(`forced colours: the button "${b}" has no border`));
      found.checked = checked;
    } finally {
      await w.close();
    }
    return found;
  },
};
