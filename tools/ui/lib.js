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
// rc.22: "Add another account" links to /sign-in?add=1, which would sign in again for ever.
const SKIP = /^\/(auth\/|sign-out|signed-out|sign-in(\?|\/|$))|\/raw\b|raw=|download|export|\/attachment\/|\.(css|js|png|svg|ico|woff2?)(\?|$)/;

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
  // rc.28 (DES-18 §13): a size may say it is a touch screen (a phone or a tablet): { touch: true, mobile: true }.
  const as = size[2] || {};
  const page = await b.newPage({ viewport: { width: size[0], height: size[1] }, hasTouch: !!as.touch, isMobile: !!as.mobile });
  page.scriptErrors = [];
  page.on('pageerror', (e) => page.scriptErrors.push(e.message));
  page.on('dialog', (d) => (d.type() === 'prompt' ? d.dismiss() : d.accept()).catch(() => {}));
  if (process.env.ANJAL_UI_CSS) {
    // styles.js: a stylesheet used instead of the one the webmail serves.
    const css = require('fs').readFileSync(process.env.ANJAL_UI_CSS, 'utf8');
    await page.route(/\/app\.css(\?|$)/, (route) => route.fulfill({ status: 200, contentType: 'text/css', body: css }));
  }
  await signIn(page);
  return page;
}

async function signIn(page) {
  await page.goto(base + '/sign-in');
  await page.fill('input[name=address]', address);
  await page.fill('input[name=password]', password);
  await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
  if (/^\/sign-in(\/|$)/.test(new URL(page.url()).pathname)) {
    throw new Error('Could not sign in as ' + address + ' at ' + base + ' - check ANJAL_UI_ADDRESS and ANJAL_UI_PASSWORD.');
  }
  if (await page.$('.welcome')) {
    await Promise.all([page.waitForNavigation(), page.click('.welcome button.btn-q')]);
  }
}

// Submit a form of the page and wait until the server has answered it and the page has loaded.
async function posted(page, path, selector) {
  await Promise.all([
    page.waitForResponse((r) => r.url().endsWith(path) && r.request().method() === 'POST', { timeout: 30000 }),
    page.$eval(selector, (f) => f.requestSubmit()),
  ]).catch(() => null);
  await page.waitForLoadState('load').catch(() => null);
}

