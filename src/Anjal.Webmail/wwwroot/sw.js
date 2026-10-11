/*
 * Anjal offline mail list (rc.15, item 65 b; as the owner decided on 10 Oct 2026, DES-11 D5).
 *
 * Registered only when the person has turned offline mail on, on their own
 * device, where their organisation allows it. It keeps the LIST of the newest
 * mail of the Inbox and Sent - who, subject (unless the organisation keeps
 * subjects off devices), when and the marks - and never a message or an
 * attachment.
 *
 * The list is locked. At setting up, this worker makes a key pair (RSA-OAEP):
 * the public half locks each new copy of the list as it arrives, so keeping it
 * up to date needs nothing from the person; the private half is itself locked
 * with a key derived from the person's offline code (PBKDF2, 310,000 rounds)
 * and, where the device can, from their passkey (WebAuthn PRF). Reading the
 * list offline asks for the code or the passkey; without them it cannot be
 * read, not even at the person's own unlocked computer. Five wrong tries
 * remove it. So do seven days without reaching Anjal, turning it off,
 * signing out, and a session ended from another device (the next time this
 * device reaches Anjal).
 *
 * The style sheets, the scripts and the fonts are kept too (they hold no
 * mail), so the offline pages look as Anjal does.
 */
"use strict";

var SHELL = "anjal-shell-v2";
var DB_NAME = "anjal-offline-list";
var MESSAGE = /^\/message\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(\/)?$/i;
var ASSET = /^\/(app\.css|app\.js|tokens\.css|offline\.js|fonts\/.+|logos\/.+)$/;
var ROUNDS = 310000;
var TRIES = 5;
var EXPIRE_MS = 7 * 24 * 3600 * 1000;
var UNLOCKED_MS = 10 * 60 * 1000;
var syncing = null;
var unlocked = null;   // { key: CryptoKey (RSA-OAEP private), at: time } - in memory only, opened offline
var pending = null;    // { pkcs8, at } - just after the code is set, only to add the passkey; never opens the list
var words = {};        // the person's language, from the page (every word from the word list)
function T(s) { return words[s] || s; }

/* ---------- IndexedDB ---------- */
function openDb() {
  return new Promise(function (resolve, reject) {
    var r = indexedDB.open(DB_NAME, 1);
    r.onupgradeneeded = function () { r.result.createObjectStore("kv"); };
    r.onsuccess = function () { resolve(r.result); };
    r.onerror = function () { reject(r.error); };
  });
}
function run(mode, work) {
  return openDb().then(function (d) {
    return new Promise(function (resolve, reject) {
      var t = d.transaction("kv", mode);
      var req = work(t.objectStore("kv"));
      t.oncomplete = function () { d.close(); resolve(req ? req.result : undefined); };
      t.onerror = function () { d.close(); reject(t.error); };
      t.onabort = function () { d.close(); reject(t.error); };
    });
  });
}
function get(key) { return run("readonly", function (s) { return s.get(key); }); }
function put(key, value) { return run("readwrite", function (s) { return s.put(value, key); }); }

function wipe() {
  unlocked = null;
  pending = null;
  return Promise.all([
    new Promise(function (resolve) { var r = indexedDB.deleteDatabase(DB_NAME); r.onsuccess = r.onerror = r.onblocked = function () { resolve(); }; }),
    caches.delete(SHELL)
  ]).catch(function () { /* already gone */ });
}

