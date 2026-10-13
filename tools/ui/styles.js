#!/usr/bin/env node
// Anjal: a record of how every screen is drawn, to prove a stylesheet clean-up changes nothing
// (design review, 11 Oct 2026, item 4.8: "one block per component ... No visual change intended;
// compare screenshots before and after").
//
// For each screen in out/screens.txt (written by check.js) and each size, it records every visible
// element's position and size and the computed values of the properties that change how it looks,
// in the light and the dark theme, as a short fingerprint per element (one screen per line).
// Two records are compared element by element.
//
//   node styles.js snap before.json            record (the webmail must be running; see setup.sh)
//   node styles.js snap after.json
//   node styles.js diff before.json after.json  list every difference; exit code 1 when any
//
// Settings: as check.js (ANJAL_UI_BASE, ANJAL_UI_ADDRESS, ANJAL_UI_PASSWORD), and
// ANJAL_UI_SCREENS, a file with the screens to record (default out/screens.txt), and ANJAL_UI_CSS,
// a stylesheet to use instead of the one the webmail serves - so both records come from the same
// webmail, the same mail and the same markup, and only the stylesheet differs.
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./lib.js');

const PROPS = [
  'display', 'position', 'top', 'right', 'bottom', 'left', 'z-index', 'float', 'visibility', 'opacity',
  'width', 'height', 'min-width', 'min-height', 'max-width', 'max-height',
  'margin-top', 'margin-right', 'margin-bottom', 'margin-left',
  'padding-top', 'padding-right', 'padding-bottom', 'padding-left',
  'border-top-width', 'border-right-width', 'border-bottom-width', 'border-left-width',
  'border-top-style', 'border-right-style', 'border-bottom-style', 'border-left-style',
  'border-top-color', 'border-right-color', 'border-bottom-color', 'border-left-color',
  'border-top-left-radius', 'border-top-right-radius', 'border-bottom-left-radius', 'border-bottom-right-radius',
  'outline-style', 'outline-width', 'outline-color', 'box-shadow',
  'color', 'background-color', 'background-image', 'font-family', 'font-size', 'font-weight', 'font-style',
  'line-height', 'letter-spacing', 'text-align', 'text-transform', 'text-decoration-line', 'white-space',
  'text-overflow', 'overflow-x', 'overflow-y', 'flex-direction', 'flex-wrap', 'flex-grow', 'flex-shrink',
  'flex-basis', 'justify-content', 'align-items', 'align-self', 'gap', 'grid-template-columns',
  'grid-template-rows', 'transform', 'cursor', 'pointer-events', 'fill', 'stroke', 'font-variant-numeric',
  'scrollbar-gutter', 'transition-property', 'animation-name',
];

const SIZES = [L.SMALL, L.LARGE];
const THEMES = ['anjal-light', 'anjal-dark'];

async function record(file) {
  const list = process.env.ANJAL_UI_SCREENS || path.join(L.out, 'screens.txt');
  const screens = fs.existsSync(list) ? fs.readFileSync(list, 'utf8').split('\n').filter(Boolean) : await L.allScreens();
  let count = 0;
  fs.writeFileSync(file, '');
  const b = await L.launch();
  try {
    for (const size of SIZES) {
      for (const theme of THEMES) {
        const w = await L.open(size, b);
        for (const url of screens) {
          if (!(await L.visit(w, url, 600))) {
            continue;
          }
          const key = `${size[0]}x${size[1]} ${theme} ${url}`;
          const shot = await w.page.evaluate(([props, th]) => {
            // No movement while measuring: a value caught half-way through a transition differs by chance.
            const still = document.createElement('style');
            still.textContent = '*,*::before,*::after{transition:none!important;animation:none!important}';
            document.head.appendChild(still);
            document.documentElement.setAttribute('data-theme', th);
            // A short fingerprint of each element's look (every property in PROPS) and its box,
            // so a record of every screen stays small; the diff names the elements that differ.
            const hash = (str) => {
              let h = 2166136261;
              for (let i = 0; i < str.length; i++) {
                h ^= str.charCodeAt(i);
                h = Math.imul(h, 16777619);
              }
              return (h >>> 0).toString(36);
            };
            const out = {};
            const pathOf = (el) => {
              const parts = [];
              for (let e = el; e && e.nodeType === 1 && e !== document.documentElement; e = e.parentElement) {
                let i = 1;
                for (let s = e.previousElementSibling; s; s = s.previousElementSibling) {
                  if (s.tagName === e.tagName) {
                    i++;
                  }
                }
                parts.unshift(e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + ':' + i);
              }
              return parts.join('>');
            };
            for (const el of document.querySelectorAll('body *')) {
              if (['SCRIPT', 'STYLE', 'TEMPLATE', 'NOSCRIPT'].includes(el.tagName)) {
                continue;
              }
              const r = el.getBoundingClientRect();
              const cs = getComputedStyle(el);
              const look = props.map((p) => cs.getPropertyValue(p)).join('|');
              out[pathOf(el)] = hash(look) + ' ' + [Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)].join(',') + ' ' + (el.getAttribute('class') || '').slice(0, 60);
            }
            return out;
          }, [PROPS, theme]);
          fs.appendFileSync(file, JSON.stringify([key, shot]) + '\n');
          count++;
        }
        await w.close();
      }
    }
  } finally {
    await b.close();
  }
  console.log(`recorded ${count} screens into ${file}`);
}

function read(file) {
  const map = {};
  for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
    if (line) {
      const [key, shot] = JSON.parse(line);
      map[key] = shot;
    }
  }
  return map;
}

function diff(a, b) {
  const A = read(a);
  const B = read(b);
  const found = [];
  for (const key of Object.keys(A)) {
    if (!B[key]) {
      found.push(`${key}: screen missing in the second record`);
      continue;
    }
    for (const el of Object.keys(A[key])) {
      const x = A[key][el];
      const y = B[key][el];
      if (y === undefined) {
        found.push(`${key}: ${el} missing (${x})`);
      } else if (x !== y) {
        found.push(`${key}: ${el}: ${x}  ->  ${y}`);
      }
    }
  }
  found.forEach((f) => console.log(f));
  console.log(`${found.length} difference(s)`);
  return found.length;
}

(async () => {
  const [cmd, a, b] = process.argv.slice(2);
  if (cmd === 'snap' && a) {
    await record(a);
  } else if (cmd === 'diff' && a && b) {
    process.exit(diff(a, b) ? 1 : 0);
  } else {
    console.error('node styles.js snap <file> | diff <a> <b>');
    process.exit(2);
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
