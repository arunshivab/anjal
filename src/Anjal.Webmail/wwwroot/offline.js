/*
 * Anjal offline list (DES-11 D5): "Use my passkey" on the locked offline page.
 * Where the device's passkey can derive a secret (WebAuthn PRF), the list is
 * opened with it instead of the offline code. Nothing leaves the device.
 */
(function () {
  "use strict";
  var btn = document.querySelector("[data-offline-passkey]");
  if (!btn) { return; }
  if (!window.PublicKeyCredential || !navigator.credentials || !navigator.serviceWorker || !navigator.serviceWorker.controller) {
    btn.hidden = true;
    return;
  }
  function fromB64(s) {
    s = s.replace(/-/g, "+").replace(/_/g, "/");
    while (s.length % 4) { s += "="; }
    var bin = window.atob(s);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
    return out;
  }
  btn.addEventListener("click", function () {
    var ids = (btn.getAttribute("data-ids") || "").split(",").filter(Boolean);
    var salt = fromB64(btn.getAttribute("data-salt") || "");
    navigator.credentials.get({ publicKey: {
      challenge: crypto.getRandomValues(new Uint8Array(32)),
      allowCredentials: ids.map(function (id) { return { type: "public-key", id: fromB64(id) }; }),
      userVerification: "required",
      extensions: { prf: { eval: { first: salt } } }
    } }).then(function (cred) {
      var r = cred.getClientExtensionResults();
      var first = r && r.prf && r.prf.results && r.prf.results.first;
      if (!first) { throw new Error("no prf"); }
      var ch = new MessageChannel();
      ch.port1.onmessage = function (m) {
        if (m.data && m.data.result === "open") { window.location.href = "/folder/INBOX"; } else { window.location.reload(); }
      };
      navigator.serviceWorker.controller.postMessage({ kind: "passkey", secret: first }, [ch.port2]);
    }).catch(function () { btn.hidden = true; });
  });
}());