/* ---------- The lock ---------- */
var enc = new TextEncoder();
function codeKey(code, salt) {
  return crypto.subtle.importKey("raw", enc.encode(code), "PBKDF2", false, ["deriveKey"]).then(function (base) {
    return crypto.subtle.deriveKey({ name: "PBKDF2", salt: salt, iterations: ROUNDS, hash: "SHA-256" }, base, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
  });
}
function secretKey(secret, salt) {
  return crypto.subtle.importKey("raw", secret, "HKDF", false, ["deriveKey"]).then(function (base) {
    return crypto.subtle.deriveKey({ name: "HKDF", salt: salt, info: enc.encode("anjal-offline-list"), hash: "SHA-256" }, base, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
  });
}
function seal(key, bytes) {
  var iv = crypto.getRandomValues(new Uint8Array(12));
  return crypto.subtle.encrypt({ name: "AES-GCM", iv: iv }, key, bytes).then(function (data) { return { iv: iv, data: data }; });
}
function unseal(key, box) { return crypto.subtle.decrypt({ name: "AES-GCM", iv: box.iv }, key, box.data); }

// Set up, or change, the offline code: a new key pair; any kept list starts again.
function setCode(code) {
  if (!code || code.length < 6) { return Promise.resolve({ ok: false, error: "The offline code needs at least 6 characters." }); }
  return crypto.subtle.generateKey({ name: "RSA-OAEP", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" }, true, ["encrypt", "decrypt"]).then(function (pair) {
    return crypto.subtle.exportKey("pkcs8", pair.privateKey).then(function (pkcs8) {
      var salt = crypto.getRandomValues(new Uint8Array(16));
      return codeKey(code, salt).then(function (k) { return seal(k, pkcs8); }).then(function (byCode) {
        return crypto.subtle.exportKey("spki", pair.publicKey).then(function (spki) {
          var lock = { spki: spki, salt: salt, byCode: byCode, pkSalt: crypto.getRandomValues(new Uint8Array(32)), byPasskey: null, passkeys: [], tries: 0 };
          // Setting the code does not open the list: offline, the code (or passkey) is always asked for.
          unlocked = null;
          pending = { pkcs8: pkcs8, at: Date.now() };
          return put("lock", lock).then(function () { return put("list", null); }).then(function () { return { ok: true }; });
        });
      });
    });
  });
}

// Add the passkey as a second way to open the lock (needs the lock open, just after setting the code).
function setPasskey(secret, ids) {
  return get("lock").then(function (lock) {
    if (!lock || !pending || Date.now() - pending.at > UNLOCKED_MS) { return { ok: false, error: "Set the offline code first." }; }
    return secretKey(secret, lock.pkSalt).then(function (k) { return seal(k, pending.pkcs8); }).then(function (byPasskey) {
      lock.byPasskey = byPasskey;
      lock.passkeys = ids || [];
      pending = null;
      return put("lock", lock).then(function () { return { ok: true }; });
    });
  });
}

function openWith(lock, key, box) {
  return unseal(key, box).then(function (pkcs8) {
    return crypto.subtle.importKey("pkcs8", pkcs8, { name: "RSA-OAEP", hash: "SHA-256" }, false, ["decrypt"]).then(function (priv) {
      unlocked = { key: priv, at: Date.now() };
      lock.tries = 0;
      return put("lock", lock).then(function () { return true; });
    });
  });
}

// One try at the lock: right opens it; five wrong ones remove everything.
function tryUnlock(how, value) {
  return get("lock").then(function (lock) {
    if (!lock) { return "none"; }
    var attempt = how === "passkey"
      ? (lock.byPasskey ? secretKey(value, lock.pkSalt).then(function (k) { return openWith(lock, k, lock.byPasskey); }) : Promise.reject(new Error("no passkey")))
      : codeKey(value || "", lock.salt).then(function (k) { return openWith(lock, k, lock.byCode); });
    return attempt.then(function () { return "open"; }).catch(function () {
      lock.tries = (lock.tries || 0) + 1;
      if (lock.tries >= TRIES) { return wipe().then(function () { return "wiped"; }); }
      return put("lock", lock).then(function () { return "wrong"; });
    });
  });
}

function isOpen() { return !!(unlocked && Date.now() - unlocked.at < UNLOCKED_MS); }

/* ---------- Keeping the list ---------- */
function keepList(list) {
  return get("lock").then(function (lock) {
    if (!lock) { return; }
    return crypto.subtle.importKey("spki", lock.spki, { name: "RSA-OAEP", hash: "SHA-256" }, false, ["encrypt"]).then(function (pub) {
      return crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, true, ["encrypt"]).then(function (aes) {
        return crypto.subtle.exportKey("raw", aes).then(function (raw) {
          return crypto.subtle.encrypt({ name: "RSA-OAEP" }, pub, raw).then(function (wrapped) {
            return seal(aes, enc.encode(JSON.stringify(list))).then(function (box) {
              return put("list", { wrapped: wrapped, box: box, at: Date.now() });
            });
          });
        });
      });
    });
  });
}

function readList() {
  if (!isOpen()) { return Promise.resolve(null); }
  return get("list").then(function (rec) {
    if (!rec) { return { messages: [] }; }
    return crypto.subtle.decrypt({ name: "RSA-OAEP" }, unlocked.key, rec.wrapped).then(function (raw) {
      return crypto.subtle.importKey("raw", raw, "AES-GCM", false, ["decrypt"]);
    }).then(function (aes) { return unseal(aes, rec.box); }).then(function (plain) {
      return JSON.parse(new TextDecoder().decode(plain));
    });
  }).catch(function () { return null; });
}

function sync() {
  if (syncing) { return syncing; }
  syncing = get("lock").then(function (lock) {
    if (!lock) { return null; }
    return fetch("/offline/list", { credentials: "same-origin", cache: "no-store", redirect: "manual" });
  }).then(function (r) {
    if (!r) { return; }
    // Signed out, or the session ended from elsewhere: nothing is kept (DES-11 F11).
    if (r.type === "opaqueredirect" || r.status === 401 || r.status === 404) { return wipe(); }
    if (r.status === 204 || !r.ok) { return; }
    // Anjal answers again: the list closes, so the next time offline asks for the code once more.
    unlocked = null;
    return r.json().then(function (list) {
      return get("meta").then(function (meta) {
        if (meta && meta.session && list.session && meta.session !== list.session) {
          // Another session (another sign-in) on this device: start again from nothing.
          return wipe();
        }
        return keepList(list).then(function () {
          return put("meta", { lastContact: Date.now(), session: list.session, subjects: list.subjects });
        });
      });
    });
  }).catch(function () { /* offline: tried again next time */ })
    .then(function () { syncing = null; });
  return syncing;
}

/* ---------- Answering when the network is gone ---------- */
function page(body, script) {
  var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Offline - Anjal</title>" +
    "<link rel=\"stylesheet\" href=\"/fonts/lipi.css\"><style>" +
    "body{font-family:'LiPi Sans',system-ui,sans-serif;font-size:14px;margin:0;background:#f3f5f4;color:#1a1a1a}" +
    "header{padding:12px 18px;background:#fff6e0;border-bottom:1px solid #e8d9b0}" +
    "main{max-width:820px;margin:16px auto;background:#fff;border:1px solid #dde3e1;border-radius:14px;padding:6px 0}" +
    "form{padding:18px 22px;display:flex;flex-direction:column;gap:10px;max-width:360px}input{font:inherit;padding:8px 10px;border:1px solid #c9d2cf;border-radius:8px}" +
    "button{font:inherit;padding:8px 14px;border-radius:8px;border:0;background:#0E4B4F;color:#fff;font-weight:600;cursor:pointer}button.q{background:#fff;color:#0E4B4F;border:1px solid #c9d2cf}" +
    ".err{color:#9b1c1c;font-weight:600}ul{list-style:none;margin:0;padding:0}li a{display:grid;grid-template-columns:1fr auto;gap:2px 12px;padding:10px 18px;border-bottom:1px solid #eef1f0;color:inherit;text-decoration:none}" +
    "li a b{font-weight:600}li a.u b{font-weight:800}li a span{color:#555;grid-column:1}li a time{color:#666;font-size:12px;grid-column:2;grid-row:1}h2{font-size:13px;margin:12px 18px 4px;color:#555}p{margin:12px 22px}" +
    "</style></head><body>" + body + (script ? "<script src=\"/offline.js\"></script>" : "") + "</body></html>";
  return new Response(html, { status: 200, headers: {
    "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store",
    "Content-Security-Policy": "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'" } });
}
function esc(s) {
  return String(s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]; });
}

