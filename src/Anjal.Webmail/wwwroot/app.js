/* Anjal webmail enhancements.
   Every page works with this file absent. Each block below attaches to
   markup that already functions; none of them builds a control it then
   operates. No framework, no build step.

   1. Connectivity light   2. Draft autosave        3. Address suggestions
   4. Mark all read        5. Keyboard shortcuts    6. Client-side form checks */
(function () {
  "use strict";
  document.documentElement.classList.add("js");

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
  // rc.15: the script's own sentences in the person's language (JsWords.razor); English when missing.
  var WORDS = (function () {
    try { var b = document.getElementById("anjal-words"); return b ? JSON.parse(b.textContent || "{}") : {}; } catch (e) { return {}; }
  }());
  var W = function (english) { return WORDS[english] || english; };

  /* ---------- 0. Swapping pages: no full reloads (owner, 9 Oct 2026, "C") ----------
     Following a link or sending a form fetches the next page and swaps the
     frame in place, as the big webmail services do: nothing goes white, the
     live check, the sound and the connection light keep running, and the
     address, the tab's title, Back and Forward, focus and scroll all behave as
     after a real page load. Only what is new changes on screen - the rail's
     markup is the same, so in practice the middle changes. Each part of this
     script that works on the page runs again after a swap (page() below); its
     listeners on the document or the window are taken off first (scope). A
     page that is not one of the mail screens - signing in or out, a file, an
     error - is loaded the ordinary way. Without the script every link and form
     works as before. */
  var PAGE = [];
  function newScope() {
    var offs = [];
    var timers = [];
    return {
      on: function (target, type, fn, options) {
        target.addEventListener(type, fn, options);
        offs.push(function () { target.removeEventListener(type, fn, options); });
      },
      every: function (fn, ms) { var id = window.setInterval(fn, ms); timers.push(id); return id; },
      end: function () {
        offs.forEach(function (off) { off(); });
        timers.forEach(function (id) { window.clearInterval(id); });
        offs = [];
        timers = [];
      }
    };
  }
  var scope = newScope();
  function runSafely(fn) {
    try { fn(); } catch (err) { if (window.console && window.console.error) { window.console.error(err); } }
  }
  function page(fn) { PAGE.push(fn); runSafely(fn); }

  var swapper = (function () {
    var able = !!(window.fetch && window.DOMParser && window.history && window.history.pushState && window.FormData);
    var busy = null;
    var said = null;
    // The page now shown, without its #part: Back to a #panel of the same page swaps nothing.
    var shown = window.location.pathname + window.location.search;
    function frame(doc) {
      var f = (doc || document).querySelector("body > .frame");
      return f && f.querySelector(".frame-body > main#main") ? f : null;
    }
    function here() { return able && !!frame(document); }
    // Remember where the page we leave was scrolled, for Back and Forward.
    function keepScroll() {
      var main = $("#main");
      var list = $(".fview .listcol");
      var state = window.history.state || {};
      state.anjal = 1;
      state.mainScroll = main ? main.scrollTop : 0;
      state.listScroll = list ? list.scrollTop : 0;
      try { window.history.replaceState(state, "", window.location.href); } catch (err) { /* too big: never mind */ }
    }
    function announce(text) {
      if (!said) {
        said = document.createElement("div");
        said.className = "sr-only";
        said.setAttribute("aria-live", "polite");
        document.body.appendChild(said);
      }
      said.textContent = "";
      window.setTimeout(function () { said.textContent = text; }, 60);
    }
    // A file sent back by a form (an export): saved as the browser would.
    function saveFile(response) {
      var name = "download";
      var cd = response.headers.get("content-disposition") || "";
      var m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
      if (m) { try { name = decodeURIComponent(m[1]); } catch (err) { name = m[1]; } }
      return response.blob().then(function (blob) {
        var a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = name;
        document.body.appendChild(a);
        a.click();
        window.setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
      });
    }
    function apply(html, url, how) {
      var doc = new DOMParser().parseFromString(html, "text/html");
      var fresh = frame(doc);
      var old = frame(document);
      var root = document.documentElement;
      if (!fresh || !old
          || doc.documentElement.getAttribute("data-theme") !== root.getAttribute("data-theme")
          || doc.documentElement.getAttribute("lang") !== root.getAttribute("lang")) {
        return false;
      }
      var rail = $("nav.rail");
      var railTop = rail ? rail.scrollTop : 0;
      if (how.push) { keepScroll(); }
      $$("dialog[open]").forEach(function (d) { try { d.close(); } catch (err) { /* already closed */ } });
      document.dispatchEvent(new Event("anjal:leaving"));
      scope.end();
      scope = newScope();
      old.replaceWith(document.adoptNode(fresh));
      document.title = doc.title;
      var words = doc.getElementById("anjal-words");
      if (words) { try { WORDS = JSON.parse(words.textContent || "{}"); } catch (err) { /* keep the old words */ } }
      if (how.push) { window.history.pushState({ anjal: 1 }, "", url); }
      else if (how.replace) { window.history.replaceState({ anjal: 1 }, "", url); }
      shown = window.location.pathname + window.location.search;
      rail = $("nav.rail");
      if (rail) { rail.scrollTop = railTop; }
      PAGE.forEach(runSafely);
      document.dispatchEvent(new Event("anjal:swapped"));
      var main = $("#main");
      var list = $(".fview .listcol");
      var state = window.history.state || {};
      var target = window.location.hash.length > 1 ? document.getElementById(window.location.hash.slice(1)) : null;
      if (how.restore) {
        if (main) { main.scrollTop = state.mainScroll || 0; }
        if (list && typeof state.listScroll === "number") { list.scrollTop = state.listScroll; }
      } else if (target && target.scrollIntoView) {
        target.scrollIntoView();
      } else if (main && !how.keepScroll) {
        main.scrollTop = 0;
      }
      // Focus goes where a page load would leave a reader: the message opened, else the page's heading.
      var heading = $(".readcol h1") || $("#main h1") || main;
      if (heading && !how.keepFocus) {
        if (!heading.hasAttribute("tabindex")) { heading.setAttribute("tabindex", "-1"); }
        heading.focus({ preventScroll: true });
      }
      announce(document.title);
      return true;
    }
    // Go to a page: GET a link, or send a form. how: { push, replace, restore, keepScroll, keepFocus }.
    function go(url, init, how) {
      if (busy) { busy.abort(); }
      var ctl = window.AbortController ? new AbortController() : null;
      busy = ctl;
      var method = (init && init.method) || "GET";
      document.documentElement.classList.add("swapping");
      return fetch(url, {
        method: method,
        body: init && init.body,
        credentials: "same-origin",
        cache: "no-store",
        redirect: "follow",
        headers: { "X-Anjal-Swap": "1", "Accept": "text/html" },
        signal: ctl ? ctl.signal : undefined
      }).then(function (r) {
        var type = r.headers.get("content-type") || "";
        if (type.indexOf("text/html") < 0) {
          if (method === "GET") {
            if (r.body && r.body.cancel) { r.body.cancel(); }
            window.location.href = r.url;
            return;
          }
          return saveFile(r);
        }
        return r.text().then(function (html) {
          var to = method === "GET" || r.redirected ? r.url : window.location.href;
          var done = apply(html, to, { push: how.push && to !== window.location.href ? true : false, replace: !how.push || to === window.location.href, restore: how.restore, keepScroll: how.keepScroll || (method !== "GET" && to === window.location.href), keepFocus: how.keepFocus });
          if (done) { return; }
          // Not a mail screen (signing in, an error): shown as a page load would show it.
          if (method === "GET" || r.redirected) { window.location.href = r.url; return; }
          document.open();
          document.write(html);
          document.close();
        });
      }).catch(function (err) {
        if (err && err.name === "AbortError") { return; }
        // Unreachable: a GET goes the ordinary way (offline mail answers it when kept); a form stays.
        if (method === "GET") { window.location.href = url; return; }
        if (window.anjalPaint) { window.anjalPaint(false); }
        offlineNote();
      }).then(function () {
        if (busy === ctl) { busy = null; document.documentElement.classList.remove("swapping"); }
      });
    }
    function link(href) {
      if (!here()) { window.location.href = href; return; }
      go(href, null, { push: true });
    }
    // A form sent: by the swap when it can be, otherwise the ordinary way.
    // A button may send its form somewhere of its own (formaction, formmethod, formenctype).
    function attr(form, submitter, name, own) {
      return submitter && submitter.hasAttribute(own) ? submitter.getAttribute(own) : form.getAttribute(name);
    }
    function send(form, submitter) {
      if (!here() || !swappable(form, submitter)) { form.submit(); return; }
      var method = (attr(form, submitter, "method", "formmethod") || "get").toUpperCase();
      var action = attr(form, submitter, "action", "formaction") || window.location.href;
      var enctype = attr(form, submitter, "enctype", "formenctype") || "application/x-www-form-urlencoded";
      var data;
      try { data = submitter ? new FormData(form, submitter) : new FormData(form); } catch (err) { data = new FormData(form); if (submitter && submitter.name) { data.append(submitter.name, submitter.value); } }
      if (method === "GET") {
        var u = new URL(action, window.location.href);
        u.search = new URLSearchParams(data).toString();
        go(u.href, null, { push: true });
        return;
      }
      go(new URL(action, window.location.href).href, { method: "POST", body: enctype === "multipart/form-data" ? data : new URLSearchParams(data) }, { push: true });
    }
    function swappable(form, submitter) {
      if (form.hasAttribute("data-noswap") || attr(form, submitter, "target", "formtarget") || (attr(form, submitter, "method", "formmethod") || "").toLowerCase() === "dialog") { return false; }
      var action;
      try { action = new URL(attr(form, submitter, "action", "formaction") || window.location.href, window.location.href); } catch (err) { return false; }
      return action.origin === window.location.origin && !/^\/auth\//.test(action.pathname);
    }
    if (able) {
      document.addEventListener("submit", function (e) {
        var form = e.target;
        if (e.defaultPrevented || !form || form.tagName !== "FORM" || !here() || !swappable(form, e.submitter)) { return; }
        e.preventDefault();
        send(form, e.submitter);
      });
      window.addEventListener("popstate", function () {
        if (!here() || window.location.pathname + window.location.search === shown) { return; }
        go(window.location.href, null, { push: false, restore: true });
      });
      if (!window.history.state) { try { window.history.replaceState({ anjal: 1 }, "", window.location.href); } catch (err) { /* fine */ } }
      if ("scrollRestoration" in window.history) { window.history.scrollRestoration = "manual"; }
    }
    return { link: link, send: send, go: go, here: here };
  }());
  window.anjalSwap = swapper;

  // rc.15: the dashboards' cards stack in their columns as the boards draw them, with no
  // holes where a short card sits beside a tall one. Each card spans as many fine rows as
  // it is tall, in 1px rows (app.css .dgrid.masonry); without the script the plain grid remains.
  page(function masonry() {
    // rc.15 (owner, 7 Oct: "all boxes aligned"): only a grid that asks for it stacks; the dashboards use aligned rows.
    var grids = $$(".dgrid[data-masonry]");
    if (!grids.length) { return; }
    function lay() {
      grids.forEach(function (g) {
        var cards = $$(":scope > .dcard", g);
        var columns = window.getComputedStyle(g).gridTemplateColumns.split(" ").length;
        if (columns < 2) {
          g.classList.remove("masonry");
          cards.forEach(function (c) { c.style.gridRowEnd = ""; });
          return;
        }
        // Rows of 1px with no gap of their own: each card spans its height plus the 14px gap.
        var gap = parseFloat(window.getComputedStyle(g).columnGap) || 14;
        g.classList.add("masonry");
        cards.forEach(function (c) {
          var h = c.getBoundingClientRect().height;
          c.style.gridRowEnd = h > 0 ? "span " + Math.ceil(h + gap) : "";
        });
      });
    }
    lay();
    scope.on(window, "resize", lay);
    scope.on(window, "load", lay);
    if (document.fonts && document.fonts.ready) { document.fonts.ready.then(lay); }
  });

  // rc.15 (owner, 7 Oct: "when I click the next mail I get Site can't be reached"): going to
  // another page first makes sure Anjal answers. If it does not, the page stays as it is,
  // the light turns to Offline and a note says why - nothing goes blank (item 65a). With
  // offline mail on, the service worker answers instead, from the copies it keeps.
  function offlineNote() {
    var n = $(".offnote");
    if (!n) {
      n = document.createElement("div");
      n.className = "offnote";
      n.setAttribute("role", "status");
      document.body.appendChild(n);
    }
    n.textContent = W("You are offline. This opens when Anjal can be reached again; what is on screen stays readable.");
    n.classList.add("on");
    window.clearTimeout(n._t);
    n._t = window.setTimeout(function () { n.classList.remove("on"); }, 5000);
  }
  function reachable() {
    var ctl = window.AbortController ? new AbortController() : null;
    var t = ctl ? window.setTimeout(function () { ctl.abort(); }, 3000) : 0;
    return fetch("/api/ping", { method: "GET", cache: "no-store", signal: ctl ? ctl.signal : undefined })
      .then(function (r) { window.clearTimeout(t); return r.ok; })
      .catch(function () { window.clearTimeout(t); return false; });
  }
  function keptHere() {
    var host = document.querySelector("[data-offline-mail]");
    return !!(host && host.getAttribute("data-offline-mail") === "on" && navigator.serviceWorker && navigator.serviceWorker.controller);
  }
  // rc.15 (item 18): leaving a new sender's mail first asks whether to save them. The answer
  // (save, not now, never) is sent with where the person was going, and they go on there.
  function askNewSender(href) {
    var d = $("dialog[data-newsender]");
    if (!d || d.hasAttribute("data-asked") || typeof d.showModal !== "function") { return false; }
    var id = d.getAttribute("data-message") || "";
    if (id && href.indexOf(id) >= 0) { return false; }
    var back = $("[data-ns-back]", d);
    try {
      var u = new URL(href, window.location.href);
      if (u.origin !== window.location.origin) { return false; }
      if (back) { back.value = u.pathname + u.search; }
    } catch (x) { return false; }
    d.setAttribute("data-asked", "1");
    d.showModal();
    var first = d.querySelector("input[name='firstName']");
    if (first) { first.focus(); }
    return true;
  }
  window.anjalGo = function (href) {
    if (askNewSender(href)) { return; }
    reachable().then(function (ok) {
      if (ok) { swapper.link(href); return; }
      if (window.anjalPaint) { window.anjalPaint(false); }
      // Offline mail on: the worker answers from what it keeps (or lists what it keeps).
      if (keptHere()) { window.location.href = href; return; }
      offlineNote();
    });
  };
  document.addEventListener("click", function (e) {
    if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) { return; }
    var a = e.target.closest ? e.target.closest("a[href]") : null;
    if (!a || a.target || a.hasAttribute("download")) { return; }
    // A message in the list: the double-click handler below decides (and checks the same way).
    var row = a.matches(".mlist a[data-open]") ? a.closest(".mrow") : null;
    if (row && row.getAttribute("data-message") && !/^\/draft\//.test(a.getAttribute("href") || "")) { return; }
    var raw = a.getAttribute("href") || "";
    if (raw.charAt(0) === "#" || /^(mailto|tel|javascript):/i.test(raw)) { return; }
    var url;
    try { url = new URL(a.href, window.location.href); } catch (x) { return; }
    if (url.origin !== window.location.origin) { return; }
    if (url.pathname === window.location.pathname && url.search === window.location.search && url.hash) { return; }
    e.preventDefault();
    window.anjalGo(url.href);
  });

  // rc.15 (item 5): every text button carries a short tip - what it does - in the person's language.
  page(function buttonTips() {
    var src = document.getElementById("anjal-tips");
    if (!src) { return; }
    var map;
    try { map = JSON.parse(src.textContent || "{}"); } catch (x) { return; }
    $$(".btn, .eb, .pbtn, .linkbtn, .ctempl, .eback").forEach(function (b) {
      if (b.hasAttribute("title") || b.hasAttribute("data-tip") || b.closest(".pmenu-body, .emenu, dialog, .modal")) { return; }
      var c = b.cloneNode(true); $$("[aria-hidden='true']", c).forEach(function (h) { h.parentNode.removeChild(h); }); var words = (c.textContent || "").replace(/\s+/g, " ").trim();
      // A count shown on the button ("Reply all (4)") is not part of its words.
      if (!map[words]) { words = words.replace(/\s*\(\d+\)$/, ""); }
      if (map[words]) { b.setAttribute("data-tip", map[words]); if (map[words].length > 28) { b.classList.add("tiplong"); } }
    });
  });

  // Owner, 8 Oct 2026: a time as the person reads every other one - in their own zone, on the
  // 24-hour (14:30) or 12-hour (2:30 pm) clock their settings or their organisation chose.
  window.anjalTime = function (d) {
    var bar = document.querySelector("[data-statusbar]");
    var zone = bar ? bar.getAttribute("data-tz") || undefined : undefined;
    var twelve = bar && bar.getAttribute("data-hours") === "12";
    try {
      var parts = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", hour12: false, timeZone: zone }).formatToParts(d);
      var h = 0, m = "00";
      parts.forEach(function (p) { if (p.type === "hour") { h = parseInt(p.value, 10) % 24; } if (p.type === "minute") { m = p.value; } });
      if (!twelve) { return ("0" + h).slice(-2) + ":" + m; }
      return ((h % 12) || 12) + ":" + m + " " + (h < 12 ? W("am") : W("pm"));
    } catch (e) { return ""; }
  };

  // rc.15 (owner, 8 Oct): a tip floats over the page - one element, placed against the window - so the
  // box a button sits in never cuts it off, and it stays on screen at the edges. It shows after the
  // pointer rests a moment, or at once for keyboard focus; it goes on a click, Escape or scrolling.
  (function floatingTips() {
    var tip = document.createElement("div");
    tip.className = "tipfloat";
    tip.setAttribute("role", "tooltip");
    tip.hidden = true;
    document.body.appendChild(tip);
    var timer = null, cur = null;
    var above = ".statusbar, .pager, .letterend, .csend, .cfoot, .cdock-foot, .ctools, .modal-actions";
    function show(el) {
      tip.textContent = el.getAttribute("data-tip") || "";
      tip.classList.toggle("long", tip.textContent.length > 28);
      tip.hidden = false;
      var r = el.getBoundingClientRect(), t = tip.getBoundingClientRect();
      var vw = document.documentElement.clientWidth, vh = window.innerHeight;
      var up = !!el.closest(above);
      var top = up ? r.top - t.height - 6 : r.bottom + 6;
      if (!up && top + t.height > vh - 4) { top = r.top - t.height - 6; }
      if (up && top < 4) { top = r.bottom + 6; }
      var left = Math.max(8, Math.min(r.left + (r.width / 2) - (t.width / 2), vw - t.width - 8));
      tip.style.left = left + "px";
      tip.style.top = top + "px";
      tip.classList.add("on");
    }
    function hide() {
      if (timer) { window.clearTimeout(timer); timer = null; }
      cur = null;
      tip.classList.remove("on");
      tip.hidden = true;
    }
    document.addEventListener("pointerover", function (e) {
      var el = e.target.closest ? e.target.closest("[data-tip]") : null;
      if (el === cur) { return; }
      hide();
      if (!el) { return; }
      cur = el;
      timer = window.setTimeout(function () { timer = null; if (cur === el && document.contains(el)) { show(el); } }, 350);
    });
    document.addEventListener("focusin", function (e) {
      var el = e.target.closest ? e.target.closest("[data-tip]") : null;
      hide();
      if (el && el.matches(":focus-visible")) { cur = el; show(el); }
    });
    document.addEventListener("focusout", hide);
    document.addEventListener("pointerdown", hide);
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") { hide(); } });
    window.addEventListener("scroll", hide, true);
  }());

  // rc.15 (owner, 7 Oct): the rail's fold handle sits exactly on its dividing line, whatever
  // width the rail has at this screen size.
  page(function railHandle() {
    var rail = $("nav.rail");
    if (!rail) { return; }
    function place() { rail.style.setProperty("--railw", rail.getBoundingClientRect().width + "px"); }
    place();
    scope.on(window, "resize", place);
    scope.on(window, "load", place);
  });

  // rc.15 (owner, 7 Oct): one click opens a message beside the list; a double click opens it
  // on its own, full view. The single click waits a moment so the second can be told apart.
  (function doubleClickOpens() {
    var timer = null;
    document.addEventListener("click", function (e) {
      var link = e.target.closest ? e.target.closest(".mlist a[data-open]") : null;
      if (!link || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) { return; }
      var row = link.closest(".mrow");
      var id = row ? row.getAttribute("data-message") : null;
      if (!id || /^\/draft\//.test(link.getAttribute("href") || "")) { return; }
      e.preventDefault();
      if (timer) { return; }
      var href = link.href;
      timer = window.setTimeout(function () { timer = null; window.anjalGo(href); }, 240);
    });
    document.addEventListener("dblclick", function (e) {
      var link = e.target.closest ? e.target.closest(".mlist a[data-open]") : null;
      var row = link ? link.closest(".mrow") : null;
      var id = row ? row.getAttribute("data-message") : null;
      if (!id || /^\/draft\//.test(link.getAttribute("href") || "")) { return; }
      e.preventDefault();
      if (timer) { window.clearTimeout(timer); timer = null; }
      window.anjalGo("/message/" + id + "?back=" + encodeURIComponent(window.location.pathname + window.location.search));
    });
  }());

  // rc.15 (owner, 7 Oct): a message open on its own (focus, or full view) has a way back
  // in the envelope; Escape takes it too, unless you are typing or a menu is open.
  page(function escapeGoesBack() {
    var back = $("a[data-escback]");
    if (!back) { return; }
    scope.on(document, "keydown", function (e) {
      if (e.key !== "Escape" || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) { return; }
      var t = e.target;
      if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) { return; }
      if (window.location.hash.length > 1 || $(".cdock") || $("dialog[open]")) { return; }
      window.anjalGo(back.href);
    });
  });

  // rc.15: a form marked data-reveal shows only its choice; the rest appears once that choice changes.
  page(function reveal() {
    $$("form[data-reveal]").forEach(function (f) {
      f.classList.add("quiet");
      f.addEventListener("change", function () { f.classList.remove("quiet"); });
    });
  });

  // rc.15: an icon that is only an icon says what it does as soon as the pointer rests on it
  // (a styled tip, app.css "Tooltips"). The words stay as its accessible name.
  page(function tips() {
    $$(".iconbtn[title], .eb.eicon[title], .rowact[title], .cdock-btn[title], .rte-btn[title]").forEach(function (b) {
      var t = b.getAttribute("title");
      if (!t) { return; }
      if (!b.getAttribute("aria-label")) { b.setAttribute("aria-label", t); }
      b.setAttribute("data-tip", t);
      b.removeAttribute("title");
    });
  });

  /* ---------- 1. Connectivity light ----------
     The element is rendered empty by the server and only filled in here,
     so a page without this script never shows a light that cannot tell
     the truth. */
  (function connectivity() {
    // Every light on the page: the status bar's, and the phone's folders sheet.
    if (!$$("[data-connectivity]").length) { return; }
    var timer = null;

    var wasOnline = true;
    function paint(online) {
      // rc.11 (item 65a): nothing goes blank offline; actions needing the
      // server are greyed, and the page catches up when the connection returns.
      document.documentElement.classList.toggle("offline", !online);
      if (online !== wasOnline) {
        wasOnline = online;
        document.dispatchEvent(new Event(online ? "anjal:online" : "anjal:offline"));
      }
      $$("[data-connectivity]").forEach(function (host) {
        host.className = "lite" + (online ? "" : " off");
        host.innerHTML = "";
        var dot = document.createElement("i");
        host.appendChild(dot);
        // rc.11: the server gives the words in the person's language; English otherwise.
        host.appendChild(document.createTextNode(online
          ? (host.getAttribute("data-online") || "Connected")
          : (host.getAttribute("data-offline") || "Offline")));
        host.setAttribute("title", online
          ? W("Anjal is reachable.")
          : W("Anjal is not reachable from this browser. Anything you send will fail until it returns."));
      });
    }

    function check() {
      if (document.hidden) { return; }
      fetch("/api/ping", { method: "GET", cache: "no-store" })
        .then(function (r) { paint(r.ok); })
        .catch(function () { paint(false); });
    }

    window.anjalPaint = paint;
    document.addEventListener("anjal:swapped", function () { var was = wasOnline; wasOnline = !was; paint(was); });
    paint(navigator.onLine !== false);
    check();
    timer = window.setInterval(check, 30000);
    window.addEventListener("online", check);
    window.addEventListener("offline", function () { paint(false); });
    document.addEventListener("visibilitychange", function () { if (!document.hidden) { check(); } });
    window.addEventListener("pagehide", function () { if (timer) { window.clearInterval(timer); } });
  }());

  /* ---------- 2. Draft autosave ----------
     Posts the compose form to /draft in the background 20 seconds after
     the last keystroke. Without the script the Save draft button is the
     only way a draft is kept, and the caption beside it says so. */
  page(function autosave() {
    var form = $("form[data-compose]");
    if (!form) { return; }
    var state = $("[data-draft-state]");
    var idField = form.querySelector("input[name='draftId']");
    if (!state || !idField) { return; }

    var dirty = false;
    var saving = false;
    var timer = null;

    state.textContent = W("Not saved yet");

    function stamp() { return window.anjalTime(new Date()); }

    function save() {
      if (saving || !dirty) { return; }
      var body = new FormData(form);
      body.set("autosave", "1");
      body.delete("attachments");   // files are only sent when the user presses Send or Save draft
      saving = true;
      fetch("/draft", { method: "POST", body: body, headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (data) {
          saving = false;
          if (!data || !data.id) { return; }
          idField.value = data.id;
          dirty = false;
          state.innerHTML = "";
          var dot = document.createElement("i");
          state.className = "draftstate";
          state.appendChild(dot);
          state.appendChild(document.createTextNode(W("Saved as draft \u00b7 {time}").replace("{time}", stamp())));
          // Item 46: the status bar says so too.
          var stDraft = $("[data-st-draft]");
          if (stDraft) { stDraft.textContent = W("Saved as draft \u00b7 {time}").replace("{time}", stamp()); stDraft.hidden = false; }
        })
        .catch(function () { saving = false; });
    }

    // rc.11 (item 65a): while offline, what is being written is kept in this
    // tab's session (gone when the tab closes) and saved as a draft on return.
    var keepKey = "anjal-writing:" + window.location.pathname + window.location.search;
    function keep() {
      if (!document.documentElement.classList.contains("offline")) { return; }
      // Item 65a: nothing is kept in the browser on a shared computer.
      var bar = $("[data-statusbar]");
      if (bar && bar.getAttribute("data-shared") === "1") { return; }
      var data = {};
      $$("input[type='text'], input[type='email'], input:not([type]), textarea", form).forEach(function (f) {
        if (f.name) { data[f.name] = f.value; }
      });
      try { window.sessionStorage.setItem(keepKey, JSON.stringify(data)); } catch (e) { /* storage full or off */ }
    }
    try {
      var kept = window.sessionStorage.getItem(keepKey);
      if (kept) {
        var values = JSON.parse(kept);
        Object.keys(values).forEach(function (name) {
          var f = form.elements.namedItem(name);
          if (f && "value" in f && !f.value) { f.value = values[name]; dirty = true; }
        });
      }
    } catch (e) { /* nothing kept */ }
    scope.on(document, "anjal:online", function () {
      if (dirty) { save(); }
      try { window.sessionStorage.removeItem(keepKey); } catch (e) { /* ignore */ }
    });

    form.addEventListener("input", function () {
      dirty = true;
      keep();
      if (timer) { window.clearTimeout(timer); }
      timer = window.setTimeout(save, 20000);
    });
    form.addEventListener("submit", function () {
      if (timer) { window.clearTimeout(timer); }
      dirty = false;
    });
  });

  /* ---------- 3. Recipients (rc.12: items 26-28, 52, UX-01) ----------
     To, Cc and Bcc become name cards (the board RecipCompose): a known
     address shows its name; someone outside the organisation is amber; an
     address already chosen is not suggested again; Up and Down move through
     suggestions and Tab or Enter chooses. A pasted list is reported - added,
     duplicates removed, not valid. More than five collapse to "A, B, C and N
     others". The real field is a hidden input kept in step, so the server
     reads exactly what it would without the script. Cards and their remove
     buttons come from the page's own template. */
  page(function recipients() {
    var form = $("form[data-compose]");
    var tpl = form ? $("template[data-chip-template]", form) : null;
    if (!form || !tpl || !("content" in tpl)) { return; }
    var org = (form.getAttribute("data-org-domains") || "").toLowerCase().split(",").filter(function (d) { return d; });
    var orgName = form.getAttribute("data-org-name") || "";
    var known = {};
    try {
      var given = JSON.parse(form.getAttribute("data-names") || "{}");
      Object.keys(given).forEach(function (k) { known[k.toLowerCase()] = given[k]; });
    } catch (e) { /* no names */ }
    var EMAIL = /^[^\s@<>,;"()]+@[^\s@<>,;"()]+\.[^\s@<>,;"()]{2,}$/;
    var fields = [];
    var hint = form.getAttribute("data-word-hint") || "Up and Down to move, Tab or Enter to choose. Chosen addresses are not offered again.";

    function domainOf(a) { var at = a.lastIndexOf("@"); return at < 0 ? "" : a.slice(at + 1).toLowerCase(); }
    function inside(a) { return org.length === 0 || org.indexOf(domainOf(a)) >= 0; }

    // "A <a@x>, b@y; "C, D" <c@z>" -> [{name, address}], commas inside quotes or <> kept.
    function parseList(text) {
      var out = [], cur = "", quote = false, angle = false;
      String(text || "").split("").forEach(function (ch) {
        if (ch === '"') { quote = !quote; cur += ch; return; }
        if (ch === "<") { angle = true; } else if (ch === ">") { angle = false; }
        if (!quote && !angle && (ch === "," || ch === ";" || ch === "\n")) { out.push(cur); cur = ""; return; }
        cur += ch;
      });
      out.push(cur);
      return out.map(function (s) { return s.trim(); }).filter(function (s) { return s.length; }).map(function (s) {
        var m = /^(.*)<([^<>]+)>\s*$/.exec(s);
        if (m) { return { name: m[1].replace(/"/g, "").trim(), address: m[2].trim() }; }
        return { name: "", address: s.replace(/^mailto:/i, "") };
      });
    }
    function chosen() {
      var all = [];
      fields.forEach(function (f) { f.chips.forEach(function (c) { all.push(c.address.toLowerCase()); }); });
      return all;
    }
    function attachments() {
      var n = $$("input[name='carry']:checked", form).length;
      var files = form.querySelector("input[type='file'][name='attachments']");
      return n + (files && files.files ? files.files.length : 0);
    }

    function update() {
      var everyone = [], outside = [];
      fields.forEach(function (f) {
        f.chips.forEach(function (c) {
          if (everyone.indexOf(c.address.toLowerCase()) < 0) {
            everyone.push(c.address.toLowerCase());
            if (!inside(c.address)) { outside.push(c); }
          }
        });
      });
      var warn = $("[data-outside-warning]", form);
      if (warn) {
        if (outside.length && org.length) {
          var lead = outside.length === 1
            ? (warn.getAttribute("data-word-one") || "{name} is outside {org}").replace("{name}", outside[0].name || outside[0].address)
            : (warn.getAttribute("data-word-many") || "{n} of {total} recipients are outside {org}").replace("{n}", String(outside.length)).replace("{total}", String(everyone.length));
          var holder = $("[data-warn-text]", warn) || warn;
          holder.textContent = "";
          var b = document.createElement("b");
          b.textContent = lead.replace("{org}", orgName);
          holder.appendChild(b);
          holder.appendChild(document.createTextNode(attachments() > 0 ? (warn.getAttribute("data-word-attach") || ", and there is an attachment.") : "."));
          warn.hidden = false;
        } else {
          warn.hidden = true;
        }
      }
      var count = $("[data-rcount]", form);
      if (count) { count.textContent = everyone.length > 1 ? String(everyone.length) : ""; }
      var send = $("button[data-send]", form);
      if (send && send.hasAttribute("data-word-sendn")) {
        send.textContent = everyone.length > 5
          ? send.getAttribute("data-word-sendn").replace("{n}", String(everyone.length))
          : (send.getAttribute("data-word-send") || "Send");
      }
      form.dispatchEvent(new CustomEvent("anjal:recipients", { detail: { count: everyone.length } }));
    }

    function report(pasted, added, dupes, bad) {
      var box = $("[data-paste-report]", form);
      if (!box) { return; }
      if (pasted < 2 && !bad.length) { box.hidden = true; return; }
      box.textContent = "";
      var head = document.createElement("b");
      head.textContent = (box.getAttribute("data-word-pasted") || "You pasted {n} addresses").replace("{n}", String(pasted));
      box.appendChild(head);
      var ul = document.createElement("ul");
      function icon(name) { var tp = $("template[data-icon-" + name + "]", form); return tp ? tp.content.firstElementChild.cloneNode(true) : document.createTextNode(""); }
      function line(cls, text) {
        var li = document.createElement("li");
        li.className = cls;
        li.appendChild(icon(cls === "ok" ? "ok" : "no"));
        li.appendChild(document.createTextNode(" " + text));
        ul.appendChild(li);
        return li;
      }
      line("ok", (box.getAttribute("data-word-added") || "{n} added").replace("{n}", String(added)));
      if (dupes) { line("dupe", (box.getAttribute("data-word-dupes") || "{n} duplicate removed").replace("{n}", String(dupes))); }
      if (bad.length) { line("bad", (box.getAttribute("data-word-bad") || "{n} not valid:").replace("{n}", String(bad.length)) + " " + bad.join(", ")); }
      box.appendChild(ul);
      box.hidden = false;
    }

    $$("[data-recips]", form).forEach(function (block) {
      var box = $("[data-rbox]", block);
      var input = box ? $("input.rinput", box) : null;
      if (!box || !input || !input.name) { return; }
      var hidden = document.createElement("input");
      hidden.type = "hidden";
      hidden.name = input.name;
      input.removeAttribute("name");
      input.removeAttribute("required");
      box.appendChild(hidden);
      var sum = $("[data-rsum]", block);
      var field = { block: block, box: box, input: input, hidden: hidden, chips: [] };
      fields.push(field);

      function sync() {
        hidden.value = field.chips.map(function (c) {
          return c.name && c.name.toLowerCase() !== c.address.toLowerCase() ? '"' + c.name.replace(/"/g, "") + '" <' + c.address + ">" : c.address;
        }).concat(input.value.trim() ? [input.value.trim()] : []).join(", ");
        update();
        collapse(false);
      }
      function remove(chip) {
        field.chips = field.chips.filter(function (c) { return c !== chip; });
        chip.el.parentNode.removeChild(chip.el);
        sync();
        input.focus();
      }
      function paintName(chip) {
        var n = $("[data-chip-name]", chip.el), a = $("[data-chip-addr]", chip.el);
        if (n) { n.textContent = chip.name || chip.address; }
        if (a) { a.textContent = chip.name ? chip.address : ""; a.hidden = !chip.name; }
        chip.el.setAttribute("title", chip.address);
      }
      function lookup(chip) {
        fetch("/api/contacts?q=" + encodeURIComponent(chip.address), { headers: { "X-Requested-With": "anjal" } })
          .then(function (r) { return r.ok ? r.json() : []; })
          .then(function (list) {
            var hit = (list || []).filter(function (c) { return c.address && c.address.toLowerCase() === chip.address.toLowerCase() && c.name; })[0];
            if (hit && !chip.name) { chip.name = hit.name; known[chip.address.toLowerCase()] = hit.name; paintName(chip); sync(); }
          })
          .catch(function () { /* the address alone is fine */ });
      }
      // Returns "added", "dupe" or "bad".
      function add(address, name) {
        var a = String(address || "").trim();
        if (!a) { return "bad"; }
        if (!EMAIL.test(a)) { return "bad"; }
        if (chosen().indexOf(a.toLowerCase()) >= 0) { return "dupe"; }
        var el = tpl.content.firstElementChild.cloneNode(true);
        var chip = { address: a, name: name || known[a.toLowerCase()] || "", el: el };
        if (!inside(a)) { el.classList.add("out"); }
        paintName(chip);
        var x = $("[data-chip-remove]", el);
        if (x) {
          x.setAttribute("aria-label", (x.getAttribute("aria-label") || "Remove") + " " + (chip.name || a));
          x.addEventListener("click", function () { remove(chip); });
        }
        box.insertBefore(el, input);
        field.chips.push(chip);
        if (!chip.name) { lookup(chip); }
        return "added";
      }
      function commit(text, isPaste) {
        var items = parseList(text);
        if (!items.length) { return; }
        var added = 0, dupes = 0, bad = [];
        items.forEach(function (it) {
          var r = add(it.address, it.name);
          if (r === "added") { added++; } else if (r === "dupe") { dupes++; } else { bad.push(it.address); }
        });
        input.value = bad.join(", ");
        if (isPaste || items.length > 1 || bad.length) { report(items.length, added, dupes, bad); }
        sync();
      }

      // What the server put in the field becomes cards.
      var start = input.value;
      input.value = "";
      parseList(start).forEach(function (it) {
        if (add(it.address, it.name) === "bad") { input.value = (input.value ? input.value + ", " : "") + it.address; }
      });
      sync();

      // Suggestions: contacts and colleagues first; groups as one entry that adds its people.
      var list = document.createElement("ul");
      list.className = "rsuggest";
      list.setAttribute("role", "listbox");
      list.id = "rs-" + (input.id || Math.random().toString(36).slice(2));
      list.hidden = true;
      block.appendChild(list);
      input.setAttribute("role", "combobox");
      input.setAttribute("aria-autocomplete", "list");
      input.setAttribute("aria-controls", list.id);
      input.setAttribute("aria-expanded", "false");
      var items = [], active = -1, timer = null, seq = 0;
      function close() { list.hidden = true; list.textContent = ""; items = []; active = -1; input.setAttribute("aria-expanded", "false"); }
      function highlight() {
        $$("li[role='option']", list).forEach(function (li, i) {
          li.setAttribute("aria-selected", i === active ? "true" : "false");
          if (i === active) { input.setAttribute("aria-activedescendant", li.id); li.scrollIntoView({ block: "nearest" }); }
        });
      }
      function choose(i) {
        var it = items[i];
        if (!it) { return; }
        if (it.members) {
          var added = 0, dupes = 0;
          it.members.forEach(function (m) { var r = add(m, ""); if (r === "added") { added++; } else if (r === "dupe") { dupes++; } });
          report(it.members.length, added, dupes, []);
        } else {
          add(it.address, it.name);
        }
        input.value = "";
        sync();
        close();
        input.focus();
      }
      function paint(data) {
        list.textContent = "";
        var taken = chosen();
        items = data.filter(function (c) { return c.members || taken.indexOf(String(c.address).toLowerCase()) < 0; }).slice(0, 8);
        active = items.length ? 0 : -1;
        if (!items.length) { close(); return; }
        items.forEach(function (c, i) {
          var li = document.createElement("li");
          li.setAttribute("role", "option");
          li.id = list.id + "-" + i;
          var b = document.createElement("b");
          b.textContent = c.members ? c.name : (c.name || c.address);
          li.appendChild(b);
          var s = document.createElement("span");
          s.textContent = c.members ? c.members.length + " " + (form.getAttribute("data-word-people") || "people") : (c.name ? c.address : "");
          li.appendChild(s);
          if (c.members) {
            li.classList.add("grp");
            var gi = $("template[data-icon-group]", form);
            if (gi) { b.insertBefore(gi.content.firstElementChild.cloneNode(true), b.firstChild); }
          }
          if (!inside(c.address || "") && !c.members) { li.classList.add("out"); }
          li.addEventListener("mousedown", function (e) { e.preventDefault(); choose(i); });
          list.appendChild(li);
        });
        var h = document.createElement("li");
        h.className = "rhint";
        h.setAttribute("aria-hidden", "true");
        h.textContent = hint;
        list.appendChild(h);
        list.hidden = false;
        input.setAttribute("aria-expanded", "true");
        highlight();
      }
      function query() {
        var q = input.value.trim();
        if (q.length < 1) { close(); return; }
        var mine = ++seq;
        var headers = { headers: { "X-Requested-With": "anjal" } };
        Promise.all([
          fetch("/api/contacts?q=" + encodeURIComponent(q), headers).then(function (r) { return r.ok ? r.json() : []; }).catch(function () { return []; }),
          fetch("/api/groups?q=" + encodeURIComponent(q), headers).then(function (r) { return r.ok ? r.json() : []; }).catch(function () { return []; })
        ]).then(function (res) { if (mine === seq) { paint((res[1] || []).concat(res[0] || [])); } });
      }
      input.addEventListener("input", function () {
        if (/[,;]\s*$/.test(input.value)) { commit(input.value.replace(/[,;]\s*$/, ""), false); close(); return; }
        if (timer) { window.clearTimeout(timer); }
        timer = window.setTimeout(query, 140);
        sync();
      });
      input.addEventListener("paste", function (e) {
        var text = e.clipboardData ? e.clipboardData.getData("text") : "";
        if (/[,;\n]/.test(text)) { e.preventDefault(); commit(text, true); close(); }
      });
      input.addEventListener("keydown", function (e) {
        var open = !list.hidden && items.length;
        if (e.key === "ArrowDown" && open) { e.preventDefault(); active = Math.min(active + 1, items.length - 1); highlight(); }
        else if (e.key === "ArrowUp" && open) { e.preventDefault(); active = Math.max(active - 1, 0); highlight(); }
        else if ((e.key === "Enter" || e.key === "Tab") && open && active >= 0) { e.preventDefault(); choose(active); }
        else if ((e.key === "Enter" || e.key === "Tab") && input.value.trim()) {
          if (EMAIL.test(input.value.trim()) || e.key === "Enter") { e.preventDefault(); commit(input.value, false); }
        }
        else if (e.key === "Escape" && open) { e.preventDefault(); close(); }
        else if (e.key === "Backspace" && !input.value && field.chips.length) { remove(field.chips[field.chips.length - 1]); }
      });
      input.addEventListener("blur", function () {
        window.setTimeout(function () {
          close();
          if (input.value.trim() && EMAIL.test(input.value.trim())) { commit(input.value, false); }
          collapse(true);
        }, 150);
      });
      input.addEventListener("focus", function () { collapse(false); });
      box.addEventListener("click", function (e) { if (e.target === box) { input.focus(); } });

      // Item 52: more than five fold to "A, B, C and N others".
      function collapse(fold) {
        if (!sum) { return; }
        var many = field.chips.length > 5;
        if (!fold || !many) { box.classList.remove("collapsed"); sum.hidden = true; return; }
        var first = field.chips.slice(0, 3).map(function (c) { return c.name || c.address; }).join(", ");
        sum.textContent = "";
        var t = document.createElement("span");
        t.textContent = (form.getAttribute("data-word-others") || "{names} and {n} others").replace("{names}", first).replace("{n}", String(field.chips.length - 3));
        sum.appendChild(t);
        var show = document.createElement("a");
        show.href = "#" + input.id;
        show.className = "linkbtn";
        show.textContent = (form.getAttribute("data-word-showall") || "Show all {n}").replace("{n}", String(field.chips.length));
        show.addEventListener("click", function (e) { e.preventDefault(); collapse(false); input.focus(); });
        sum.appendChild(show);
        box.classList.add("collapsed");
        sum.hidden = false;
      }
      collapse(true);
      field.commitTyped = function () { if (input.value.trim()) { commit(input.value, false); } };
    });

    // rc.15 (owner, 7 Oct): To, Cc and Bcc are always shown - no "Show Cc" link to handle.
    // Before sending, whatever is still typed goes in as written; the server checks it.
    form.addEventListener("submit", function () { fields.forEach(function (f) { if (f.commitTyped) { f.commitTyped(); } }); }, true);
    form.addEventListener("change", function (e) { if (e.target && (e.target.name === "carry" || e.target.name === "attachments")) { update(); } });
    update();
  });

  /* ---------- 3b. Attachments: a list, remove, and drag and drop (UX-06) ----------
     Files chosen or dropped are listed with their size and a remove button
     (from the page's template). Dropping anywhere on the message adds them. */
  page(function attachmentsList() {
    var form = $("form[data-compose]");
    var input = form ? form.querySelector("input[type='file'][name='attachments']") : null;
    var listEl = form ? $("[data-filelist]", form) : null;
    var tpl = form ? $("template[data-file-template]", form) : null;
    if (!form || !input || !listEl || !tpl || typeof DataTransfer !== "function") { return; }
    // Owner, 9 Oct 2026: sizes with two decimals, as on every screen.
    function size(b) { return b < 1024 ? b + " B" : b < 1048576 ? (b / 1024).toFixed(2) + " KB" : b < 1073741824 ? (b / 1048576).toFixed(2) + " MB" : (b / 1073741824).toFixed(2) + " GB"; }
    function set(files) {
      var dt = new DataTransfer();
      files.forEach(function (f) { dt.items.add(f); });
      input.files = dt.files;
      render();
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }
    function render() {
      listEl.textContent = "";
      Array.prototype.forEach.call(input.files || [], function (f, i) {
        var li = tpl.content.firstElementChild.cloneNode(true);
        var n = $("[data-fname]", li), s = $("[data-fsize]", li), x = $("[data-fremove]", li);
        if (n) { n.textContent = f.name; }
        if (s) { s.textContent = size(f.size); }
        if (x) {
          x.setAttribute("aria-label", (x.getAttribute("aria-label") || "Remove") + " " + f.name);
          x.addEventListener("click", function () {
            var keep = Array.prototype.filter.call(input.files, function (_, j) { return j !== i; });
            set(keep);
          });
        }
        listEl.appendChild(li);
      });
    }
    input.addEventListener("change", function (e) { if (e.isTrusted) { render(); } });
    var drop = $("[data-drop]", form);
    var depth = 0;
    form.addEventListener("dragenter", function (e) {
      if (!e.dataTransfer || Array.prototype.indexOf.call(e.dataTransfer.types || [], "Files") < 0) { return; }
      depth++;
      form.classList.add("dropping");
    });
    form.addEventListener("dragleave", function () { depth = Math.max(0, depth - 1); if (!depth) { form.classList.remove("dropping"); } });
    form.addEventListener("dragover", function (e) {
      if (e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], "Files") >= 0) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; }
    });
    // rc.15 (owner, 7 Oct): Ctrl+V of a copied file or a screenshot attaches it.
    form.addEventListener("paste", function (e) {
      var cd = e.clipboardData;
      if (!cd) { return; }
      var got = [];
      Array.prototype.forEach.call(cd.items || [], function (it) {
        if (it.kind === "file") { var f = it.getAsFile(); if (f) { got.push(f); } }
      });
      if (!got.length && cd.files && cd.files.length) { got = Array.prototype.slice.call(cd.files); }
      if (!got.length) { return; }
      e.preventDefault();
      var stamp = new Date();
      function two(n) { return (n < 10 ? "0" : "") + n; }
      got = got.map(function (f, i) {
        if (f.name && f.name !== "image.png" && f.name !== "image.jpeg") { return f; }
        var ext = (f.type.split("/")[1] || "png").replace("jpeg", "jpg");
        var name = "Pasted " + stamp.getFullYear() + "-" + two(stamp.getMonth() + 1) + "-" + two(stamp.getDate()) + " " +
          two(stamp.getHours()) + "." + two(stamp.getMinutes()) + "." + two(stamp.getSeconds()) + (got.length > 1 ? " (" + (i + 1) + ")" : "") + "." + ext;
        try { return new File([f], name, { type: f.type }); } catch (x) { return f; }
      });
      set(Array.prototype.slice.call(input.files || []).concat(got));
      if (drop) { drop.classList.add("got"); window.setTimeout(function () { drop.classList.remove("got"); }, 700); }
    });
    form.addEventListener("drop", function (e) {
      if (!e.dataTransfer || !e.dataTransfer.files || !e.dataTransfer.files.length) { return; }
      e.preventDefault();
      depth = 0;
      form.classList.remove("dropping");
      set(Array.prototype.slice.call(input.files || []).concat(Array.prototype.slice.call(e.dataTransfer.files)));
      if (drop) { drop.classList.add("got"); window.setTimeout(function () { drop.classList.remove("got"); }, 700); }
    });
  });

  /* ---------- 3c. Undo send, the held bar, the docked panel (UX-02, item 48) ---------- */
  page(function sending() {
    $$("form[data-compose] input[data-undo]").forEach(function (u) { u.value = "1"; });
    var bar = $("[data-heldbar]");
    if (bar) {
      var left = parseInt(bar.getAttribute("data-seconds") || "10", 10);
      var text = $("[data-heldtext]", bar);
      var undo = $("[data-heldundo]", bar);
      var word = bar.getAttribute("data-word-sending") || "Sending in {n} seconds";
      var tick = scope.every(function () {
        left -= 1;
        if (left > 0) { if (text) { text.textContent = word.replace("{n}", String(left)); } return; }
        window.clearInterval(tick);
        if (text) { text.textContent = bar.getAttribute("data-word-sent") || "Sent"; }
        if (undo) { undo.hidden = true; }
        bar.classList.add("done");
        window.setTimeout(function () { bar.classList.add("gone"); }, 2500);
      }, 1000);
    }
    $$("[data-dock-min]").forEach(function (b) {
      b.addEventListener("click", function () {
        var dock = b.closest("form.cdock");
        if (dock) { dock.classList.toggle("min"); b.setAttribute("aria-pressed", dock.classList.contains("min") ? "true" : "false"); }
      });
    });
  });

  /* ---------- 3d. Send one each: who is missing a name, and each copy (item 64) ---------- */
  page(function sendOneEach() {
    var form = $("form[data-compose]");
    var box = form ? form.querySelector("input[data-merge]") : null;
    var info = form ? $("[data-merge-info]", form) : null;
    var mapBox = form ? $("[data-merge-map]", form) : null;
    var tpl = form ? $("template[data-merge-template]", form) : null;
    var listInput = form ? form.querySelector("input[name='mergeList']") : null;
    if (!form || !box || !info) { return; }
    // DES-11 F5: with Send one each on, Cc and Bcc step aside (and are not sent); the summary copy replaces them.
    function ccOff() {
      $$("[data-merge-off]", form).forEach(function (row) {
        row.hidden = box.checked;
        $$("input", row).forEach(function (i) { i.disabled = box.checked; });
      });
    }
    box.addEventListener("change", ccOff);
    ccOff();
    var people = [];
    var columns = [];
    var fromList = false;
    var orgDomains = (form.getAttribute("data-org-domains") || "").toLowerCase().split(",").filter(Boolean);
    // The To line as sent: the chips' hidden field (the page can hold more than one field named "to").
    function to() {
      var h = form.querySelector("[data-recips] input[type='hidden'][name='to']") || form.querySelector("input[name='to']");
      return h ? h.value : "";
    }
    function words(k, d) { return (mapBox && mapBox.getAttribute("data-word-" + k)) || d; }

    // rc.15 (item 64): a list read here in the browser - its header names the columns.
    function readCsv(text) {
      var rows = [], row = [], cell = "", q = false;
      for (var i = 0; i < text.length; i++) {
        var c = text.charAt(i);
        if (q) {
          if (c === '"') { if (text.charAt(i + 1) === '"') { cell += '"'; i++; } else { q = false; } } else { cell += c; }
        } else if (c === '"') { q = true; }
        else if (c === "," || c === ";") { row.push(cell); cell = ""; }
        else if (c === "\n" || c === "\r") {
          if (c === "\r" && text.charAt(i + 1) === "\n") { i++; }
          row.push(cell); cell = ""; if (row.join("").trim().length) { rows.push(row); } row = [];
        } else { cell += c; }
      }
      row.push(cell); if (row.join("").trim().length) { rows.push(row); }
      return rows;
    }
    function fromCsv(text) {
      var rows = readCsv(text.replace(/^﻿/, ""));
      if (rows.length < 2) { return []; }
      var head = rows[0].map(function (h) { return h.trim(); });
      var at = -1;
      head.forEach(function (h, i) { if (at < 0 && (/mail/i.test(h) || /^address$/i.test(h))) { at = i; } });
      columns = head.filter(function (h, i) { return i !== at && h.length; });
      var seen = {};
      return rows.slice(1).map(function (r) {
        var addr = (at >= 0 ? r[at] : r.filter(function (f) { return f.indexOf("@") > 0; })[0] || "").trim();
        if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(addr) || seen[addr.toLowerCase()]) { return null; }
        seen[addr.toLowerCase()] = true;
        var values = {};
        head.forEach(function (h, i) { if (i !== at && h.length) { values[h] = (r[i] || "").trim(); } });
        return { address: addr, values: values };
      }).filter(Boolean).slice(0, 500);
    }
    function blanks() {
      var subj = form.querySelector("input[name='subject']");
      var ed = form.querySelector("[data-rte-editor]");
      var body = ed ? ed.innerText : (form.querySelector("textarea[name='body']") || {}).value || "";
      var found = [];
      String((subj ? subj.value : "") + "\n" + body).replace(/\{([^{}]{1,40})\}/g, function (all, k) { if (found.indexOf(k) < 0) { found.push(k); } return all; });
      return found;
    }
    // The matching step: each blank - which field fills it, and what to write when that is empty.
    function drawMap() {
      if (!mapBox) { return; }
      var list = blanks();
      mapBox.hidden = list.length === 0;
      if (!list.length) { mapBox.textContent = ""; return; }
      var kept = {};
      $$("[data-map-row]", mapBox).forEach(function (r) {
        kept[r.getAttribute("data-map-row")] = { field: $("select", r).value, fallback: $("input[type='text']", r).value };
      });
      mapBox.textContent = "";
      var h = document.createElement("p");
      h.className = "mmap-head";
      h.textContent = words("head", "Fill each blank from");
      mapBox.appendChild(h);
      var fieldsOffered = fromList ? columns.slice() : [words("first", "First name"), words("last", "Last name"), words("org", "Organisation")];
      var keys = fromList ? columns.slice() : ["First name", "Last name", "Organisation"];
      list.forEach(function (blank) {
        var row = document.createElement("div");
        row.className = "mmap-row";
        row.setAttribute("data-map-row", blank);
        var name = document.createElement("b");
        name.textContent = "{" + blank + "}";
        var sel = document.createElement("select");
        sel.className = "railinp";
        sel.setAttribute("aria-label", "{" + blank + "}");
        var none = document.createElement("option");
        none.value = "-";
        none.textContent = words("unused", "(leave empty)");
        sel.appendChild(none);
        var guess = "";
        keys.forEach(function (k, i) {
          var o = document.createElement("option");
          o.value = k;
          o.textContent = fieldsOffered[i];
          sel.appendChild(o);
          if (!guess && k.toLowerCase() === blank.toLowerCase()) { guess = k; }
        });
        if (!guess && /^name$/i.test(blank)) { guess = keys.filter(function (k) { return /first/i.test(k); })[0] || ""; }
        var fb = document.createElement("input");
        fb.type = "text";
        fb.className = "railinp";
        fb.maxLength = 60;
        fb.placeholder = words("fallback", "When empty, write");
        fb.setAttribute("aria-label", words("fallback", "When empty, write") + " {" + blank + "}");
        var was = kept[blank];
        sel.value = was ? was.field : (guess || "-");
        fb.value = was ? was.fallback : (/^(first name|name|last name)$/i.test(blank) ? words("colleague", "colleague") : "");
        var hb = document.createElement("input"); hb.type = "hidden"; hb.name = "mapBlank"; hb.value = blank;
        var hf = document.createElement("input"); hf.type = "hidden"; hf.name = "mapField";
        var hx = document.createElement("input"); hx.type = "hidden"; hx.name = "mapFallback";
        function sync() { hf.value = sel.value === "-" ? "-none-" : sel.value; hx.value = fb.value; }
        sel.addEventListener("change", sync);
        fb.addEventListener("input", sync);
        sync();
        row.appendChild(name); row.appendChild(sel); row.appendChild(fb);
        row.appendChild(hb); row.appendChild(hf); row.appendChild(hx);
        mapBox.appendChild(row);
      });
    }
    function rule(blank) {
      var r = mapBox ? $("[data-map-row='" + blank.replace(/'/g, "\\'") + "']", mapBox) : null;
      return r ? { field: $("select", r).value, fallback: $("input[type='text']", r).value } : null;
    }
    function fill(text, p) {
      return String(text).replace(/\{([^{}]{1,40})\}/g, function (all, k) {
        var r = rule(k);
        var field = r ? (r.field === "-" ? "" : r.field) : k;
        var v = "";
        if (field && p.values) {
          Object.keys(p.values).forEach(function (key) { if (!v && key.toLowerCase() === field.toLowerCase()) { v = p.values[key]; } });
        }
        if (v) { return v; }
        if (r) { return r.fallback; }
        return /^(first name|name|last name)$/i.test(k) ? "colleague" : "";
      });
    }
    function show() {
      info.textContent = "";
      var outside = people.filter(function (p) { return orgDomains.indexOf(p.address.split("@")[1].toLowerCase()) < 0; }).length;
      if (outside && orgDomains.length) {
        var o = document.createElement("p");
        o.className = "cwarn";
        o.textContent = (info.getAttribute("data-word-outside") || "{n} of {total} are outside {org}.").replace("{n}", String(outside)).replace("{total}", String(people.length)).replace("{org}", info.getAttribute("data-org") || "");
        info.appendChild(o);
      }
      var l = document.createElement("p");
      l.className = "railnote";
      l.textContent = (info.getAttribute("data-word-limit") || "").replace("{org}", info.getAttribute("data-org") || "").replace("{limit}", info.getAttribute("data-limit") || "200").replace("{n}", String(people.length));
      info.appendChild(l);
      var send = $("button[data-send]", form);
      if (send && send.hasAttribute("data-word-sendn")) {
        send.textContent = (form.getAttribute("data-word-sendeach") || "Send {n} separate messages").replace("{n}", String(people.length));
      }
      drawMap();
    }
    function refresh() {
      form.classList.toggle("merging", box.checked);
      if (!box.checked) { info.textContent = ""; if (mapBox) { mapBox.hidden = true; } return; }
      var file = listInput && listInput.files && listInput.files[0];
      if (file && window.FileReader) {
        var reader = new FileReader();
        reader.onload = function () { fromList = true; people = fromCsv(String(reader.result || "")); show(); };
        reader.readAsText(file);
        return;
      }
      fromList = false;
      columns = [];
      fetch("/api/merge-preview?to=" + encodeURIComponent(to()), { headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (list) { people = list || []; show(); })
        .catch(function () { /* the server checks again */ });
    }
    box.addEventListener("change", refresh);
    if (listInput) { listInput.addEventListener("change", refresh); }
    form.addEventListener("anjal:recipients", function () { if (box.checked) { refresh(); } });
    var subjField = form.querySelector("input[name='subject']");
    var redraw = null;
    function later() { if (!box.checked) { return; } window.clearTimeout(redraw); redraw = window.setTimeout(drawMap, 500); }
    if (subjField) { subjField.addEventListener("input", later); }
    form.addEventListener("input", function (e) { if (e.target && e.target.closest && e.target.closest("[data-rte-editor], textarea[name='body']")) { later(); } });

    // The preview: each person's copy, one at a time ("3 of 24") - typed people or a list.
    var previewBtn = $("[data-merge-preview]", form);
    if (!previewBtn || !tpl) { return; }
    previewBtn.addEventListener("click", function () {
      if (!people.length) { refresh(); return; }
      var letter = $(".cletter", form);
      var old = $(".mergeview", form);
      if (old) { old.parentNode.removeChild(old); }
      var view = tpl.content.firstElementChild.cloneNode(true);
      letter.appendChild(view);
      var i = 0;
      function one() {
        var p = people[i];
        var subj = form.querySelector("input[name='subject']");
        var ed = form.querySelector("[data-rte-editor]");
        var body = ed ? ed.innerText : (form.querySelector("textarea[name='body']") || {}).value;
        $("[data-mv-who]", view).textContent = (i + 1) + " / " + people.length + ": " + p.address;
        $("[data-mv-subject]", view).textContent = fill(subj ? subj.value : "", p);
        $("[data-mv-body]", view).textContent = fill(body || "", p);
      }
      $("[data-mv-prev]", view).addEventListener("click", function () { i = (i - 1 + people.length) % people.length; one(); });
      $("[data-mv-next]", view).addEventListener("click", function () { i = (i + 1) % people.length; one(); });
      $("[data-mv-close]", view).addEventListener("click", function () { view.parentNode.removeChild(view); });
      one();
    });
  });

  /* ---------- 4b. Drag messages onto a folder (rc.12) ----------
     A row - or every ticked row, when the dragged one is ticked - can be
     dropped on a folder in the rail; it moves there through the folder's own
     Move path, so Undo works as for any move. */
  page(function dragToFolder() {
    var list = $("[data-list][data-folder]");
    if (!list) { return; }
    var from = list.getAttribute("data-folder");
    var dragging = null;
    function rows() { return $$("[data-message]", list); }
    function arm() {
      rows().forEach(function (row) {
        if (row.getAttribute("draggable") === "true") { return; }
        row.setAttribute("draggable", "true");
        row.addEventListener("dragstart", function (e) {
          var box = row.querySelector("input[data-rowcheck]");
          var ids = box && box.checked
            ? $$("input[data-rowcheck]:checked", list).map(function (b) { return b.value; })
            : [row.getAttribute("data-message")];
          dragging = ids;
          e.dataTransfer.effectAllowed = "move";
          e.dataTransfer.setData("text/plain", ids.length + " message" + (ids.length === 1 ? "" : "s"));
          document.documentElement.classList.add("dragging-mail");
        });
        row.addEventListener("dragend", function () {
          dragging = null;
          document.documentElement.classList.remove("dragging-mail");
          $$(".rail-item.dropok").forEach(function (r) { r.classList.remove("dropok"); });
        });
      });
    }
    arm();
    scope.on(document, "anjal:listchanged", arm);
    $$(".rail a.rail-item[href^='/folder/']").forEach(function (target) {
      var name = decodeURIComponent(target.getAttribute("href").slice("/folder/".length).split("?")[0]);
      if (name === from || name === "Drafts" || name === "Scheduled") { return; }
      target.addEventListener("dragover", function (e) { if (dragging) { e.preventDefault(); target.classList.add("dropok"); } });
      target.addEventListener("dragleave", function () { target.classList.remove("dropok"); });
      target.addEventListener("drop", function (e) {
        if (!dragging) { return; }
        e.preventDefault();
        var t = $("input[name='__RequestVerificationToken']");
        var form = document.createElement("form");
        form.method = "post";
        form.action = "/folder/" + encodeURIComponent(from) + "/bulk";
        function field(k, v) { var i = document.createElement("input"); i.type = "hidden"; i.name = k; i.value = v; form.appendChild(i); }
        field("__RequestVerificationToken", t ? t.value : "");
        field("action", name === "Trash" ? "trash" : name === "Archive" ? "archive" : "move");
        field("to", name);
        dragging.forEach(function (id) { field("id", id); });
        document.body.appendChild(form);
        swapper.send(form);
      });
    });
  });

  /* ---------- 4. Selection ----------
     The actions act on ticked rows, so they are disabled until something is
     ticked; only the actions that apply are enabled - Mark as read only when
     an unread message is ticked, and so on (item 13). Shift-click ticks a
     range (item 37). When the whole page is ticked, "Select all N in this
     folder" offers the rest. Without the script every button stays enabled
     and the server handles an empty selection. */
  function offline() { return document.documentElement.classList.contains("offline"); }

  // One current sync: rebinding after a live refresh replaces it, so no
  // listener ever acts on rows that are no longer on the page.
  var currentSync = function () { /* nothing selectable yet */ };
  document.addEventListener("anjal:online", function () { currentSync(); });
  document.addEventListener("anjal:offline", function () { currentSync(); });

  function bindSelection() {
    var form = $("form[data-bulkform]");
    if (!form) { return; }
    var boxes = $$("input[data-rowcheck]", form);
    var actions = $$("[data-bulkaction]", form);
    var all = $("input[data-selectall]", form);
    var label = $("[data-bulklabel]", form);
    var whole = $("[data-selectall-folder-btn]", form);
    var wholeInput = $("input[data-selectall-folder]", form);
    var plain = label ? (label.getAttribute("data-word-plain") || label.textContent) : "";
    if (label) { label.setAttribute("data-word-plain", plain); }
    var last = -1;

    function rowOf(box) { return box.closest("[data-message]"); }

    var clear = $("[data-selclear]", form);
    if (clear) {
      clear.addEventListener("click", function () {
        boxes.forEach(function (b) { b.checked = false; });
        if (wholeInput) { wholeInput.value = ""; }
        sync();
      });
    }

    function sync() {
      var picked = boxes.filter(function (b) { return b.checked; });
      var everything = wholeInput && wholeInput.value === "1";
      var n = picked.length;
      var anyUnread = everything || picked.some(function (b) { return rowOf(b).getAttribute("data-seen") === "0"; });
      var anyRead = everything || picked.some(function (b) { return rowOf(b).getAttribute("data-seen") === "1"; });
      actions.forEach(function (b) {
        var off = n === 0 && !everything;
        if (b.hasAttribute("data-needs-unread")) { off = off || !anyUnread; }
        if (b.hasAttribute("data-needs-read")) { off = off || !anyRead; }
        b.disabled = off || (offline() && b.hasAttribute("data-needs-server"));
      });
      if (label) {
        var word = label.getAttribute("data-word-selected") || "{n} selected";
        label.textContent = n === 0 && !everything ? plain
          : word.replace("{n}", everything && whole ? whole.getAttribute("data-total") : String(n));
        label.classList.toggle("armed", n > 0 || everything);
      }
      // The approved board: the selection bar shows only while something is ticked,
      // in place of the search row; the list's dots become tick boxes meanwhile.
      var selbar = $("[data-selbar]", form);
      var col = form.closest(".listcol") || document.body;
      if (selbar) { selbar.classList.toggle("on", n > 0 || everything); }
      col.classList.toggle("selecting", n > 0 || everything);
      if (all) {
        all.checked = n === boxes.length && n > 0;
        all.indeterminate = n > 0 && n < boxes.length;
      }
      if (whole) {
        var total = parseInt(whole.getAttribute("data-total") || "0", 10);
        whole.hidden = !(all && all.checked) || everything || total <= boxes.length;
        whole.textContent = (whole.getAttribute("data-word") || "").replace("{n}", String(total));
      }
    }

    boxes.forEach(function (box, i) {
      box.addEventListener("click", function (e) {
        if (e.shiftKey && last >= 0 && last !== i) {
          var lo = Math.min(last, i), hi = Math.max(last, i);
          for (var k = lo; k <= hi; k++) { boxes[k].checked = box.checked; }
        }
        last = i;
        if (wholeInput) { wholeInput.value = ""; }
        sync();
      });
    });
    if (all && !all.hasAttribute("data-bound")) {
      all.setAttribute("data-bound", "1");
      all.addEventListener("change", function () {
        $$("input[data-rowcheck]", form).forEach(function (b) { b.checked = all.checked; });
        if (wholeInput) { wholeInput.value = ""; }
        currentSync();
      });
    }
    if (whole && !whole.hasAttribute("data-bound")) {
      whole.setAttribute("data-bound", "1");
      whole.addEventListener("click", function () {
        if (wholeInput) { wholeInput.value = "1"; }
        currentSync();
      });
    }
    currentSync = sync;
    sync();
  }
  page(bindSelection);

  /* ---------- 4b. Mark all read ----------
     Posts in the background and greys the unread dots in place. Without
     the script it is an ordinary submit and the page reloads. */
  page(function markAllRead() {
    var button = $("[data-markallread-btn]");
    // rc.15: the button sits in its own small form in the folder's More menu.
    var form = button ? (button.form || button.closest("form")) : null;
    if (!button || !form) { return; }
    button.addEventListener("click", function (e) {
      e.preventDefault();
      var body = new FormData();
      var token = form.querySelector("input[name='__RequestVerificationToken']");
      if (token) { body.set("__RequestVerificationToken", token.value); }
      fetch(button.getAttribute("formaction") || form.getAttribute("action"), {
        method: "POST", body: body, headers: { "X-Requested-With": "anjal" }
      })
        .then(function (r) {
          if (!r.ok) { window.location.reload(); return; }
          $$(".mrow.unread").forEach(function (row) { row.classList.remove("unread"); row.setAttribute("data-seen", "1"); });
          $$(".spine i.on").forEach(function (dot) { dot.classList.remove("on"); });
          // Hide the badge outright; an emptied badge is still a red pill (DEF-014).
          var badge = $("[data-unread-badge]");
          if (badge) { badge.textContent = ""; badge.hidden = true; }
          var count = $("[data-count]");
          if (count) {
            var total = count.getAttribute("data-total") || "0";
            count.textContent = total + (total === "1" ? " message" : " messages");
          }
        })
        .catch(function () { window.location.reload(); });
    });
  });

  /* ---------- 5. Keyboard shortcuts (item 38, UX-03) ----------
     The approved board's shortcuts, on every page: "?" opens the panel; c,
     g then i, /, [ and Shift+F work anywhere; j, k, Enter, x and Ctrl+A move
     and tick in a list; r, a, f, s, # and e act on the highlighted row, or on
     the open message. Nothing fires while typing, except Ctrl+Enter to send. */
  page(function shortcuts() {
    var hint = $("[data-kbhint]");
    if (hint) { hint.hidden = false; }
    var rows = $$("[data-message]");
    var index = -1;
    var pendingG = false;
    scope.on(document, "anjal:listchanged", function () { rows = $$("[data-message]"); index = -1; });

    function focusRow(i) {
      if (i < 0 || i >= rows.length) { return; }
      index = i;
      rows.forEach(function (r) { r.classList.remove("kbfocus"); });
      rows[i].classList.add("kbfocus");
      if (rows[i].scrollIntoView) { rows[i].scrollIntoView({ block: "nearest" }); }
    }

    function typing(target) {
      var tag = (target.tagName || "").toLowerCase();
      return tag === "input" || tag === "textarea" || tag === "select" || target.isContentEditable;
    }

    function token() {
      var t = $("input[name='__RequestVerificationToken']");
      return t ? t.value : "";
    }

    function postForm(action, fields) {
      var form = document.createElement("form");
      form.method = "post";
      form.action = action;
      fields.__RequestVerificationToken = token();
      Object.keys(fields).forEach(function (k) {
        var input = document.createElement("input");
        input.type = "hidden"; input.name = k; input.value = fields[k];
        form.appendChild(input);
      });
      document.body.appendChild(form);
      swapper.send(form);
    }

    // The message a key acts on: the highlighted row, or the one being read.
    function target() {
      if (index >= 0 && rows[index]) { return { row: rows[index], id: rows[index].getAttribute("data-message") }; }
      var reader = $("[data-reader-id]");
      return reader ? { row: null, id: reader.getAttribute("data-reader-id") } : null;
    }

    // In a list, tick only this row and press the selection bar's own button.
    function bulk(row, value, setup) {
      var box = row.querySelector("input[data-rowcheck]");
      var button = $("button[data-bulkaction][value='" + value + "']");
      if (!box || !button) { return false; }
      $$("input[data-rowcheck]").forEach(function (c) { c.checked = false; });
      box.checked = true;
      if (setup) { setup(); }
      button.disabled = false;
      button.click();
      return true;
    }

    function here() { return window.location.pathname + window.location.search; }

    scope.on(document, "keydown", function (e) {
      if (e.key === "Escape") {
        if (window.location.hash === "#shortcuts") { e.preventDefault(); window.location.hash = "close"; return; }
        if (typing(e.target)) { return; }
        var url = new URL(window.location.href);
        if (url.searchParams.has("open")) { url.searchParams.delete("open"); window.location.href = url.pathname + url.search; return; }
        var back = $(".focusbar a");
        if (back) { window.location.href = back.href; }
        return;
      }
      if (typing(e.target)) {
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
          var send = e.target.form ? e.target.form.querySelector("[data-send]") : null;
          if (send && !send.disabled) { e.preventDefault(); send.click(); }
        }
        return;
      }
      if (e.ctrlKey || e.metaKey) {
        var key = e.key.toLowerCase();
        if (key === "a" && rows.length) {
          e.preventDefault();
          var all = $("input[data-selectall]");
          if (all) { all.checked = true; all.dispatchEvent(new Event("change")); }
        } else if (key === "z") {
          var undo = $("form.undobar");
          if (undo) { e.preventDefault(); swapper.send(undo); }
        }
        return;
      }
      if (e.altKey) { return; }
      if (pendingG) {
        pendingG = false;
        if (e.key === "i") { e.preventDefault(); window.location.href = "/folder/INBOX"; }
        return;
      }
      var t = target();
      switch (e.key) {
        case "?": e.preventDefault(); window.location.hash = "shortcuts"; break;
        case "g": pendingG = true; window.setTimeout(function () { pendingG = false; }, 1500); break;
        case "/":
          e.preventDefault();
          var box = $(".sbox");
          if (box) { box.focus(); } else { window.location.href = "/search"; }
          break;
        case "c": e.preventDefault(); window.location.href = "/compose"; break;
        case "[":
          // rc.15: the rail's form has a button for each way; the one showing is the one to press.
          var foldBtn = $$("form.rail-fold button").filter(function (b) { return b.offsetParent !== null; })[0];
          if (foldBtn) { e.preventDefault(); foldBtn.click(); }
          break;
        case "F": {
          e.preventDefault();
          var view = $("[data-layout]");
          var now = view ? view.getAttribute("data-layout") : "three";
          postForm("/settings/layout", { layout: now === "focus" ? "three" : "focus", back: here() });
          break;
        }
        case "j": e.preventDefault(); focusRow(index + 1); break;
        case "k": e.preventDefault(); focusRow(index - 1); break;
        case "Enter":
          if (index >= 0) {
            var link = rows[index].querySelector("a[data-open]");
            if (link) { e.preventDefault(); window.location.href = link.href; }
          }
          break;
        case "x":
          if (index >= 0) {
            var tick = rows[index].querySelector("input[data-rowcheck]");
            if (tick) { e.preventDefault(); tick.click(); }
          }
          break;
        case "r": if (t) { e.preventDefault(); window.location.href = "/compose?reply=" + t.id; } break;
        case "a":
          if (t) {
            e.preventDefault();
            // Item 53: where many people, or anyone outside, would get it, the reader asks first.
            var check = $("details.replycheck");
            if (check && check.offsetParent !== null) { check.open = true; var s1 = check.querySelector("summary"); if (s1) { s1.focus(); } }
            else if (t.row) { var open = t.row.querySelector("a[data-open]"); window.location.href = open ? open.href : "/compose?replyall=" + t.id; }
            else { window.location.href = "/compose?replyall=" + t.id; }
          }
          break;
        case "f": if (t) { e.preventDefault(); window.location.href = "/compose?forward=" + t.id; } break;
        case "s":
          if (t) {
            var flag = t.row ? t.row.querySelector("button.fav") : $("[data-kb='flag']");
            if (flag) { e.preventDefault(); flag.click(); }
          }
          break;
        case "#":
          if (t) {
            e.preventDefault();
            if (!t.row || !bulk(t.row, "trash")) {
              var del = $("[data-kb='delete']");
              if (del) { del.click(); }
            }
          }
          break;
        case "e":
          // D-106: Archive is a standard folder, so e always works.
          if (t) {
            e.preventDefault();
            if (!t.row || !bulk(t.row, "archive")) {
              var archive = $("[data-kb='archive']");
              if (archive) { archive.click(); }
            }
          }
          break;
        default: break;
      }
    });
  });

  /* ---------- 5a. The tour (item 5, D-105) ----------
     Started by "Show me around" or "Restart the tour" (the #tour address):
     one bubble at a time beside the real button, with Next, Back and Skip;
     Esc ends it. A step whose button is not on this page is skipped. The
     bubble and its buttons are page markup (review 3); this only fills in
     each step's words, as text, and places it. */
  page(function tour() {
    var data = $("#tour");
    var bubble = $("#tourtip");
    if (!data || !bubble) { return; }
    var steps;
    try { steps = JSON.parse(data.getAttribute("data-steps") || "[]"); } catch (err) { return; }
    var title = $("[data-tour-title]", bubble);
    var text = $("[data-tour-text]", bubble);
    var count = $("[data-tour-count]", bubble);
    var skip = $("[data-tour-skip]", bubble);
    var back = $("[data-tour-back]", bubble);
    var nextButton = $("[data-tour-next]", bubble);
    var live = [];
    var i = 0;
    var mark = null;
    var running = false;

    function show() {
      var step = live[i];
      var el = document.querySelector(step.at);
      if (!el) { next(); return; }
      if (mark) { mark.classList.remove("tourmark"); }
      mark = el;
      el.classList.add("tourmark");
      if (el.scrollIntoView) { el.scrollIntoView({ block: "nearest" }); }
      title.textContent = step.title;
      text.textContent = step.text;
      count.textContent = (i + 1) + " / " + live.length;
      skip.hidden = i === live.length - 1;
      back.hidden = i === 0;
      nextButton.textContent = i < live.length - 1 ? nextButton.getAttribute("data-word-next") : nextButton.getAttribute("data-word-done");
      bubble.hidden = false;
      var r = el.getBoundingClientRect();
      var h = bubble.offsetHeight || 150;
      var top = r.bottom + 10;
      if (top + h > window.innerHeight - 8) { top = Math.max(8, r.top - h - 10); }
      bubble.style.top = top + "px";
      bubble.style.left = Math.min(Math.max(8, r.left), Math.max(8, window.innerWidth - 330)) + "px";
      nextButton.focus();
    }

    function next() { if (i < live.length - 1) { i += 1; show(); } else { end(); } }

    function end() {
      running = false;
      bubble.hidden = true;
      if (mark) { mark.classList.remove("tourmark"); mark = null; }
      if (window.history && window.history.replaceState) { window.history.replaceState(null, "", window.location.pathname + window.location.search); }
    }

    function start() {
      if (running) { return; }
      live = steps.filter(function (s) { return !!document.querySelector(s.at); });
      if (!live.length) { return; }
      running = true;
      i = 0;
      show();
    }

    nextButton.addEventListener("click", next);
    back.addEventListener("click", function () { if (i > 0) { i -= 1; show(); } });
    skip.addEventListener("click", end);
    scope.on(document, "keydown", function (e) {
      if (running && e.key === "Escape") { e.preventDefault(); e.stopPropagation(); end(); }
    }, true);
    if (window.location.hash === "#tour") { start(); }
    scope.on(window, "hashchange", function () { if (window.location.hash === "#tour") { start(); } });
  });

  /* ---------- 5a2. Keyboard focus follows what opens (UX-09) ----------
     When the welcome screen shows, or a panel or sheet opens (the "?" panel,
     the folders sheet, the envelope sheet), focus moves to its first button
     or link, so the next key acts inside it. */
  page(function focusWhatOpens() {
    function first(container) {
      return container ? container.querySelector("button:not([disabled]):not([hidden]), a[href], input:not([type='hidden']), select") : null;
    }
    function focusTarget() {
      var id = window.location.hash.slice(1);
      if (!id || id === "close" || id === "tour") { return; }
      var panel = document.getElementById(id);
      if (panel && (panel.classList.contains("kbpanel") || panel.classList.contains("phonesheet") || panel.classList.contains("envelope"))) {
        var f = first(panel);
        if (f) { f.focus(); }
      }
    }
    var welcome = $(".welcome");
    if (welcome) {
      var f = welcome.querySelector(".btn-p") || first(welcome);
      if (f) { f.focus(); }
    } else {
      focusTarget();
    }
    scope.on(window, "hashchange", focusTarget);
  });

  /* ---------- 5a3. The list keeps its place (owner, 6 Oct) ----------
     Opening a message reloads the page; the list's scroll position is
     remembered for this folder page and put back, and the open message is
     kept in view. Nothing else is stored. */
  page(function keepListPlace() {
    var col = $(".fview .listcol");
    if (!col) { return; }
    var url = new URL(window.location.href);
    var key = "anjal:listscroll:" + url.pathname + "?" + (url.searchParams.get("page") || "0") + ":" + (url.searchParams.get("show") || "all");
    try {
      var saved = window.sessionStorage.getItem(key);
      if (saved !== null) { col.scrollTop = parseInt(saved, 10) || 0; }
    } catch (err) { /* storage unavailable: the list starts at the top */ }
    var open = $(".mrow.open", col);
    if (open && open.scrollIntoView) {
      var r = open.getBoundingClientRect(), c = col.getBoundingClientRect();
      if (r.top < c.top || r.bottom > c.bottom) { open.scrollIntoView({ block: "nearest" }); }
    }
    function remember() {
      try { window.sessionStorage.setItem(key, String(col.scrollTop)); } catch (err) { /* ignore */ }
    }
    col.addEventListener("click", function (e) { if (e.target.closest("a[data-open]")) { remember(); } });
    scope.on(document, "anjal:leaving", remember);
    scope.on(window, "pagehide", remember);
  });

  /* ---------- 5b. Choices that apply at once ----------
     A field marked data-autosubmit (messages per page; the Appearance and
     Language and time choices, which "apply at once") submits its own form
     when changed; the page's security policy allows no inline handlers. */
  page(function choices() {
    $$("[data-autosubmit]").forEach(function (field) {
      field.addEventListener("change", function () { if (field.form) { swapper.send(field.form); } });
    });
  });

  /* ---------- 5b0. Offline mail (rc.15, item 65 b) ----------
     When the person has turned it on (never on a shared computer), the
     service worker keeps their newest mail encrypted in this browser and
     answers from it when the network is gone; it is brought up to date on
     each page and every quarter of an hour. When it is off, any copies a
     worker kept are removed and the worker goes. */
  (function offlineMail() {
    var host = document.querySelector("[data-offline-mail]");
    var lockBox = document.querySelector("[data-offline-lock]");
    if (!("serviceWorker" in navigator)) {
      if (lockBox) { $("[data-offline-state]", lockBox).textContent = lockBox.getAttribute("data-word-noworker"); }
      return;
    }
    // DES-11 D5/F11: a sign-in or signed-out page means no session - any list kept here goes.
    if (!host && /^\/(sign-in|signed-out)/.test(window.location.pathname)) {
      navigator.serviceWorker.getRegistrations().then(function (regs) {
        regs.forEach(function (reg) { if (reg.active) { reg.active.postMessage({ kind: "wipe" }); } });
      }).catch(function () { /* nothing registered */ });
      return;
    }
    if (!host) { return; }
    var on = host.getAttribute("data-offline-mail") === "on";
    var words = {};
    try { words = JSON.parse(host.getAttribute("data-offline-words") || "{}"); } catch (e) { words = {}; }
    // Ask the worker something and wait for its answer.
    function ask(w, message) {
      return new Promise(function (resolve) {
        var ch = new MessageChannel();
        ch.port1.onmessage = function (m) { resolve(m.data || {}); };
        w.postMessage(message, [ch.port2]);
      });
    }
    if (on) {
      navigator.serviceWorker.register("/sw.js", { scope: "/" }).then(function (reg) {
        function worker() { return navigator.serviceWorker.controller || reg.active || reg.waiting || reg.installing; }
        function sync() {
          var w = worker();
          if (w && navigator.onLine) { w.postMessage({ kind: "sync", words: words }); }
        }
        navigator.serviceWorker.ready.then(function (r) {
          sync();
          if (lockBox) { offlineLock(r.active, lockBox); }
          // A page reached by a swap may carry the lock too (Settings).
          document.addEventListener("anjal:swapped", function () {
            var box = $("[data-offline-lock]");
            if (box) { offlineLock(r.active, box); }
          });
        });
        setInterval(sync, 15 * 60 * 1000);
        window.addEventListener("online", sync);
      }).catch(function () { /* not available here: online use is unchanged */ });
    } else {
      navigator.serviceWorker.getRegistrations().then(function (regs) {
        regs.forEach(function (reg) {
          if (reg.active) { reg.active.postMessage({ kind: "wipe" }); }
          setTimeout(function () { reg.unregister(); }, 1500);
        });
      }).catch(function () { /* nothing registered */ });
    }
  }());

  /* ---------- 5a9. The flap from the open card into the letter (DES-11 D9) ----------
     Three panes side by side only: the marigold flap sits in the gap beside the
     open card, at its middle, follows it as the list scrolls, and hides when the
     card is out of view or the panes are not side by side. */
  page(function flapMark() {
    var mark = $("[data-flapmark]");
    if (!mark) { return; }
    var view = mark.parentElement;
    var list = $(".listcol", view);
    var read = $(".readcol", view);
    var row = $(".mrow.open", view);
    if (!list || !read || !row) { return; }
    function place() {
      var v = view.getBoundingClientRect();
      var l = list.getBoundingClientRect();
      var r = row.getBoundingClientRect();
      var c = read.getBoundingClientRect();
      var side = l.width > 0 && r.width > 0 && c.left >= l.right - 1;
      var middle = r.top + r.height / 2;
      var show = side && middle > l.top + 14 && middle < l.bottom - 14;
      mark.hidden = !show;
      if (!show) { return; }
      // Owner, 10 Oct 2026: the flap sits beside the open card, before the list's scroll bar (Windows
      // shows one), never on it: its stroke is centred between the card's edge and the inside edge
      // of the list's scroll area.
      var inner = l.left + list.clientLeft + list.clientWidth;
      var ink = 11.5;
      mark.style.top = Math.round(middle - v.top - 12) + "px";
      mark.style.left = Math.round(r.right - v.left + Math.max(0, (inner - r.right - ink) / 2) - 2.25) + "px";
    }
    list.addEventListener("scroll", place, { passive: true });
    scope.on(window, "resize", place);
    place();
  });

  /* ---------- 5b0a. Who should get a changed default (DES-11 D7) ----------
     The question shows once a default is changed; "Everyone" is not offered
     where it would keep mail longer than people chose. */
  page(function whoGets() {
    $$("[data-whogets]").forEach(function (box) {
      var form = box.closest("form");
      if (!form) { return; }
      var fields = $$("[data-default]", form);
      var every = $("[data-whogets-everyone]", box);
      var note = $("[data-whogets-note]", box);
      function update() {
        var changed = false, longer = false;
        fields.forEach(function (f) {
          var was = f.getAttribute("data-was");
          if (f.type === "radio") { if (f.checked && f.value !== was) { changed = true; } return; }
          if (f.value !== was) {
            changed = true;
            if (f.hasAttribute("data-longer-looser") && Number(f.value) > Number(was)) { longer = true; }
          }
        });
        box.hidden = !changed;
        if (every) {
          every.hidden = longer;
          var r = $("input", every);
          if (longer && r.checked) { $("input[value=followers]", box).checked = true; }
        }
        if (note) { note.hidden = !longer; }
      }
      fields.forEach(function (f) { f.addEventListener("change", update); });
      update();
    });
  });

  /* ---------- 5b0b. The offline lock (DES-11 D5) ----------
     In Settings: set this device's offline code (it never leaves the
     device), and - where the passkey can derive a secret (WebAuthn PRF) - let
     the passkey open the list too. */
  function offlineLock(w, box) {
    var state = $("[data-offline-state]", box);
    var code = $("[data-offline-code]", box);
    var set = $("[data-offline-setcode]", box);
    var pk = $("[data-offline-passkey-add]", box);
    function say(k) { state.textContent = box.getAttribute("data-word-" + k) || ""; }
    function ask(message) {
      return new Promise(function (resolve) {
        var ch = new MessageChannel();
        ch.port1.onmessage = function (m) { resolve(m.data || {}); };
        w.postMessage(message, [ch.port2]);
      });
    }
    var salt = "";
    function refresh() {
      return ask({ kind: "status" }).then(function (s) {
        salt = s.salt || "";
        say(s.set ? (s.passkey ? "setpk" : "set") : "notset");
        var ids = (box.getAttribute("data-passkeys") || "").split(",").filter(Boolean);
        pk.hidden = !(s.set && !s.passkey && ids.length && window.PublicKeyCredential);
      });
    }
    refresh();
    set.addEventListener("click", function () {
      if ((code.value || "").length < 6) { say("short"); code.focus(); return; }
      ask({ kind: "setcode", code: code.value }).then(function (r) {
        code.value = "";
        if (!r.ok) { say("short"); return; }
        refresh();
      });
    });
    pk.addEventListener("click", function () {
      var ids = (box.getAttribute("data-passkeys") || "").split(",").filter(Boolean);
      navigator.credentials.get({ publicKey: {
        challenge: crypto.getRandomValues(new Uint8Array(32)),
        allowCredentials: ids.map(function (id) { return { type: "public-key", id: fromB64(id) }; }),
        userVerification: "required",
        extensions: { prf: { eval: { first: new Uint8Array(fromB64(salt.replace(/\+/g, "-").replace(/\//g, "_"))) } } }
      } }).then(function (cred) {
        var r = cred.getClientExtensionResults();
        var first = r && r.prf && r.prf.results && r.prf.results.first;
        if (!first) { throw new Error("no prf"); }
        return ask({ kind: "setpasskey", secret: first, ids: ids });
      }).then(function (r) {
        if (!r.ok) { throw new Error("not set"); }
        refresh();
      }).catch(function () { say("pkfail"); pk.hidden = true; });
    });
  }

  /* ---------- 5b1. Copy (rc.14) ----------
     A button marked data-copy puts its value on the clipboard and says so
     for a moment: DNS records, invitation links, application keys. Without
     the script the value stays on the page to select by hand. */
  page(function copyButtons() {
  $$("[data-copy]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var text = btn.getAttribute("data-copy") || "";
      if (!navigator.clipboard) { return; }
      navigator.clipboard.writeText(text).then(function () {
        btn.classList.add("copied");
        btn.setAttribute("aria-live", "polite");
        var was = btn.getAttribute("title");
        btn.setAttribute("title", "Copied");
        setTimeout(function () { btn.classList.remove("copied"); if (was) { btn.setAttribute("title", was); } }, 1600);
      });
    });
  });
  });

  /* ---------- 5b2. Print (D-103) ----------
     The print page opens the browser's print dialog by itself. */
  if (document.body && document.body.hasAttribute("data-autoprint")) {
    window.addEventListener("load", function () { window.print(); });
  }

  /* ---------- 5b3. The envelope (item 52, D-103) ----------
     "Find a person" narrows the full recipient list as you type; the More
     menu closes when you click elsewhere. Without the script the list shows
     everyone and the menu closes from its own button. */
  page(function findPerson() {
  $$("[data-findperson]").forEach(function (input) {
    var box = input.closest("details") || document;
    input.addEventListener("input", function () {
      var q = input.value.trim().toLowerCase();
      $$("[data-person]", box).forEach(function (p) {
        p.hidden = q.length > 0 && p.getAttribute("data-person").indexOf(q) < 0;
      });
    });
  });
  });
  // rc.15 (owner, 7 Oct): every small menu - the list's three dots, the
  // envelope's More, New category, Send later and the rest - closes when
  // you click anywhere else or press Escape, and only one is open at a time.
  // rc.15 (owner, 7 Oct, second report): the rule covers EVERY pop-up menu -
  // anything that opens over the page - not a list of named ones, so a new
  // menu cannot be missed. A click inside an open message's text happens in
  // the message's own frame and never reaches this page; the page losing
  // focus to that frame closes the menus too.
  function openPopups() { return $$("details[open]").filter(function (d) { return !!popupBody(d); }); }
  document.addEventListener("click", function (e) {
    openPopups().forEach(function (menu) {
      if (!menu.contains(e.target)) { menu.open = false; }
    });
  });
  window.addEventListener("blur", function () {
    window.setTimeout(function () {
      var a = document.activeElement;
      if (a && a.tagName === "IFRAME") { openPopups().forEach(function (menu) { menu.open = false; }); }
    }, 0);
  });
  document.addEventListener("keydown", function (e) {
    if (e.key !== "Escape") { return; }
    var open = openPopups();
    if (!open.length) { return; }
    open.forEach(function (menu) { menu.open = false; });
    var s = open[open.length - 1].querySelector("summary");
    if (s) { s.focus(); }
    e.stopPropagation();
  }, true);

  // rc.15 (owner, 7 Oct): an open menu is never drawn behind anything. A menu
  // sits inside layers - the selection bar, the list's head row, a sticky
  // envelope - and a later layer on the page (the day headings, the search
  // row) could cover it. When any pop-up menu opens, every layer it sits in
  // is raised above the rest of the page; when it closes they go back. One
  // rule for every menu, so a new one cannot repeat the fault.
  function popupBody(d) {
    for (var c = d.firstElementChild; c; c = c.nextElementSibling) {
      if (c.tagName === "SUMMARY") { continue; }
      var pos = getComputedStyle(c).position;
      if (pos === "absolute" || pos === "fixed") { return c; }
    }
    return null;
  }
  function lift(d) {
    var raised = [];
    for (var el = d.parentElement; el && el !== document.body; el = el.parentElement) {
      var cs = getComputedStyle(el);
      if (cs.position !== "static" || cs.zIndex !== "auto") {
        el.classList.add("menu-lift");
        raised.push(el);
      }
    }
    d.classList.add("menu-lift");
    d._lifted = raised;
  }
  function drop(d) {
    (d._lifted || []).forEach(function (el) {
      if (!el.querySelector("details.menu-lift[open]")) { el.classList.remove("menu-lift"); }
    });
    d.classList.remove("menu-lift");
    d._lifted = null;
  }
  // A menu inside a box that scrolls or clips (the message details, the
  // compose column, the signature editor) would be cut off at the box's
  // edge, and one near the foot of the screen would run off it. Such a menu
  // is drawn over the whole page instead, beside its button, opening upward
  // when there is no room below.
  function clipped(d, body) {
    var r = body.getBoundingClientRect();
    // A menu already drawn against the window is clipped by nothing but the window.
    if (getComputedStyle(body).position === "fixed") {
      return r.left < 0 || r.top < 0 || r.right > window.innerWidth || r.bottom > window.innerHeight;
    }
    if (r.left < 0 || r.top < 0 || r.right > window.innerWidth || r.bottom > window.innerHeight) { return true; }
    for (var el = body.parentElement; el && el !== document.body; el = el.parentElement) {
      var cs = getComputedStyle(el);
      if (cs.overflowX !== "visible" || cs.overflowY !== "visible") {
        var b = el.getBoundingClientRect();
        if (r.left < b.left - 1 || r.top < b.top - 1 || r.right > b.right + 1 || r.bottom > b.bottom + 1) { return true; }
      }
    }
    return false;
  }
  function place(d) {
    var body = d._float, s = d.querySelector(":scope > summary");
    if (!body || !s) { return; }
    var sr = s.getBoundingClientRect(), gap = 6, edge = 8;
    var vw = window.innerWidth, vh = window.innerHeight;
    body.style.maxHeight = (vh - 2 * edge) + "px";
    var w = body.offsetWidth, h = body.offsetHeight;
    var left = d._alignRight ? sr.right - w : sr.left;
    left = Math.max(edge, Math.min(left, vw - w - edge));
    var top = sr.bottom + gap;
    if (top + h > vh - edge) {
      top = sr.top - gap - h >= edge ? sr.top - gap - h : Math.max(edge, vh - edge - h);
    }
    body.style.left = left + "px";
    body.style.top = top + "px";
    // A layer around the menu may still hold its fixed position (one that
    // really is turned or moved, and so is left as it is): correct by the
    // difference between where the menu landed and where it should be.
    var got = body.getBoundingClientRect();
    if (Math.abs(got.left - left) > 0.5) { body.style.left = (2 * left - got.left) + "px"; }
    if (Math.abs(got.top - top) > 0.5) { body.style.top = (2 * top - got.top) + "px"; }
  }
  function float(d, body) {
    var r = body.getBoundingClientRect(), sr = d.querySelector(":scope > summary").getBoundingClientRect();
    d._alignRight = Math.abs(r.right - sr.right) < Math.abs(r.left - sr.left);
    d._floatStyle = body.getAttribute("style");
    d._float = body;
    body.style.width = r.width + "px";
    body.style.position = "fixed";
    body.style.right = "auto";
    body.style.bottom = "auto";
    body.style.margin = "0";
    body.style.transform = "none";
    body.style.overflowY = "auto";
    body.classList.add("menu-float");
    // A transform or filter on a layer around the menu would hold a fixed
    // menu inside that layer; it is set aside while the menu floats.
    d._hosts = [];
    for (var el = body.parentElement; el && el !== document.documentElement; el = el.parentElement) {
      var cs = getComputedStyle(el);
      // Only effects that change nothing on screen are set aside (a transform
      // that does not move anything, filters, containment): a layer that is
      // really moved keeps its place, so the button never jumps.
      var still = cs.transform === "none" || cs.transform === "matrix(1, 0, 0, 1, 0, 0)";
      if (!still) { continue; }
      if (cs.transform !== "none" || cs.filter !== "none" || (cs.backdropFilter && cs.backdropFilter !== "none") ||
          cs.perspective !== "none" || (cs.contain && cs.contain !== "none") || /transform|filter|perspective/.test(cs.willChange)) {
        el.classList.add("menu-host");
        d._hosts.push(el);
      }
    }
    place(d);
  }
  function unfloat(d) {
    var body = d._float;
    if (!body) { return; }
    if (d._floatStyle === null) { body.removeAttribute("style"); } else { body.setAttribute("style", d._floatStyle); }
    body.classList.remove("menu-float");
    (d._hosts || []).forEach(function (el) { el.classList.remove("menu-host"); });
    d._hosts = null;
    d._float = null;
  }
  function floating() { return $$("details.menu-lift[open]").filter(function (d) { return d._float; }); }
  window.addEventListener("resize", function () { floating().forEach(place); });
  document.addEventListener("scroll", function (e) {
    floating().forEach(function (d) {
      if (!d._float.contains(e.target)) { place(d); }
    });
  }, true);
  document.addEventListener("toggle", function (e) {
    var d = e.target;
    if (!d || d.tagName !== "DETAILS") { return; }
    if (d.open) {
      var body = popupBody(d);
      if (body) {
        lift(d);
        if (clipped(d, body)) { float(d, body); }
      }
    } else if (d._lifted) {
      unfloat(d);
      drop(d);
    }
  }, true);

  /* ---------- 5c. Live update and the new-mail sound (item 11; owner, 9 Oct 2026: A, B, C) ----------
     New mail updates the rail count and the tab title, plays Anjal's own
     chime once (made by the browser: no sound file), and appears in the INBOX
     list by itself when the reader is at the top with nothing selected -
     otherwise a bar offers to show it, so the list never jumps under the
     mouse. Browsers play sound only after the first click or key press.
     A: the newest mail already announced is remembered in this browser, so
        mail that arrives while a page changes still chimes, and only one tab
        chimes - the one holding the browser's lock for this mailbox; the
        others are told by it and update their counts quietly.
     B: that tab listens on a live channel (/api/events), which says within
        about a second that mail has come; the check every 30 seconds stays,
        in case the channel is cut.
     C: pages swap in place (section 0), so this keeps running between them. */
  (function liveUpdate() {
    var bar = null, sound = false, updated = null, badge = null, baseTitle = document.title;
    function grab() {
      bar = $("[data-statusbar]");
      if (!bar) { return false; }
      sound = bar.getAttribute("data-sound") === "1";
      updated = $("[data-updated]");
      badge = $("[data-unread-badge]");
      baseTitle = document.title.replace(/^\(\d+\) /, "");
      return true;
    }
    if (!grab()) { return; }
    var me = bar.getAttribute("data-me") || "me";
    var KEY = "anjal:newest:" + me;
    var known = null;
    var lastUnread = null;
    var latest = null;
    var audio = null;
    var leader = !(navigator.locks && navigator.locks.request);
    var channel = window.BroadcastChannel ? new BroadcastChannel("anjal-live-" + me) : null;

    function heard() { try { return parseInt(window.localStorage.getItem(KEY) || "0", 10) || 0; } catch (err) { return -1; } }
    function hear(n) { try { if (n > heard()) { window.localStorage.setItem(KEY, String(n)); } } catch (err) { /* storage off: this tab alone */ } }

    function wake() {
      if (!sound) { return; }
      if (!audio) {
        var Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) { return; }
        try { audio = new Ctx(); } catch (e) { return; }
      }
      if (audio.state === "suspended") { audio.resume().catch(function () { /* allowed after a click */ }); }
    }
    wake();
    ["pointerdown", "keydown", "touchstart"].forEach(function (kind) {
      document.addEventListener(kind, wake, { capture: true, passive: true });
    });

    function chime(always) {
      if (!sound && !always) { return; }
      var was = sound;
      sound = true;
      wake();
      sound = was;
      if (!audio) { return; }
      if (audio.state === "running") { notes(); return; }
      // Woken now; a chime the browser holds back until a later click is dropped, never played late.
      var asked = Date.now();
      audio.resume().then(function () {
        if (audio.state === "running" && Date.now() - asked < 3000) { notes(); }
      }).catch(function () { /* the browser allows sound after a click on the page */ });
    }
    // rc.13 (Mail settings): "Play it now" plays the chime whatever the setting.
    window.anjalChime = function () { chime(true); };

    function notes() {
      [[880, 0], [1318.5, 0.16]].forEach(function (note) {
        var osc = audio.createOscillator();
        var gain = audio.createGain();
        var start = audio.currentTime + note[1];
        osc.type = "sine";
        osc.frequency.value = note[0];
        gain.gain.setValueAtTime(0, start);
        gain.gain.linearRampToValueAtTime(0.12, start + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.42);
        osc.connect(gain);
        gain.connect(audio.destination);
        osc.start(start);
        osc.stop(start + 0.45);
      });
    }

    function clock(ms) { return window.anjalTime(new Date(ms)); }

    function showUnread(n) {
      document.title = (n > 0 ? "(" + n + ") " : "") + baseTitle;
      if (badge) { badge.textContent = n > 0 ? String(n) : ""; badge.hidden = n === 0; }
    }

    function listHere() {
      var list = $("[data-list]");
      return list && list.getAttribute("data-folder") === "INBOX" && list.getAttribute("data-page") === "0" ? list : null;
    }

    function refreshList() {
      fetch(window.location.href, { cache: "no-store", headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.text() : null; })
        .then(function (html) {
          if (!html) { return; }
          var doc = new DOMParser().parseFromString(html, "text/html");
          ["[data-list]", "[data-count]"].forEach(function (sel) {
            var fresh = doc.querySelector(sel), old = $(sel);
            if (fresh && old) { old.replaceWith(fresh); }
          });
          bindSelection();
          document.dispatchEvent(new Event("anjal:listchanged"));
        })
        .catch(function () { /* the next check tries again */ });
    }

    // New mail in this tab's list: shown at once at the top, otherwise offered.
    function arrived(count) {
      var list = listHere();
      if (!list) { return; }
      var pane = $("#main");
      var atTop = !pane || pane.scrollTop < 40;
      var picked = $$("input[data-rowcheck]").some(function (b) { return b.checked; });
      if (atTop && !picked) { refreshList(); return; }
      var newbar = $("[data-newbar]");
      if (newbar) {
        var text = $("[data-newbar-text]", newbar);
        if (text) { text.textContent = (bar.getAttribute("data-word-new") || "{n} new messages just arrived.").replace("{n}", String(Math.max(1, count))); }
        newbar.hidden = false;
      }
    }

    // A state from the server (or from the tab that listens): counts, the chime, the list.
    function take(state, fromLeader) {
      if (!state || typeof state.newest !== "number") { return; }
      latest = state;
      var now = Date.now();
      if (updated) {
        updated.textContent = (bar.getAttribute("data-word-updated") || "Updated {time}").replace("{time}", clock(now));
        updated.setAttribute("data-at", String(now));
      }
      showUnread(state.unread);
      if (leader && !fromLeader) {
        var before = heard();
        if (before < 0) { if (known !== null && state.newest > known) { chime(false); } }
        else if (before === 0) { hear(state.newest); }
        else if (state.newest > before) { hear(state.newest); chime(false); }
      }
      if (known === null) {
        var list = listHere();
        known = list ? parseInt(list.getAttribute("data-newest") || "0", 10) : state.newest;
      }
      if (state.newest > known) {
        arrived(lastUnread === null ? 1 : state.unread - lastUnread);
        known = state.newest;
      }
      lastUnread = state.unread;
    }
    function share(state) { if (channel && state) { channel.postMessage(state); } }

    function check() {
      if (document.documentElement.classList.contains("offline")) { return; }
      fetch("/api/state", { cache: "no-store", headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (state) { take(state, false); if (leader) { share(state); } })
        .catch(function () { /* offline: the connection light says so */ });
    }

    // B: the live channel, opened by the tab that holds the lock.
    var stream = null;
    function listen() {
      if (stream || !window.EventSource) { return; }
      stream = new EventSource("/api/events");
      stream.addEventListener("state", function (e) {
        var state = null;
        try { state = JSON.parse(e.data); } catch (err) { return; }
        take(state, false);
        share(state);
      });
      stream.addEventListener("end", function () { stream.close(); stream = null; window.setTimeout(listen, 1000); });
    }
    if (channel) {
      channel.onmessage = function (e) { if (!leader) { take(e.data, true); } };
    }
    if (navigator.locks && navigator.locks.request) {
      navigator.locks.request("anjal-live-" + me, function () {
        leader = true;
        listen();
        check();
        return new Promise(function () { /* held while this tab is open */ });
      }).catch(function () { leader = true; listen(); });
    } else {
      listen();
    }

    document.addEventListener("anjal:offline", function () {
      if (!updated) { return; }
      var at = parseInt(updated.getAttribute("data-at") || String(Date.now()), 10);
      updated.textContent = (bar.getAttribute("data-word-offline") || "Offline: showing mail as of {time}").replace("{time}", clock(at));
    });
    document.addEventListener("anjal:online", check);
    // C: a new page in place - its own status bar, title and list.
    document.addEventListener("anjal:swapped", function () {
      if (!grab()) { return; }
      sound = bar.getAttribute("data-sound") === "1";
      known = null;
      if (latest) { showUnread(latest.unread); }
    });
    document.addEventListener("visibilitychange", function () { if (!document.hidden) { check(); } });
    var timer = window.setInterval(check, 30000);
    window.addEventListener("pagehide", function () { window.clearInterval(timer); if (stream) { stream.close(); } });
    check();
  }());

  // rc.13 (Mail settings): "Play it now" plays the chime whatever the setting.
  page(function playNow() {
    $$("[data-play-sound]").forEach(function (b) {
      b.addEventListener("click", function () { if (window.anjalChime) { window.anjalChime(); } });
    });
  });

  /* ---------- 5e. Offline: say why an action waits (item 65a) ---------- */
  (function offlineReasons() {
    var bar = $("[data-statusbar]");
    var reason = bar ? bar.getAttribute("data-word-needs-server") : null;
    if (!reason) { return; }
    function mark(on) {
      $$("[data-needs-server]").forEach(function (el) {
        if (on) { el.setAttribute("data-was-title", el.getAttribute("title") || ""); el.setAttribute("title", reason); }
        else if (el.hasAttribute("data-was-title")) { el.setAttribute("title", el.getAttribute("data-was-title")); el.removeAttribute("data-was-title"); }
      });
    }
    document.addEventListener("anjal:offline", function () { mark(true); });
    document.addEventListener("anjal:online", function () { mark(false); });
    document.addEventListener("anjal:swapped", function () { if (document.documentElement.classList.contains("offline")) { mark(true); } });
  }());

  /* ---------- 5d. Formatting editor ----------
     A textarea marked data-rte becomes a formatting editor with a toolbar.
     The textarea stays in the form, hidden, and is kept in step with the
     editor's plain text; the hidden data-rte-html input carries its HTML.
     The server sanitises the HTML and derives the plain part from it, so
     nothing here is trusted. Without JavaScript, or where the browser cannot
     edit rich text, the plain textarea is simply what the user types in.
     rc.12 (item 15): size, colour, highlight, alignment, indent, emoji, a
     background for a paragraph and for the whole mail. Formatting is written
     as inline styles, the form every mail program shows. */
  page(function formattingEditor() {
    if (typeof document.execCommand !== "function") { return; }
    function textToHtml(text) {
      return String(text || "").split(/\r?\n/).map(function (line) {
        var safe = line.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        return "<div>" + (safe.length ? safe : "<br>") + "</div>";
      }).join("");
    }
    $$("textarea[data-rte]").forEach(function (area) {
      var form = area.form;
      var html = form ? form.querySelector("input[data-rte-html]") : null;
      if (!form || !html) { return; }

      // The toolbar is part of the page (shared component), hidden until now.
      var bar = form.querySelector("[data-rte-bar]");
      if (!bar) { return; }
      var editor = document.createElement("div");
      editor.className = "rte-ed " + area.className.replace("canvas", "canvas rte-canvas");
      editor.contentEditable = "true";
      editor.setAttribute("role", "textbox");
      editor.setAttribute("aria-multiline", "true");
      editor.setAttribute("aria-label", area.getAttribute("aria-label") || "Message");
      editor.setAttribute("data-rte-editor", "");
      editor.innerHTML = html.value && html.value.trim() ? html.value : textToHtml(area.value);
      area.parentNode.insertBefore(editor, area);
      bar.hidden = false;
      area.hidden = true;
      area.setAttribute("aria-hidden", "true");

      // The whole mail's background lives on one wrapper inside the editor.
      function mailBgWrap() {
        var first = editor.firstElementChild;
        return first && first.classList && first.classList.contains("anjal-mailbg") && editor.children.length === 1 ? first : null;
      }
      function paintEditorBg() {
        var w = mailBgWrap();
        editor.style.backgroundColor = w ? w.style.backgroundColor : "";
        editor.style.backgroundImage = w ? w.style.backgroundImage : "";
        var label = form.querySelector("[data-bg-name]");
        if (label) { label.textContent = w ? (w.getAttribute("data-name") || "") : ""; }
      }
      function setMailBg(colour, picture) {
        var w = mailBgWrap();
        if (!colour) {
          if (w) {
            while (w.firstChild) { editor.insertBefore(w.firstChild, w); }
            editor.removeChild(w);
          }
        } else {
          if (!w) {
            w = document.createElement("div");
            w.className = "anjal-mailbg";
            while (editor.firstChild) { w.appendChild(editor.firstChild); }
            if (!w.firstChild) { w.innerHTML = "<div><br></div>"; }
            editor.appendChild(w);
          }
          // Written as one attribute, so the picture travels exactly as the server checks it.
          w.setAttribute("style", "background-color:" + colour + (picture ? ";background-image:url(" + picture + ")" : "") + ";padding:20px 24px;border-radius:10px");
        }
        paintEditorBg();
      }
      // The paragraph the cursor is in: the closest block inside the editor.
      function currentBlock() {
        var sel = window.getSelection();
        if (!sel || sel.rangeCount === 0) { return null; }
        var node = sel.anchorNode;
        if (node && node.nodeType === 3) { node = node.parentNode; }
        while (node && node !== editor) {
          if (/^(DIV|P|LI|BLOCKQUOTE|H[1-6])$/.test(node.nodeName) && !node.classList.contains("anjal-mailbg")) { return node; }
          node = node.parentNode;
        }
        return null;
      }

      function sync() {
        html.value = editor.innerHTML;
        area.value = editor.innerText;
        form.dispatchEvent(new CustomEvent("anjal:edited"));
      }
      // Show which formatting applies where the cursor is.
      var STATEFUL = ["bold", "italic", "underline", "strikeThrough", "insertUnorderedList", "insertOrderedList", "justifyLeft", "justifyCenter", "justifyRight"];
      var size = bar.querySelector("[data-cmd-select='fontSize']");
      function reflect() {
        var sel = window.getSelection();
        var inside = sel && sel.rangeCount > 0 && editor.contains(sel.anchorNode);
        STATEFUL.forEach(function (cmd) {
          var b = bar.querySelector("[data-cmd='" + cmd + "']");
          var on = false;
          try { on = inside && document.queryCommandState(cmd); } catch (err) { on = false; }
          if (b) { b.setAttribute("aria-pressed", on ? "true" : "false"); }
        });
        if (size && inside) {
          var v = "";
          try { v = String(document.queryCommandValue("fontSize") || ""); } catch (err) { v = ""; }
          size.value = ["2", "3", "5", "6"].indexOf(v) >= 0 ? v : "3";
        }
      }
      scope.on(document, "selectionchange", reflect);
      editor.addEventListener("input", sync);
      form.addEventListener("submit", sync, true);

      // The cursor is kept while a pop-up or the size list is used.
      var saved = null;
      function remember() {
        var sel = window.getSelection();
        if (sel && sel.rangeCount > 0 && editor.contains(sel.anchorNode)) { saved = sel.getRangeAt(0).cloneRange(); }
      }
      function restore() {
        editor.focus();
        if (saved) {
          var sel = window.getSelection();
          sel.removeAllRanges();
          sel.addRange(saved);
        }
      }
      editor.addEventListener("keyup", remember);
      editor.addEventListener("mouseup", remember);
      editor.addEventListener("blur", remember);

      // Keep the text selection when a toolbar button is pressed.
      bar.addEventListener("mousedown", function (e) {
        if (e.target.closest("button") || e.target.closest("summary")) { e.preventDefault(); }
      });
      // One pop-up open at a time; Escape or a click elsewhere closes it.
      function closePops(except) {
        $$("details.rte-pop[open]", bar).forEach(function (d) { if (d !== except) { d.open = false; } });
      }
      $$("details.rte-pop", bar).forEach(function (d) {
        d.addEventListener("toggle", function () { if (d.open) { remember(); closePops(d); } });
      });
      scope.on(document, "click", function (e) { if (!bar.contains(e.target)) { closePops(null); } });
      bar.addEventListener("keydown", function (e) { if (e.key === "Escape") { closePops(null); restore(); } });

      function run(cmd, val, pic, name) {
        restore();
        try { document.execCommand("styleWithCSS", false, true); } catch (err) { /* older browsers: font tags */ }
        if (cmd === "link") {
          var url = window.prompt(W("Link address"), "https://");
          url = url ? url.trim() : "";
          // Only web and mail links: never javascript: or data:.
          if (/^(https?:\/\/|mailto:)/i.test(url)) { document.execCommand("createLink", false, url); }
        } else if (cmd === "quote") {
          document.execCommand("formatBlock", false, "blockquote");
        } else if (cmd === "clear") {
          document.execCommand("removeFormat");
          document.execCommand("unlink");
          document.execCommand("formatBlock", false, "div");
          var b = currentBlock();
          if (b) { b.style.backgroundColor = ""; }
        } else if (cmd === "mailBg") {
          setMailBg(val, pic);
          var wrapNow = mailBgWrap();
          if (wrapNow && name) { wrapNow.setAttribute("data-name", name); }
        } else if (cmd === "blockBg") {
          var block = currentBlock();
          if (!block) {
            document.execCommand("formatBlock", false, "div");
            block = currentBlock();
          }
          if (block) {
            block.style.backgroundColor = val || "";
            block.style.padding = val ? "6px 10px" : "";
            block.style.borderRadius = val ? "6px" : "";
          }
        } else if (cmd === "insertText") {
          document.execCommand("insertText", false, val);
        } else if (val !== null && val !== undefined) {
          document.execCommand(cmd, false, val);
        } else {
          document.execCommand(cmd);
        }
        remember();
        sync();
        reflect();
      }
      bar.addEventListener("click", function (e) {
        var b = e.target.closest("button[data-cmd]");
        if (!b) { return; }
        e.preventDefault();
        var pop = b.closest("details.rte-pop");
        run(b.getAttribute("data-cmd"), b.hasAttribute("data-val") ? b.getAttribute("data-val") : null, b.getAttribute("data-pic"), b.getAttribute("data-name"));
        if (pop) {
          pop.open = false;
          var now = pop.querySelector("[data-swatch-now]");
          if (now && b.getAttribute("data-cmd") === "foreColor") { now.setAttribute("fill", b.getAttribute("data-val")); }
        }
      });
      if (size) {
        size.addEventListener("mousedown", remember);
        size.addEventListener("change", function () { run("fontSize", size.value); });
      }
      paintEditorBg();
      html.value = editor.innerHTML;
      area.value = editor.innerText;
    });
  });

  /* ---------- 5c. Attachment size, checked before uploading ----------
     A file over the limit would otherwise be sent in full, only for the
     connection to be cut once the server's request limit is reached, with
     nothing shown to the user (DEF-010). The server still checks. */
  page(function attachmentLimit() {
    var LIMIT = 18 * 1024 * 1024;
    var input = $("input[type='file'][name='attachments']");
    var notice = $("[data-client-error]");
    if (!input || !input.form) { return; }
    input.form.addEventListener("submit", function (e) {
      var total = 0;
      Array.prototype.forEach.call(input.files || [], function (f) { total += f.size; });
      if (total > LIMIT) {
        e.preventDefault();
        if (notice) {
          notice.textContent = W("The attachments add up to more than 18 MB. Remove some, or send them in more than one message.");
          notice.hidden = false;
          notice.scrollIntoView({ block: "nearest" });
        }
      }
    });
  });

  /* ---------- 5b. Auto-submitting selects ----------
     A select marked data-autosubmit submits its form on change. Kept here
     rather than inline so the content security policy can forbid inline
     script entirely; without the script the form's own button submits. */
  // Kept with the other choices above (5b): a select there is bound once.

  /* ---------- 6. Client-side form checks ----------
     A courtesy only: the server validates every field again and returns
     the same banner, so nothing depends on this running. */
  page(function checks() {
    var form = $("form[data-compose]");
    if (!form) { return; }
    form.addEventListener("submit", function (e) {
      if (form.getAttribute("data-skip-validate") === "1") { return; }
      // Save draft and Discard keep half-typed addresses as they are.
      if (e.submitter && e.submitter.hasAttribute("formnovalidate")) { return; }
      var to = form.querySelector("input[name='to']");
      if (!to) { return; }
      var bad = to.value.split(",").map(function (s) { return s.trim(); })
        .filter(function (s) { return s.length > 0; })
        .filter(function (s) {
          var at = s.lastIndexOf("@");
          var domain = at < 0 ? "" : s.slice(at + 1).replace(/>$/, "");
          return at <= 0 || domain.indexOf(".") < 1;
        });
      if (!bad.length) { return; }
      e.preventDefault();
      var banner = $("[data-client-error]");
      if (banner) {
        banner.hidden = false;
        banner.textContent = W("Not sent. {address} is not a valid mailbox address. Correct it and press Send again.").replace("{address}", bad[0]);
        banner.scrollIntoView({ block: "nearest" });
      } else {
        form.setAttribute("data-skip-validate", "1");
        form.submit();
      }
    });
  });

  /* ---------- 6b. Keep this draft? (rc.12, item 17) ----------
     Leaving a message after writing in it - Cancel, Close, a folder in the
     rail, another message - opens the page's own dialog: Save draft,
     Discard (removes the draft autosave may have made) or Keep editing; the
     first two then go where the person was going. Nothing changed since the
     page opened: it simply leaves. */
  page(function keepDraft() {
    var form = $("form[data-compose]");
    var dialog = form ? $("dialog[data-keepdraft]", form) : null;
    if (!form || !dialog || typeof dialog.showModal !== "function") { return; }
    var next = $("input[data-next]", form);
    function snapshot() {
      var parts = [];
      ["to", "cc", "bcc", "subject"].forEach(function (n) {
        var f = form.elements.namedItem(n);
        parts.push(f && "value" in f ? f.value.trim() : "");
      });
      var ed = form.querySelector("[data-rte-editor]");
      var area = form.querySelector("textarea[name='body']");
      parts.push(ed ? ed.innerHTML : (area ? area.value : ""));
      var files = form.querySelector("input[type='file'][name='attachments']");
      parts.push(files && files.files ? String(files.files.length) : "0");
      return parts.join("\u0001");
    }
    var start = snapshot();
    var leaving = false;
    function changed() { return !leaving && !window.anjalLeaving && snapshot() !== start; }
    function ask(href) {
      if (next) { next.value = href || ""; }
      dialog.showModal();
    }
    scope.on(document, "click", function (e) {
      var a = e.target.closest ? e.target.closest("a[href]") : null;
      if (!a || dialog.contains(a) || e.defaultPrevented || e.ctrlKey || e.metaKey || e.shiftKey) { return; }
      var href = a.getAttribute("href") || "";
      if (href.charAt(0) === "#" || a.target === "_blank" || a.hasAttribute("download") || form.contains(a) && !a.hasAttribute("data-cancel")) { return; }
      if (!changed()) { return; }
      e.preventDefault();
      ask(href);
    }, true);
    var keep = $("[data-keepediting]", dialog);
    if (keep) { keep.addEventListener("click", function () { dialog.close(); }); }
    form.addEventListener("submit", function () { leaving = true; });
    var discard = $("[data-discard]", dialog);
    if (discard) {
      discard.addEventListener("click", function () {
        try {
          Object.keys(window.sessionStorage).forEach(function (k) {
            if (k.indexOf("anjal-writing:") === 0) { window.sessionStorage.removeItem(k); }
          });
        } catch (err) { /* storage off */ }
      });
    }
    // Closing the tab or reloading with unsent text: the browser's own question.
    scope.on(window, "beforeunload", function (e) {
      if (changed()) { e.preventDefault(); e.returnValue = ""; }
    });
  });

  /* ---------- rc.13 Accounts: sign-in, two-step, passkeys, idle sign-out ----------
     Each part attaches to markup the server already rendered and that works
     without this file: the six code boxes are six fields, the password rules
     are a list, the approval page has its own button, and an idle session is
     signed out by the server whether or not this file runs. */

  // Six boxes for a code: typing moves on, Backspace moves back, a pasted code fills them all.
  page(function codeBoxes() {
  $$("[data-codeboxes]").forEach(function (group) {
    var boxes = $$("input.codebox", group);
    var auto = group.getAttribute("data-autosubmit") === "1";
    function full() { return boxes.every(function (b) { return /^[0-9]$/.test(b.value); }); }
    function fill(text, from) {
      var digits = (text || "").replace(/\D/g, "").split("");
      for (var i = from; i < boxes.length && digits.length; i++) { boxes[i].value = digits.shift(); }
      var next = boxes.filter(function (b) { return !b.value; })[0];
      (next || boxes[boxes.length - 1]).focus();
      if (auto && full()) { var f = group.closest("form"); if (f && f.requestSubmit) { f.requestSubmit(); } }
    }
    boxes.forEach(function (box, i) {
      box.addEventListener("input", function () {
        var v = box.value.replace(/\D/g, "");
        if (v.length > 1) { box.value = ""; fill(v, i); return; }
        box.value = v;
        if (v && i < boxes.length - 1) { boxes[i + 1].focus(); }
        if (v && auto && full()) { var f = group.closest("form"); if (f && f.requestSubmit) { f.requestSubmit(); } }
      });
      box.addEventListener("keydown", function (e) {
        if (e.key === "Backspace" && !box.value && i > 0) { boxes[i - 1].focus(); boxes[i - 1].value = ""; e.preventDefault(); }
        if (e.key === "ArrowLeft" && i > 0) { boxes[i - 1].focus(); e.preventDefault(); }
        if (e.key === "ArrowRight" && i < boxes.length - 1) { boxes[i + 1].focus(); e.preventDefault(); }
      });
      box.addEventListener("paste", function (e) {
        var text = (e.clipboardData || window.clipboardData).getData("text");
        if (text) { e.preventDefault(); fill(text, i); }
      });
      box.addEventListener("focus", function () { box.select(); });
    });
  });
  });

  // Owner, 10 Oct 2026 (P7; NIST SP 800-63B-4, OWASP ASVS): an eye beside every password box shows
  // what was typed, and hides it again. The button comes from the page's own template (words and icons).
  page(function passwordEyes() {
  var tp = $("template[data-pwreveal]");
  if (!tp) { return; }
  $$("input[type='password']:not([data-pweye])").forEach(function (input) {
    if (input.hidden || input.closest("template")) { return; }
    input.setAttribute("data-pweye", "");
    var btn = tp.content.firstElementChild.cloneNode(true);
    var box = input.parentElement && input.parentElement.classList.contains("authinpwrap") ? input.parentElement : null;
    if (!box) {
      box = document.createElement("span");
      box.className = "pwwrap";
      input.parentNode.insertBefore(box, input);
      box.appendChild(input);
    }
    box.appendChild(btn);
    btn.addEventListener("click", function () {
      var show = input.type === "password";
      input.type = show ? "text" : "password";
      btn.setAttribute("aria-pressed", show ? "true" : "false");
      var word = btn.getAttribute(show ? "data-hide" : "data-show");
      btn.setAttribute("aria-label", word);
      btn.setAttribute("title", word);
      input.focus();
    });
    // Hidden again before the form is sent, so the browser keeps treating it as a password.
    if (input.form) { input.form.addEventListener("submit", function () { input.type = "password"; }); }
  });
  });

  // The password rules, ticked as the person types: the server judges, so the list says what it will.
  page(function passwordRules() {
  $$("[data-pwrules]").forEach(function (list) {
    var input = document.getElementById(list.getAttribute("data-for"));
    var form = input ? input.closest("form") : null;
    if (!input || !form) { return; }
    var timer = null;
    var asked = 0;
    function paint(r) {
      $$("[data-rule]", list).forEach(function (li) {
        var key = li.getAttribute("data-rule");
        if (!(key in r)) { return; }
        li.classList.toggle("ok", !!r[key] && input.value.length > 0);
        li.classList.toggle("bad", !r[key] && input.value.length > 0);
      });
    }
    function check() {
      if (!input.value) { $$("[data-rule]", list).forEach(function (li) { if (li.getAttribute("data-rule") !== "history") { li.classList.remove("ok", "bad"); } }); return; }
      var body = new FormData();
      var token = form.querySelector("input[name='__RequestVerificationToken']");
      if (token) { body.set("__RequestVerificationToken", token.value); }
      body.set("password", input.value);
      var ticket = form.querySelector("[data-ticket]");
      if (ticket) { body.set("t", ticket.value); }
      var invite = form.querySelector("[data-invite]");
      if (invite) { body.set("token", invite.value); }
      var mine = ++asked;
      fetch("/auth/password-check", { method: "POST", body: body, headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (r) { if (r && mine === asked) { paint(r); } })
        .catch(function () { /* the server says on saving */ });
    }
    input.addEventListener("input", function () {
      var history = $("[data-rule='history']", list);
      if (history) { history.classList.remove("bad"); }
      window.clearTimeout(timer);
      timer = window.setTimeout(check, 250);
    });
    if (input.value) { check(); }
  });
  });

  // Sign-in: "Forgot password?" carries the user name typed; the domain after it hides when a whole address is typed.
  (function signIn() {
    var form = $("form[data-signin]");
    if (!form) { return; }
    var user = form.querySelector("#address");
    var forgot = $("[data-forgot]", form);
    var suffix = $("[data-suffix]", form);
    function sync() {
      if (forgot) { forgot.href = "/sign-in/forgot" + (user.value ? "?u=" + encodeURIComponent(user.value.trim()) : ""); }
      if (suffix) { suffix.hidden = user.value.indexOf("@") >= 0; }
    }
    user.addEventListener("input", sync);
    sync();
  }());

  // Waiting for another device to approve: ask every two seconds, then carry on by itself.
  page(function approvalWait() {
  $$("[data-approval-wait]").forEach(function (box) {
    var url = box.getAttribute("data-status");
    var formId = box.getAttribute("data-approved-form");
    var tries = 0;
    function say(word) {
      var p = $("p", box);
      if (p) { p.textContent = box.getAttribute(word) || ""; }
      box.classList.add("ended");
    }
    function poll() {
      tries++;
      fetch(url, { cache: "no-store", headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : { state: "waiting" }; })
        .then(function (d) {
          if (d.state === "approved") {
            var f = formId ? document.getElementById(formId) : null;
            if (f) { f.submit(); } else { window.location.reload(); }
            return;
          }
          if (d.state === "refused") { say("data-word-refused"); return; }
          if (d.state === "expired" || tries > 160) { say("data-word-expired"); return; }
          window.setTimeout(poll, 2000);
        })
        .catch(function () { window.setTimeout(poll, 4000); });
    }
    window.setTimeout(poll, 1500);
  });
  });

  // Passkeys: base64url both ways, as the server reads and writes them.
  function fromB64(s) {
    s = s.replace(/-/g, "+").replace(/_/g, "/");
    while (s.length % 4) { s += "="; }
    var bin = window.atob(s);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
    return out.buffer;
  }
  function toB64(buf) {
    var bytes = new Uint8Array(buf);
    var bin = "";
    for (var i = 0; i < bytes.length; i++) { bin += String.fromCharCode(bytes[i]); }
    return window.btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }
  var passkeysWork = !!(window.PublicKeyCredential && navigator.credentials && window.isSecureContext);
  function passkeyError(text) {
    $$("[data-passkey-error]").forEach(function (box) {
      var span = $("span", box);
      if (span) { span.textContent = text ? W(text) : text; }
      box.hidden = !text;
    });
  }
  function post(url, form, extra) {
    var body = new FormData();
    var token = form ? form.querySelector("input[name='__RequestVerificationToken']") : $("input[name='__RequestVerificationToken']");
    if (token) { body.set("__RequestVerificationToken", token.value); }
    Object.keys(extra || {}).forEach(function (k) { body.set(k, extra[k]); });
    return fetch(url, { method: "POST", body: body, headers: { "X-Requested-With": "anjal" } })
      .then(function (r) { return r.json().catch(function () { return { error: W("The server did not answer. Try again.") }; }); });
  }

  // Second step with a passkey.
  page(function passkeySignIn() {
  $$("[data-passkey-signin]").forEach(function (form) {
    var go = $("[data-passkey-go]", form);
    if (!go) { return; }
    if (!passkeysWork) { go.disabled = true; return; }
    // DES-11 S4: the same steps confirm a signed-in person ("Confirm it is you"), at the form's own addresses.
    var optionsUrl = form.getAttribute("data-passkey-options") || "/auth/passkey/options";
    var verifyUrl = form.getAttribute("data-passkey-verify") || "/auth/passkey/verify";
    var nextField = form.querySelector("input[name='next']");
    go.addEventListener("click", function () {
      passkeyError("");
      post(optionsUrl, form).then(function (o) {
        if (o.error) { passkeyError(o.error); return null; }
        return navigator.credentials.get({ publicKey: {
          challenge: fromB64(o.challenge), rpId: o.rpId, timeout: 60000, userVerification: "preferred",
          allowCredentials: o.allow.map(function (id) { return { type: "public-key", id: fromB64(id) }; })
        } }).then(function (cred) {
          return post(verifyUrl, form, {
            next: nextField ? nextField.value : "",
            challengeId: o.challengeId, id: cred.id,
            clientDataJSON: toB64(cred.response.clientDataJSON),
            authenticatorData: toB64(cred.response.authenticatorData),
            signature: toB64(cred.response.signature)
          });
        });
      }).then(function (r) {
        if (!r) { return; }
        if (r.error) { passkeyError(r.error); if (r.next) { window.setTimeout(function () { window.location.href = r.next; }, 1800); } return; }
        window.location.href = r.next || "/folder/INBOX";
      }).catch(function (e) {
        passkeyError(e && e.name === "NotAllowedError" ? "The passkey was not used. Try again, or use another way." : "That did not work. Try again, or use another way.");
      });
    });
  });
  });

  // Adding a passkey in Security settings; the button shows only where passkeys work.
  page(function passkeyAdd() {
  $$("[data-passkey-add]").forEach(function (btn) {
    if (!passkeysWork) { return; }
    btn.hidden = false;
    btn.addEventListener("click", function () {
      passkeyError("");
      post("/settings/security/passkey/options").then(function (o) {
        // DES-11 S4: not confirmed in the last five minutes - off to "Confirm it is you", then back here.
        if (o.error && o.next) { window.location.href = o.next; return null; }
        if (o.error) { passkeyError(o.error); return null; }
        return navigator.credentials.create({ publicKey: {
          challenge: fromB64(o.challenge), rp: o.rp,
          user: { id: fromB64(o.user.id), name: o.user.name, displayName: o.user.displayName },
          pubKeyCredParams: [{ type: "public-key", alg: -7 }, { type: "public-key", alg: -257 }],
          timeout: 60000, attestation: "none",
          authenticatorSelection: { residentKey: "preferred", userVerification: "preferred" },
          excludeCredentials: o.exclude.map(function (id) { return { type: "public-key", id: fromB64(id) }; })
        } }).then(function (cred) {
          return post("/settings/security/passkey/add", null, {
            challengeId: o.challengeId,
            clientDataJSON: toB64(cred.response.clientDataJSON),
            attestationObject: toB64(cred.response.attestationObject)
          });
        });
      }).then(function (r) {
        if (!r) { return; }
        if (r.error) { passkeyError(r.error); return; }
        window.location.href = r.next || "/settings/security";
      }).catch(function (e) {
        passkeyError(e && e.name === "InvalidStateError" ? "This device's passkey is already added." : "No passkey was added. Try again.");
      });
    });
  });
  });

  page(function printButtons() {
  $$("[data-print]").forEach(function (b) { b.addEventListener("click", function () { window.print(); }); });
  });

  // On a phone the Settings sections are a strip; the one open is brought into view.
  page(function settingsStrip() {
    var on = $(".setnav3 .setnav-item.on");
    var nav = on ? on.closest(".setnav3") : null;
    if (nav && nav.scrollWidth > nav.clientWidth) { nav.scrollLeft = on.offsetLeft - (nav.clientWidth - on.offsetWidth) / 2; }
  });

  /* Idle sign-out (board AuthIdle): the server keeps the clock; this page tells
     it when the person types or clicks (at most once a minute), asks how long
     is left, and a minute before the end shows the warning. Unsent text is
     saved as a draft before signing out. Other devices' approval requests
     arrive with the same check. */
  page(function idle() {
    var bar = $("[data-statusbar]");
    var wrap = $("[data-idle]");
    var outForm = $("form[data-idle-form]");
    if (!bar || !wrap || !outForm) { return; }
    var title = $("[data-idle-title]", wrap);
    var word = title ? title.getAttribute("data-word") : "Signing out in {time}";
    var left = parseInt(bar.getAttribute("data-idle-limit") || "900", 10);
    var lastPing = Date.now();
    var active = false;
    var countdown = null;
    var shown = window.anjalApprovalsShown || (window.anjalApprovalsShown = {});
    var tpl = $("template[data-approval-template]");

    function token() { var t = outForm.querySelector("input[name='__RequestVerificationToken']"); return t ? t.value : ""; }
    function ping() {
      active = false;
      lastPing = Date.now();
      var body = new FormData();
      body.set("__RequestVerificationToken", token());
      return fetch("/auth/alive", { method: "POST", body: body, headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (d) { if (d && typeof d.left === "number") { left = d.left; } });
    }
    ["keydown", "pointerdown", "wheel", "touchstart"].forEach(function (ev) {
      scope.on(document, ev, function () {
        active = true;
        if (Date.now() - lastPing > 60000 && wrap.hidden) { ping(); }
      }, { passive: true, capture: true });
    });

    function fmt(s) { s = Math.max(0, s); return Math.floor(s / 60) + ":" + ("0" + (s % 60)).slice(-2); }
    function signOut(reason) {
      window.anjalLeaving = true;
      var reasonField = outForm.querySelector("[data-idle-reason]");
      if (reasonField) { reasonField.value = reason; }
      var compose = $("form[data-compose]");
      var done = function () { outForm.submit(); };
      if (compose && reason === "idle") {
        var body = new FormData(compose);
        body.set("autosave", "1");
        body.delete("attachments");
        var has = ["to", "subject", "body"].some(function (n) { var v = body.get(n); return v && String(v).trim(); });
        if (has) {
          fetch("/draft", { method: "POST", body: body, headers: { "X-Requested-With": "anjal" } }).then(done, done);
          return;
        }
      }
      done();
    }
    function hide() {
      wrap.hidden = true;
      if (countdown) { window.clearInterval(countdown); countdown = null; }
    }
    function show() {
      if (!wrap.hidden) { return; }
      wrap.hidden = false;
      var stay = $("[data-idle-stay]", wrap);
      if (stay) { stay.focus(); }
      countdown = scope.every(function () {
        left -= 1;
        if (title) { title.textContent = word.replace("{time}", fmt(left)); }
        if (left <= 0) { hide(); signOut("idle"); }
      }, 1000);
      if (title) { title.textContent = word.replace("{time}", fmt(left)); }
    }
    var stayBtn = $("[data-idle-stay]", wrap);
    if (stayBtn) { stayBtn.addEventListener("click", function () { hide(); ping(); }); }
    var outBtn = $("[data-idle-out]", wrap);
    if (outBtn) { outBtn.addEventListener("click", function () { signOut("person"); }); }

    function approvals(list) {
      if (!tpl || !list) { return; }
      list.forEach(function (a) {
        if (shown[a.id]) { return; }
        shown[a.id] = true;
        var node = tpl.content.firstElementChild.cloneNode(true);
        var text = $("[data-approve-text]", node);
        if (text) {
          text.textContent = (text.getAttribute(a.kind === "reset" ? "data-word-reset" : "data-word-signin") || "")
            .replace("{device}", a.device).replace("{address}", a.address);
        }
        $$("[data-approve]", node).forEach(function (b) {
          b.addEventListener("click", function () {
            var body = new FormData();
            body.set("__RequestVerificationToken", token());
            body.set("answer", b.getAttribute("data-approve"));
            fetch("/auth/approvals/" + encodeURIComponent(a.id), { method: "POST", body: body, headers: { "X-Requested-With": "anjal" } })
              .finally(function () { node.classList.add("gone"); window.setTimeout(function () { node.remove(); }, 400); });
          });
        });
        document.body.appendChild(node);
      });
    }

    // Once the page is going somewhere (a form sent, a link followed), the
    // check stands aside, so it never races a sign-out or any other navigation.
    var going = false;
    scope.on(document, "submit", function () { going = true; }, true);
    scope.on(window, "beforeunload", function () { going = true; });
    scope.on(window, "pageshow", function () { going = false; });

    function check() {
      if (going) { return; }
      if (active && Date.now() - lastPing > 60000 && wrap.hidden) { ping(); }
      fetch("/auth/alive", { cache: "no-store", headers: { "X-Requested-With": "anjal" } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (d) {
          if (!d || going) { return; }
          if (d.left <= 0) { window.location.reload(); return; }
          if (wrap.hidden) { left = d.left; }
          if (d.left <= 60) { show(); } else if (!wrap.hidden && d.left > 60) { hide(); }
          approvals(d.approvals);
        })
        .catch(function () { /* offline: the server decides when it is back */ });
    }
    scope.every(check, 15000);
    window.setTimeout(check, 2000);
    scope.on(document, "visibilitychange", function () { if (!document.hidden) { check(); } });
  });

  /* v1.0.0-rc.10 (DEF-078): a page the browser restores from its
     back-forward cache shows what it showed when it was left - a message
     just read would still look unread. Reload it instead. */
  window.addEventListener('pageshow', function (event) {
    if (event.persisted) {
      window.location.reload();
    }
  });
}());
