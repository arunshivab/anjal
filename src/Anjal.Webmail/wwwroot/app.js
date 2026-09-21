/* Anjal webmail enhancements.
   Every page works with this file absent. Each block below attaches to
   markup that already functions; none of them builds a control it then
   operates. No framework, no build step.

   1. Connectivity light   2. Draft autosave        3. Address suggestions
   4. Mark all read        5. Keyboard shortcuts    6. Client-side form checks */
(function () {
  "use strict";

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  /* ---------- 1. Connectivity light ----------
     The element is rendered empty by the server and only filled in here,
     so a page without this script never shows a light that cannot tell
     the truth. */
  (function connectivity() {
    var host = $("[data-connectivity]");
    if (!host) { return; }
    var timer = null;

    function paint(online) {
      host.className = "lite" + (online ? "" : " off");
      host.innerHTML = "";
      var dot = document.createElement("i");
      host.appendChild(dot);
      host.appendChild(document.createTextNode(online ? "Connected" : "Offline"));
      host.setAttribute("title", online
        ? "Anjal is reachable."
        : "Anjal is not reachable from this browser. Anything you send will fail until it returns.");
    }

    function check() {
      if (document.hidden) { return; }
      fetch("/api/ping", { method: "GET", cache: "no-store" })
        .then(function (r) { paint(r.ok); })
        .catch(function () { paint(false); });
    }

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
  (function autosave() {
    var form = $("form[data-compose]");
    if (!form) { return; }
    var state = $("[data-draft-state]");
    var idField = form.querySelector("input[name='draftId']");
    if (!state || !idField) { return; }

    var dirty = false;
    var saving = false;
    var timer = null;

    state.textContent = "Not saved yet";

    function stamp() {
      var d = new Date();
      return ("0" + d.getHours()).slice(-2) + ":" + ("0" + d.getMinutes()).slice(-2);
    }

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
          state.appendChild(document.createTextNode("Saved as draft \u00b7 " + stamp()));
        })
        .catch(function () { saving = false; });
    }

    form.addEventListener("input", function () {
      dirty = true;
      if (timer) { window.clearTimeout(timer); }
      timer = window.setTimeout(save, 20000);
    });
    form.addEventListener("submit", function () {
      if (timer) { window.clearTimeout(timer); }
      dirty = false;
    });
  }());

  /* ---------- 3. Address suggestions ----------
     Turns the plain To/Cc/Bcc inputs into combo boxes reading from
     /api/contacts. Without the script they are ordinary text fields and
     the caption under them says so. */
  (function suggestions() {
    $$("[data-suggest]").forEach(function (input) {
      var wrap = document.createElement("span");
      wrap.className = "suggest";
      input.parentNode.insertBefore(wrap, input);
      wrap.appendChild(input);

      var list = document.createElement("ul");
      list.setAttribute("role", "listbox");
      list.hidden = true;
      wrap.appendChild(list);

      var items = [];
      var active = -1;
      var timer = null;

      input.setAttribute("autocomplete", "off");
      input.setAttribute("role", "combobox");
      input.setAttribute("aria-expanded", "false");

      function current() {
        var parts = input.value.split(",");
        return parts[parts.length - 1].trim();
      }

      function close() {
        list.hidden = true;
        list.innerHTML = "";
        items = [];
        active = -1;
        input.setAttribute("aria-expanded", "false");
      }

      function choose(index) {
        if (index < 0 || index >= items.length) { return; }
        var parts = input.value.split(",");
        parts[parts.length - 1] = " " + items[index].address;
        input.value = parts.join(",").replace(/^\s+/, "") + ", ";
        close();
        input.focus();
      }

      function paint(data) {
        list.innerHTML = "";
        items = data;
        active = -1;
        if (!data.length) { close(); return; }
        data.forEach(function (c, i) {
          var li = document.createElement("li");
          li.setAttribute("role", "option");
          li.textContent = c.name || c.address;
          if (c.name) {
            var small = document.createElement("span");
            small.className = "a";
            small.textContent = c.address;
            li.appendChild(small);
          }
          li.addEventListener("mousedown", function (e) { e.preventDefault(); choose(i); });
          list.appendChild(li);
        });
        list.hidden = false;
        input.setAttribute("aria-expanded", "true");
      }

      function highlight() {
        $$("li", list).forEach(function (li, i) {
          li.setAttribute("aria-selected", i === active ? "true" : "false");
        });
      }

      function query() {
        var q = current();
        if (q.length < 1) { close(); return; }
        fetch("/api/contacts?q=" + encodeURIComponent(q), { headers: { "X-Requested-With": "anjal" } })
          .then(function (r) { return r.ok ? r.json() : []; })
          .then(paint)
          .catch(close);
      }

      input.addEventListener("input", function () {
        if (timer) { window.clearTimeout(timer); }
        timer = window.setTimeout(query, 150);
      });
      input.addEventListener("keydown", function (e) {
        if (list.hidden) { return; }
        if (e.key === "ArrowDown") { e.preventDefault(); active = Math.min(active + 1, items.length - 1); highlight(); }
        else if (e.key === "ArrowUp") { e.preventDefault(); active = Math.max(active - 1, 0); highlight(); }
        else if (e.key === "Enter" && active >= 0) { e.preventDefault(); choose(active); }
        else if (e.key === "Escape") { close(); }
      });
      input.addEventListener("blur", function () { window.setTimeout(close, 120); });
    });
  }());

  /* ---------- 4. Selection ----------
     The bulk buttons act on ticked rows, so they are disabled until
     something is ticked, and the select-all box ticks the page. Without
     the script every button stays enabled and submitting with nothing
     ticked returns the page unchanged - the server already handles it. */
  (function selection() {
    var form = $("form[data-bulkform]");
    if (!form) { return; }
    var boxes = $$("input[data-rowcheck]", form);
    var actions = $$("[data-bulkaction]", form);
    var all = $("input[data-selectall]", form);
    var label = $("[data-bulklabel]", form);
    if (!boxes.length) { return; }

    function sync() {
      var n = boxes.filter(function (b) { return b.checked; }).length;
      actions.forEach(function (b) { b.disabled = n === 0; });
      if (label) {
        label.textContent = n === 0 ? "With selected:" : n + (n === 1 ? " selected:" : " selected:");
        label.classList.toggle("armed", n > 0);
      }
      if (all) {
        all.checked = n === boxes.length && n > 0;
        all.indeterminate = n > 0 && n < boxes.length;
      }
    }

    boxes.forEach(function (b) { b.addEventListener("change", sync); });
    if (all) {
      all.addEventListener("change", function () {
        boxes.forEach(function (b) { b.checked = all.checked; });
        sync();
      });
    }
    sync();
  }());

  /* ---------- 4b. Mark all read ----------
     Posts in the background and greys the unread dots in place. Without
     the script it is an ordinary submit and the page reloads. */
  (function markAllRead() {
    var button = $("[data-markallread-btn]");
    var form = $("form[data-bulkform]");
    if (!button || !form) { return; }
    button.addEventListener("click", function (e) {
      e.preventDefault();
      var body = new FormData();
      var token = form.querySelector("input[name='__RequestVerificationToken']");
      if (token) { body.set("__RequestVerificationToken", token.value); }
      fetch(button.getAttribute("formaction"), {
        method: "POST", body: body, headers: { "X-Requested-With": "anjal" }
      })
        .then(function (r) {
          if (!r.ok) { window.location.reload(); return; }
          $$("tr.unread").forEach(function (tr) { tr.classList.remove("unread"); });
          $$(".udot").forEach(function (d) { d.remove(); });
          var badge = $("[data-unread-badge]");
          if (badge) { badge.textContent = ""; }
        })
        .catch(function () { window.location.reload(); });
    });
  }());

  /* ---------- 5. Keyboard shortcuts ----------
     j / k move, Enter opens, r replies, # trashes. The hint line is
     rendered hidden and revealed here, so nothing advertises a shortcut
     that will not fire. */
  (function shortcuts() {
    var rows = $$("tr[data-message]");
    var hint = $("[data-kbhint]");
    if (!rows.length) { return; }
    if (hint) { hint.hidden = false; }
    var index = -1;

    function focusRow(i) {
      if (i < 0 || i >= rows.length) { return; }
      index = i;
      rows.forEach(function (r) { r.classList.remove("kbfocus"); });
      rows[i].classList.add("kbfocus");
      rows[i].scrollIntoView({ block: "nearest" });
    }

    document.addEventListener("keydown", function (e) {
      var tag = (e.target.tagName || "").toLowerCase();
      if (tag === "input" || tag === "textarea" || tag === "select" || e.metaKey || e.ctrlKey || e.altKey) { return; }
      if (e.key === "j") { e.preventDefault(); focusRow(index + 1); }
      else if (e.key === "k") { e.preventDefault(); focusRow(index - 1); }
      else if (e.key === "Enter" && index >= 0) {
        var link = rows[index].querySelector("a[data-open]");
        if (link) { e.preventDefault(); window.location.href = link.href; }
      } else if (e.key === "r" && index >= 0) {
        var reply = rows[index].getAttribute("data-reply");
        if (reply) { e.preventDefault(); window.location.href = reply; }
      } else if (e.key === "#" && index >= 0) {
        var trash = rows[index].querySelector("form[data-trash]");
        if (trash) { e.preventDefault(); trash.submit(); }
      }
    });
  }());

  /* ---------- 5b. Auto-submitting selects ----------
     A select marked data-autosubmit submits its form on change. Kept here
     rather than inline so the content security policy can forbid inline
     script entirely; without the script the form's own button submits. */
  (function autosubmit() {
    $$("select[data-autosubmit]").forEach(function (sel) {
      sel.addEventListener("change", function () { if (sel.form) { sel.form.submit(); } });
    });
  }());

  /* ---------- 6. Client-side form checks ----------
     A courtesy only: the server validates every field again and returns
     the same banner, so nothing depends on this running. */
  (function checks() {
    var form = $("form[data-compose]");
    if (!form) { return; }
    form.addEventListener("submit", function (e) {
      if (form.getAttribute("data-skip-validate") === "1") { return; }
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
        banner.textContent = "Not sent. " + bad[0] + " is not a valid mailbox address. Correct it and press Send again.";
        banner.scrollIntoView({ block: "nearest" });
      } else {
        form.setAttribute("data-skip-validate", "1");
        form.submit();
      }
    });
  }());
}());