function lockPage(note) {
  return get("lock").then(function (lock) {
    var passkey = lock && lock.byPasskey
      ? "<button type=\"button\" class=\"q\" data-offline-passkey data-ids=\"" + esc((lock.passkeys || []).join(",")) + "\" data-salt=\"" + esc(btoa(String.fromCharCode.apply(null, new Uint8Array(lock.pkSalt)))) + "\">" + esc(T("Use my passkey")) + "</button>"
      : "";
    return page("<header>" + esc(T("You are offline. Your mail list on this device is locked.")) + "</header><main>" +
      "<form method=\"post\" action=\"/offline/unlock\">" + (note ? "<span class=\"err\">" + esc(note) + "</span>" : "") +
      "<label for=\"code\">" + esc(T("Offline code")) + "</label><input id=\"code\" name=\"code\" type=\"password\" autocomplete=\"current-password\" required autofocus>" +
      "<button type=\"submit\">" + esc(T("Open the list")) + "</button>" + passkey + "</form>" +
      "<p>" + esc(T("Five wrong tries remove the list from this device.")) + "</p></main>", !!passkey);
  });
}

function listPage() {
  return Promise.all([readList(), get("meta")]).then(function (got) {
    var list = got[0] || { messages: [] };
    var meta = got[1] || {};
    function rows(folder) {
      return list.messages.filter(function (m) { return m.f === folder; }).map(function (m) {
        var when = new Date(m.t);
        return "<li><a class=\"" + (m.u ? "u" : "") + "\" href=\"/message/" + esc(m.id) + "\"><b>" + esc(m.w) + (m.a ? " &#128206;" : "") + "</b>" +
          "<time>" + esc(when.toLocaleString()) + "</time>" + (m.s ? "<span>" + esc(m.s) + "</span>" : "") + "</a></li>";
      }).join("");
    }
    var since = meta.lastContact ? new Date(meta.lastContact).toLocaleString() : "";
    var nothing = "<li><a>" + esc(T("Nothing kept.")) + "</a></li>";
    return page("<header>" + esc(T("You are offline. This is the list kept on this device at {time}. Mail opens when Anjal can be reached again.").replace("{time}", since)) + "</header><main>" +
      "<h2>" + esc(T("Inbox")) + "</h2><ul>" + (rows("INBOX") || nothing) + "</ul><h2>" + esc(T("Sent")) + "</h2><ul>" + (rows("Sent") || nothing) + "</ul></main>", false);
  });
}

