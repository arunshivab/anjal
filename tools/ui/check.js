#!/usr/bin/env node
// Anjal screen checks (rc.15; owner 7 Oct 2026): opens every webmail screen in a real browser and
// checks what a person would notice. Item 47 (no page scrolling on mail screens at 1366x768) and
// UX-09 (every control has a name; keyboard-only pass), together with the system-wide checks made
// on 7 Oct: menus on screen and on top, menus closing, buttons level and still, nothing squeezed,
// no words cut off, and every link and button working.
//
//   node check.js                  every check, in English
//   node check.js --only names,tips
//   node check.js --skip reach     everything but pressing every button
//   node check.js --only clip --lang ta     a language preview (findings listed, not failed)
//   node check.js --only clip --lang ta,hi --strict   rc.27 (item 75): the languages job - failed like English
//
// Exit code 1 when anything is found; each check's findings are also written to out/<check>.txt.
// Run it against a throwaway copy only (setup.sh makes one): "reach" presses every button.
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./lib.js');

const ORDER = ['noscroll', 'names', 'tips', 'menus', 'close', 'align', 'shrink', 'clip', 'same', 'steady', 'live', 'lineup', 'sizes', 'scrollbars', 'flap', 'swap', 'keyboard', 'reach', 'zoom', 'contrast', 'devices'];

function arg(name) {
  const i = process.argv.indexOf('--' + name);
  return i > 0 ? process.argv[i + 1] : null;
}

(async () => {
  const only = arg('only') ? arg('only').split(',') : ORDER;
  const skip = arg('skip') ? arg('skip').split(',') : [];
  const langs = (arg('lang') || 'en').split(',');
  const strict = process.argv.includes('--strict');
  const unknown = only.concat(skip).filter((c) => !ORDER.includes(c));
  if (unknown.length) {
    console.error('Unknown check: ' + unknown.join(', ') + '. Checks: ' + ORDER.join(', '));
    process.exit(2);
  }
  const run = ORDER.filter((c) => only.includes(c) && !skip.includes(c));
  fs.mkdirSync(L.out, { recursive: true });

  const t0 = Date.now();
  const screens = await L.allScreens();
  console.log(`${screens.length} screens found (${Math.round((Date.now() - t0) / 1000)}s); list in out/screens.txt`);

  let total = 0;
  for (const lang of langs) {
  for (const name of run) {
    const check = require('./checks/' + name + '.js');
    const t = Date.now();
    let found;
    const lostBefore = L.lost.length;
    try {
      found = await check.run(screens, { lang });
      L.lost.slice(lostBefore).forEach((u) => found.push(`the page closed or crashed while checking ${u}`));
    } catch (e) {
      found = ['the check itself failed: ' + (e && e.stack ? e.stack.split('\n').slice(0, 3).join(' | ') : e)];
    }
    const file = name + (lang === 'en' ? '' : '-' + lang) + '.txt';
    fs.writeFileSync(path.join(L.out, file), found.join('\n') + (found.length ? '\n' : ''));
    const counted = found.checked !== undefined ? `, ${found.checked} checked` : '';
    const word = !found.length ? 'ok  ' : check.reportOnly || (lang !== 'en' && !strict) ? 'list' : 'FAIL';
    console.log(`\n${word} ${check.title}${lang === 'en' ? '' : ' [' + lang + ']'}: ${found.length} found${counted} (${Math.round((Date.now() - t) / 1000)}s)`);
    found.slice(0, 40).forEach((f) => console.log('     ' + f));
    if (found.length > 40) {
      console.log(`     ... and ${found.length - 40} more in out/${file}`);
    }
    // rc.27: a check that only lists (zoom) never fails; a language fails only with --strict (the languages job).
    if (!check.reportOnly && (lang === 'en' || strict)) {
      total += found.length;
    }
  }
  }
  console.log(`\n${total} finding(s) that fail in ${Math.round((Date.now() - t0) / 1000)}s` + (langs.some((g) => g !== 'en') && !strict ? ' (language preview: listed for the language check, not failed)' : ''));
  process.exit(total ? 1 : 0);
})().catch((e) => {
  console.error(e);
  process.exit(2);
});
