// rc.17 (screen check, 12 Oct 2026): the Sending systems screens are checked with data - two
// applications, each with a test message - not empty tables (the first rc.17 run missed a table
// wider than its card because no application existed). Run by setup.sh once the webmail is up.
'use strict';
const L = require('./lib.js');

(async () => {
  const w = await L.open(L.SMALL);
  const p = w.page;
  try {
    for (const [name, address] of [['Hospital information system', 'his@qa.test'], ['Payroll', 'payroll@qa.test']]) {
      await L.visit(w, '/org/apps?new=1', 400);
      await p.fill('#aname', name);
      await p.fill('#asends', address);
      // The page swaps in place: wait for the new application's own address, not for a load, or the
      // next visit cancels the post (12 Oct 2026: a first setup made none).
      await Promise.all([p.waitForURL(/[?&]sel=/, { timeout: 30000 }), p.click('form[action="/org/apps/new"] button[type=submit]')]);
    }
    await L.visit(w, '/org/apps?tab=applications', 400);
    const n = await p.$$eval('form[action="/org/sending/test"] button', (b) => b.length);
    for (let j = 0; j < n; j++) {
      await L.visit(w, '/org/apps?tab=applications', 400);
      await Promise.all([
        p.waitForResponse((r) => r.url().includes('/org/sending/test') && r.request().method() === 'POST', { timeout: 30000 }),
        p.click(`form[action="/org/sending/test"] >> nth=${j} >> button`),
      ]);
      await p.waitForTimeout(800);
    }
    if (n !== 2) { throw new Error(`expected 2 applications, found ${n}`); }
    console.log(`applications: ${n}, each with a test message`);
  } finally {
    await w.close();
  }
})().catch((e) => { console.error('seed-apps: ' + e.message); process.exit(1); });