function offlineAnswer(url) {
  return Promise.all([get("lock"), get("meta"), get("words")]).then(function (got) {
    if (got[2]) { words = got[2]; }
    var lock = got[0], meta = got[1];
    if (!lock) {
      return page("<header>" + esc(T("You are offline.")) + "</header><main><p>" + esc(T("No mail list is kept on this device. Anjal opens when it can be reached again.")) + "</p></main>", false);
    }
    if (meta && meta.lastContact && Date.now() - meta.lastContact > EXPIRE_MS) {
      return wipe().then(function () {
        return page("<header>" + esc(T("You are offline.")) + "</header><main><p>" + esc(T("The mail list on this device was removed: it had not reached Anjal for seven days. It is kept again once Anjal can be reached.")) + "</p></main>", false);
      });
    }
    if (!isOpen()) { return lockPage(""); }
    if (MESSAGE.test(url.pathname) || url.searchParams.get("open")) {
      return page("<header>" + esc(T("You are offline.")) + "</header><main><p>" + esc(T("This mail opens when Anjal can be reached again. Only the list is kept on this device.")) + "</p><p><a href=\"/folder/INBOX\">" + esc(T("Back to the list")) + "</a></p></main>", false);
    }
    return listPage();
  });
}

function unlockAnswer(req) {
  return get("words").then(function (w) { if (w) { words = w; } return req.formData(); }).then(function (form) {
    return tryUnlock("code", String(form.get("code") || ""));
  }).then(function (result) {
    if (result === "open") { return Response.redirect("/folder/INBOX", 303); }
    if (result === "wiped") { return page("<header>" + esc(T("You are offline.")) + "</header><main><p>" + esc(T("The mail list was removed from this device after five wrong tries. It is kept again, under a new offline code, once Anjal can be reached.")) + "</p></main>", false); }
    return lockPage(result === "none" ? "" : T("That code is not right."));
  });
}