// Go to a screen in window w and let its script settle. Returns false when the screen could not be
// opened.
async function visit(w, url, settle) {
  if (w.page.isClosed()) {
    lost.push(w.last || url);
    w.page = await newPage(w.browser, w.size);
  }
  w.last = url;
  // rc.27: "/shared/open?id=<desk>&open=<mail>" stands for opening the desk from the account menu (a
  // form post - a desk is never opened by a link) and then the mail in it.
  // The desk's own screens carry "in=desk"; any other screen is seen from the person's own mailbox, so
  // a session left in the desk is taken back first (a check may go through the list more than once).
  const desk = /^\/shared\/open\?id=([0-9a-f-]{36})(?:&open=([0-9a-f-]{36}))?$/.exec(url);
  if (desk) {
    if (w.inDesk !== desk[1]) {
      if (!(await visit(w, '/folder/INBOX', 100))) {
        return false;
      }
      const sel = `form[action="/shared/open"]:has(input[name=id][value="${desk[1]}"])`;
      if (!(await w.page.$(sel))) {
        return false;
      }
      await posted(w.page, '/shared/open', sel);
      w.inDesk = desk[1];
    }
    return visit(w, '/folder/INBOX?in=desk' + (desk[2] ? '&open=' + desk[2] : ''), settle);
  }
  if (w.inDesk && !/[?&]in=desk/.test(url)) {
    const back = w.inDesk;
    w.inDesk = null;
    if (await visit(w, '/folder/INBOX?in=desk', 100) && (await w.page.$('form[action="/shared/close"]'))) {
      await posted(w.page, '/shared/close', 'form[action="/shared/close"]');
    } else {
      w.inDesk = back;
    }
  }
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
  if (/^\/sign-in(\/|$)/.test(new URL(w.page.url()).pathname)) {
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

// rc.27 (DES-17 §2): a desk's own screens - its tabs, a mail with the Desk block and notes, its
// settings. Opening a desk changes which mailbox the session looks at, so these come last in the
// list: the first ("/shared/open?id=…", see visit) moves each check's session into the desk for the rest of it.
async function deskScreens(w) {
  if (!(await visit(w, '/folder/INBOX', 250))) {
    return [];
  }
  const desk = await w.page.$eval('form[action="/shared/open"] input[name=id]', (i) => i.value).catch(() => null);
  if (!desk) {
    return [];
  }
  await Promise.all([w.page.waitForURL(/\/folder\//, { timeout: 30000 }), w.page.$eval('form[action="/shared/open"]', (f) => f.requestSubmit())]);
  await visit(w, '/folder/INBOX', 250);
  const ids = await w.page.$$eval('#maillist a[href*="open="]', (a) => a.map((x) => new URL(x.href).searchParams.get('open')));
  await Promise.all([w.page.waitForURL(/\/folder\//, { timeout: 30000 }), w.page.$eval('form[action="/shared/close"]', (f) => f.requestSubmit())]).catch(() => null);
  if (ids.length === 0) {
    return [];
  }
  const list = ['/shared/open?id=' + desk + '&open=' + ids[0], '/folder/INBOX?in=desk', '/folder/INBOX?tab=mine&in=desk', '/folder/INBOX?tab=unassigned&in=desk', '/folder/INBOX?tab=done&in=desk'];
  ids.slice(1, 2).forEach((id) => list.push('/folder/INBOX?open=' + id + '&in=desk'));
  list.push('/desk/settings?in=desk');
  return list;
}

// A draft carrying an edited PDF, made through the editor's own Save ("New mail with it"); null if it cannot be made.
// In a desktop window: the editor is not offered on a phone.
// A PDF editor's address, read from the Edit link of a PDF shown on its own page, in a desktop window.
async function editorFromViewer(list) {
  const views = list.filter((u) => /^\/message\/[^?]+\?preview=\d+$/.test(u));
  if (views.length === 0) {
    return null;
  }
  const w = await open([1440, 900]);
  try {
    for (const v of views) {
      await visit(w, v, 1500);
      const href = await w.page.evaluate(() => {
        const a = [...document.querySelectorAll('a[href*="edit=1"]')].find((x) => x.getAttribute('href').startsWith('/message/'));
        return a ? a.getAttribute('href') : null;
      });
      if (href) {
        return href;
      }
    }
    return null;
  } finally {
    await w.close();
  }
}

// The editor's tools as its bar offers them, read in a desktop window (the editor is not offered on a phone).
async function editorTools(editor) {
  const w = await open([1440, 900]);
  try {
    await visit(w, editor, 1500);
    return await w.page.$$eval('[data-edit-tool]', (bs) => bs.map((b) => b.getAttribute('data-edit-tool')));
  } finally {
    await w.close();
  }
}

async function editedDraft(editor) {
  const w = await open([1440, 900]);
  try {
    await visit(w, editor, 1500);
    const go = await w.page.evaluate(async () => {
      const view = document.querySelector('[data-pdfedit]');
      if (!view) { return null; }
      const token = (document.querySelector('input[name=__RequestVerificationToken]') || {}).value || '';
      const base = view.getAttribute('data-pdfedit');
      const body = new FormData();
      body.set('__RequestVerificationToken', token);
      body.set('plan', JSON.stringify({ Numbers: { Format: '{n}', Where: 'bottom-centre', Start: 1, Size: 10 } }));
      body.set('join', '');
      body.set('entry', '');
      body.set('go', 'draft');
      const r = await fetch(base + '/save', { method: 'POST', body, credentials: 'same-origin' });
      const d = await r.json().catch(() => null);
      return d && d.go ? d.go : null;
    });
    return go;
  } catch (e) {
    return null;
  } finally {
    await w.close();
  }
}

// rc.30 (DES-22 §21.6): the DICOM screens - a DICOM file, its patient details, a ZIP's study, its series and a
// series' patient details. The crawl keeps two screens of each kind, and the mails with PDFs and ZIPs fill the
// "file in a mail" kind first, so these are found on purpose: the first mail carrying a DICOM file (its tag in the
// mail's file list) is opened, and each of its files followed to the study and its series. Nothing when DICOM is off.
async function dicomScreens(w) {
  if (!(await visit(w, '/folder/INBOX', 250))) {
    return [];
  }
  const ids = await w.page.$$eval('#maillist a[href*="open="]', (a) => [...new Set(a.map((x) => new URL(x.href).searchParams.get('open')))]);
  const list = [];
  for (const id of ids) {
    await visit(w, '/folder/INBOX?open=' + id, 150);
    const files = await w.page.$$eval('.attrow', (rows) => rows.filter((r) => r.querySelector('.ftag-dicom, .ftag-zip')).map((r) => r.querySelector('a.attname')).filter((a) => a).map((a) => a.getAttribute('href')));
    const hasDicom = await w.page.$('.ftag-dicom');
    if (!hasDicom || files.length === 0) {
      continue;
    }
    for (const f of [...new Set(files)]) {
      await visit(w, f, 600);
      if (await w.page.$('.dicomview')) {
        list.push(f, f + '&patient=1');
      }
      const study = await w.page.$$eval('a[href*="study=1"]', (as) => (as[0] ? as[0].getAttribute('href') : null));
      if (study) {
        // The archive's own list, where "View as a study" is offered.
        list.push(f, study);
        await visit(w, study, 900);
        const series = await w.page.$$eval('a[href*="&series="]', (as) => [...new Set(as.map((a) => a.getAttribute('href')))]);
        series.slice(0, 2).forEach((s) => list.push(s));
        if (series[0]) {
          list.push(series[0] + '&patient=1');
        }
      }
    }
    break;
  }
  return list;
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
    // Before the desk's screens: the first of those moves the session into the desk.
    screens = screens.concat((await dicomScreens(s)).filter((u) => !screens.includes(u)));
    screens = screens.concat(await deskScreens(s));
    // rc.29 part 2 (DES-22 §7): the PDF editor's other tools, each opened from its address, so every check sees
    // every panel (Sign, Redact when it is on, Pages, Stamp start hidden behind Mark up).
    // The PDF editor's own address - a mail's (/ops/orgs?...&edit=1 is the console's form, not the editor; it was
    // taken first until 17 Nov).
    let editor = screens.find((u) => /^\/message\/[^?]+\?(?:[^#]*&)?edit=1(?:&|$)/.test(u));
    // The crawl runs in a phone-sized window, where the editor is not offered (its Edit link is desktop-only), so it
    // may not reach the editor at all (17 Nov): then the editor is taken from a PDF's view in a desktop window.
    if (!editor) {
      editor = await editorFromViewer(screens);
      if (editor) {
        screens.push(editor);
      }
    }
    if (editor) {
      // The tools the editor offers, read from its own bar: Redact is held back until chuvadi-pdf 3.35.1 (D-405).
      const tools = await editorTools(editor);
      tools.filter((t) => t !== 'markup').forEach((t) => screens.push(editor + '&tool=' + t));
      // The owner's check (17 Nov 2026): a draft's file open over the compose screen, and the editor there. The
      // editor's "New mail with it" makes the draft (once per run), as a person would.
      const draft = await editedDraft(editor);
      if (draft) {
        screens.push(draft + '?view=0', draft + '?view=0&edit=1');
      }
    }
  } finally {
    await s.close();
  }
  fs.mkdirSync(out, { recursive: true });
  fs.writeFileSync(path.join(out, 'screens.txt'), screens.join('\n') + '\n');
  return screens;
}

module.exports = { lost, base, out, SMALL, MID, LARGE, sleep, launch, open, signIn, visit, shape, allScreens };
