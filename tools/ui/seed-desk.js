// rc.27 (DES-17 §2): the desk screens are checked with data - a shared mailbox "Reception" that the
// person checked manages, two mails in it, one taken by them with a note - so the Desk block, the tabs,
// the notes box and the desk settings are crawled, not an empty desk. Run by setup.sh in two parts:
//   node seed-desk.js make    the desk and its member (before its mail is sent)
//   node seed-desk.js work    take the first mail and write a note (after)
'use strict';
const L = require('./lib.js');

async function submit(p, selector, wait) {
  await Promise.all([p.waitForURL(wait, { timeout: 30000 }), p.click(selector)]);
  await p.waitForLoadState('load');
}

// Post a form of the page and wait for the server's answer (the form is submitted directly: the
// envelope's buttons may be scrolled out of view on a laptop screen).
async function posted(p, path, selector) {
  await Promise.all([
    p.waitForResponse((r) => r.url().endsWith(path) && r.request().method() === 'POST', { timeout: 30000 }),
    p.$eval(selector, (b) => b.form.requestSubmit(b)),
  ]);
  await p.waitForLoadState('load');
}

(async () => {
  const part = process.argv[2];
  const w = await L.open(L.SMALL);
  const p = w.page;
  try {
    if (part === 'make') {
      await L.visit(w, '/org/shared?new=1', 400);
      await p.fill('#slocal', 'reception');
      await p.fill('#sname', 'Reception');
      await submit(p, 'form[action="/org/shared/new"] button[type=submit]', /[?&]sel=/);
      // The person checked (the status bar names their mailbox), not whoever is first in the list.
      const me = await p.$eval('[data-statusbar]', (b) => b.getAttribute('data-me'));
      const who = await p.$$eval('form.addmember select[name=person] option', (o) => o.map((x) => x.value));
      if (!who.includes(me)) { throw new Error('the person checked is not offered for the desk'); }
      await p.selectOption('form.addmember select[name=person]', me);
      await p.selectOption('form.addmember select[name=right]', 'manage');
      // Already on /org/shared: wait for the post itself, or the browser closes before it lands.
      await Promise.all([
        p.waitForResponse((r) => /\/org\/shared\/[^/]+\/member$/.test(r.url()) && r.request().method() === 'POST', { timeout: 30000 }),
        p.click('form.addmember button[type=submit]'),
      ]);
      await p.waitForLoadState('load');
      console.log('desk: reception, one member with manage rights');
      return;
    }
    if (part === 'work') {
      await L.visit(w, '/folder/INBOX', 400);
      await Promise.all([p.waitForURL(/\/folder\//, { timeout: 30000 }), p.$eval('form[action="/shared/open"]', (f) => f.requestSubmit())]);
      await L.visit(w, '/folder/INBOX', 600);
      const open = await p.$$eval('#maillist a[href*="open="]', (a) => a.map((x) => x.getAttribute('href')));
      if (open.length < 2) { throw new Error(`expected 2 mails in the desk, found ${open.length}`); }
      await L.visit(w, open[0], 600);
      await posted(p, '/desk/act', 'form[action="/desk/act"]:has(input[name=action][value=take]) button[type=submit]');
      await p.fill('form.dn-form textarea[name=text]', 'Called back on Tuesday; waiting for the reports.');
      await posted(p, '/desk/act', 'form.dn-form button[type=submit]');
      await Promise.all([
        p.waitForResponse((r) => r.url().endsWith('/shared/close') && r.request().method() === 'POST', { timeout: 30000 }),
        p.$eval('form[action="/shared/close"]', (f) => f.requestSubmit()),
      ]);
      console.log('desk: 2 mails, the first taken with a note');
      return;
    }
    throw new Error('say make or work');
  } finally {
    await w.close();
  }
})().catch((e) => { console.error('seed-desk: ' + e.message); process.exit(1); });