/* ---------- The worker's life ---------- */
self.addEventListener("install", function () { self.skipWaiting(); });
self.addEventListener("activate", function (e) {
  e.waitUntil(Promise.all([
    self.clients.claim(),
    // The full-mail copies of the first version are removed: only the list is kept now.
    new Promise(function (resolve) { var r = indexedDB.deleteDatabase("anjal-offline"); r.onsuccess = r.onerror = r.onblocked = function () { resolve(); }; }),
    caches.delete("anjal-shell-v1")
  ]));
});

self.addEventListener("message", function (e) {
  var data = e.data || {};
  var reply = function (v) { if (e.ports && e.ports[0]) { e.ports[0].postMessage(v); } };
  if (data.words && typeof data.words === "object") { words = data.words; e.waitUntil(put("words", data.words).catch(function () { /* kept next time */ })); }
  if (data.kind === "sync") { e.waitUntil(sync()); }
  if (data.kind === "wipe") { e.waitUntil(wipe().then(function () { reply({ ok: true }); })); }
  if (data.kind === "status") {
    e.waitUntil(get("lock").then(function (lock) {
      reply({ set: !!lock, passkey: !!(lock && lock.byPasskey), salt: lock ? btoa(String.fromCharCode.apply(null, new Uint8Array(lock.pkSalt))) : "" });
    }).catch(function () { reply({ set: false, passkey: false }); }));
  }
  if (data.kind === "setcode") { e.waitUntil(setCode(data.code).then(function (r) { reply(r); return r.ok ? sync() : null; })); }
  if (data.kind === "setpasskey") { e.waitUntil(setPasskey(data.secret, data.ids).then(reply)); }
  if (data.kind === "passkey") {
    e.waitUntil(tryUnlock("passkey", data.secret).then(function (r) { reply({ result: r }); }));
  }
});

self.addEventListener("fetch", function (e) {
  var req = e.request;
  var url = new URL(req.url);
  if (url.origin !== self.location.origin) { return; }
  if (req.method === "POST" && url.pathname === "/offline/unlock") {
    e.respondWith(unlockAnswer(req));
    return;
  }
  if (req.method !== "GET") { return; }
  if (req.mode === "navigate") {
    e.respondWith(fetch(req).then(function (r) {
      // Back online and sent to sign in: the session ended - nothing stays on this device (DES-11 F11).
      if (r.redirected && new URL(r.url).pathname.indexOf("/sign-in") === 0) { e.waitUntil(wipe()); }
      return r;
    }).catch(function () { return offlineAnswer(url); }));
    return;
  }
  if (ASSET.test(url.pathname)) {
    // Kept so the offline pages look right: the network first, the kept copy when it is gone.
    e.respondWith(fetch(req).then(function (r) {
      if (r.ok) {
        var copy = r.clone();
        caches.open(SHELL).then(function (c) { c.put(req, copy); });
      }
      return r;
    }).catch(function () {
      return caches.match(req).then(function (hit) { return hit || Response.error(); });
    }));
  }
});
