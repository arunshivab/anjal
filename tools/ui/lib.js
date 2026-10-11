// Anjal screen checks (rc.15, items 47 and UX-09; owner 7 Oct 2026): shared pieces.
//
// The checks open the webmail in a real browser (Chromium, through Playwright), signed in as an
// operator who is also an organisation's administrator, so every screen can be reached. They are
// meant for a throwaway copy filled with sample mail (tools/seed_sample_mail.py): one check
// presses every button, including Delete.
//
// Settings (environment):
//   ANJAL_UI_BASE      the webmail's address           (default http://127.0.0.1:5080)
//   ANJAL_UI_ADDRESS   who signs in                    (default arun@qa.test)
//   ANJAL_UI_PASSWORD  their password                  (default Lotus-Garden-42)
//   ANJAL_UI_CHROMIUM  a Chromium to use instead of Playwright's own (optional)
//   ANJAL_UI_OUT       where reports are written       (default tools/ui/out)
'use strict';
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const base = (process.env.ANJAL_UI_BASE || 'http://127.0.0.1:5080').replace(/\/+$/, '');
const address = process.env.ANJAL_UI_ADDRESS || 'arun@qa.test';
const password = process.env.ANJAL_UI_PASSWORD || 'Lotus-Garden-42';
const out = process.env.ANJAL_UI_OUT || path.join(__dirname, 'out');

// The screen sizes checked: the smallest laptop (1366x768), the owner's laptop and a common desktop.
const SMALL = [1366, 768];
const LARGE = [1920, 1080];
// The owner's own laptop: 1920 x 1080 at 125% scale, which a page sees as 1536 x 730.
const MID = [1536, 730];

// Links never followed while looking for screens: they sign out, download a file, or show a
// message's raw source.
const SKIP = /^\/(auth\/|sign-out|signed-out)|\/raw\b|raw=|download|export|\/attachment\/|\.(css|js|png|svg|ico|woff2?)(\?|$)/;

// Screens on which the page closed or crashed during a check (see open).
const lost = [];

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

// Every check sees real scroll bars, as Windows draws them (owner, 10 Oct 2026: the marigold flap
// sat on the list's scroll bar on Windows; a headless browser hides scroll bars unless asked, so
// no check had seen one).
async function launch() {
  const opts = { ignoreDefaultArgs: ['--hide-scrollbars'] };
  if (process.env.ANJAL_UI_CHROMIUM) {
    opts.executablePath = process.env.ANJAL_UI_CHROMIUM;
  }
  return chromium.launch(opts);
}

// A browser window of the given size, signed in. Script errors on any page are collected. If the
// page closes or crashes during a check, the next visit opens a new one and the check is told
// (lost), so a crash can never make a check pass by skipping the screens after it.
async function open(size, browser) {
  const own = !browser;
  const b = browser || (await launch());
  const w = { browser: b, size };
  w.page = await newPage(b, size);
  w.close = async () => (own ? b.close() : w.page.close());
  return w;
}

async function newPage(b, size) {
  const page = await b.newPage({ viewport: { width: size[0], height: size[1] } });
  page.scriptErrors = [];
  page.on('pageerror', (e) => page.scriptErrors.push(e.message));
  page.on('dialog', (d) => (d.type() === 'prompt' ? d.dismiss() : d.accept()).catch(() => {}));
  await signIn(page);
  return page;
}

async function signIn(page) {
  await page.goto(base + '/sign-in');
  await page.fill('input[name=address]', address);
  await page.fill('input[name=password]', password);
  await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
  if (page.url().includes('/sign-in')) {
    throw new Error('Could not sign in as ' + address + ' at ' + base + ' - check ANJAL_UI_ADDRESS and ANJAL_UI_PASSWORD.');
  }
  if (await page.$('.welcome')) {
    await Promise.all([page.waitForNavigation(), page.click('.welcome button.btn-q')]);
  }
}

// Go to a screen in window w and let its script settle. Returns false when the screen could not be
// opened.
async function visit(w, url, settle) {
  if (w.page.isClosed()) {
    lost.push(w.last || url);
    w.page = await newPage(w.browser, w.size);
  }
  w.last = url;
  try {
    const res = await w.page.goto(base + url, { timeout: 20000 });
    w.status = res ? res.status() : 0;
    await w.page.waitForTimeout(settle === undefined ? 400 : settle);
  } catch (e) {
    if (w.page.isClosed()) {
      return visit(w, url, settle);
    }
    return false;
  }
  if (w.page.url().includes('/sign-in')) {
    await signIn(w.page);
    return visit(w, url, settle);
  }
  return true;
}

// The kind of screen an address stands for: its path with ids replaced, and the names (not the
// values) of what follows "?". Two of each kind are enough: "?a=7d" and "?a=30d" show the same
// screen with different figures, while "?new=1" and "?invite=1" are different screens.
function shape(url) {
  const [p, q] = url.split('?');
  const keys = q ? [...new URLSearchParams(q).keys()].sort().join('&') : '';
  return p.replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '{id}') + (keys ? '?' + keys : '');
}

// At most this many addresses on one path (the dashboards' period switches combine into dozens).
const PER_PATH = 12;

// Every screen reachable from the starting ones by following links, at most `keep` of each shape
// and `limit` in all. Opened as the operator, so the organisation and Anjal consoles are included.
async function findScreens(w, limit, keep) {
  const start = [
    '/folder/INBOX', '/folder/Sent', '/folder/Drafts', '/folder/Junk', '/folder/Trash', '/folder/Archive',
    '/folder/Scheduled', '/outbox', '/no-reply', '/compose', '/search?q=the', '/contacts', '/dashboard',
    '/dashboard/org', '/settings', '/org', '/ops',
  ];
  const seen = new Set();
  const perShape = new Map();
  const found = [];
  const queue = start.slice();
  while (queue.length && found.length < (limit || 400)) {
    const url = queue.shift();
    if (seen.has(url)) {
      continue;
    }
    seen.add(url);
    const s = shape(url);
    const at0 = url.split('?')[0].replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '{id}');
    if ((perShape.get(s) || 0) >= (keep || 2) || (perShape.get(at0 + '#path') || 0) >= PER_PATH) {
      continue;
    }
    if (!(await visit(w, url, 250))) {
      continue;
    }
    const at = new URL(w.page.url());
    if (at.origin !== new URL(base).origin || at.pathname + at.search !== url) {
      // A redirect: the screen it landed on is found under its own address.
      if (!seen.has(at.pathname + at.search)) {
        queue.unshift(at.pathname + at.search);
      }
      continue;
    }
    perShape.set(s, (perShape.get(s) || 0) + 1);
    perShape.set(at0 + '#path', (perShape.get(at0 + '#path') || 0) + 1);
    found.push(url);
    const links = await w.page.$$eval('a[href^="/"]', (as) => as.map((a) => a.getAttribute('href').split('#')[0]));
    for (const h of links) {
      if (h && !SKIP.test(h) && !seen.has(h)) {
        queue.push(h);
      }
    }
  }
  return found;
}

// The screens list is found once per run and kept, so every check looks at the same screens.
let screens = null;
async function allScreens() {
  if (screens) {
    return screens;
  }
  const s = await open(SMALL);
  try {
    screens = await findScreens(s);
  } finally {
    await s.close();
  }
  fs.mkdirSync(out, { recursive: true });
  fs.writeFileSync(path.join(out, 'screens.txt'), screens.join('\n') + '\n');
  return screens;
}

module.exports = { lost, base, out, SMALL, MID, LARGE, sleep, launch, open, signIn, visit, shape, allScreens };
