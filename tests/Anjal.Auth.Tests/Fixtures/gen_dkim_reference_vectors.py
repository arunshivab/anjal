"""Generates DKIM reference vectors for Anjal's verifier with dkimpy, an
independent RFC 6376 implementation. Every expected result below is what
dkimpy itself reports, not what Anjal's own signer produces (DEF-056).

Requires: pip install dkimpy cryptography. Keys are generated afresh on
each run, so re-running produces different (equally valid) vectors; paste
the output into DkimReferenceVectorTests.cs. The second half of that test
file (Anjal's signer, DEF-058) was produced by signing with Anjal.Dkim under
a fixed key and clock and verifying the result with dkimpy's DKIM(...).verify.
"""
import dkim, base64, json
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives import serialization

def key():
    k = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    pem = k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    pub = k.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    return pem, base64.b64encode(pub).decode()

K = {n: key() for n in ("sender", "relay", "relay_wrong")}
DNS = {b"s1._domainkey.sender.test.": ("v=DKIM1; k=rsa; p=" + K["sender"][1]).encode(),
       b"r1._domainkey.relay.test.": ("v=DKIM1; k=rsa; p=" + K["relay"][1]).encode()}
def dnsfunc(name, timeout=5): return DNS.get(name if name.endswith(b".") else name + b".", b"")

def msg(extra_top=b"", subject=b"Anjal inbound test 1"):
    return (extra_top +
        b"MIME-Version: 1.0\r\n"
        b"Date: Sat, 26 Sep 2026 12:01:22 +0530\r\n"
        b"Message-ID: <CAF-test-0001@mail.sender.test>\r\n"
        b"Subject: " + subject + b"\r\n"
        b"From: Arun Tester <arun@sender.test>\r\n"
        b"To: arun@anjal.co.in\r\n"
        b"Content-Type: text/plain; charset=\"UTF-8\"\r\n"
        b"\r\n"
        b"Hello from the reference signer.\r\n"
        b"\r\n"
        b"Second paragraph, trailing spaces here   \r\n")

GMAIL_H = [b"content-type", b"to", b"subject", b"message-id", b"date", b"from", b"mime-version",
           b"from", b"to", b"cc", b"subject", b"date", b"message-id", b"reply-to", b"content-type"]
def sign(m, who, sel, h, canon=(b"relaxed", b"relaxed")):
    dom = b"sender.test" if who == "sender" else b"relay.test"
    return dkim.sign(m, sel, dom, K[who][0], include_headers=h, canonicalize=canon) + m

V = {}
# 1. Gmail-shaped over-signing, relaxed/relaxed.
V["gmail_oversigned"] = sign(msg(), "sender", b"s1", GMAIL_H)
# 2. simple/simple: header names in h= lower case, in the message capitalised; one folded header.
m2 = msg(subject=b"A subject long enough\r\n to be folded onto a second line")
V["simple_simple"] = sign(m2, "sender", b"s1", [b"from", b"to", b"subject", b"date"], (b"simple", b"simple"))
# 3. A header that occurs twice, signed twice: instances must be taken from the bottom up.
m3 = msg(extra_top=b"X-Tag: first\r\nX-Tag: second\r\n")
V["repeated_bottom_up"] = sign(m3, "sender", b"s1", [b"from", b"x-tag", b"x-tag", b"subject"])
# 4. Two signatures, the top one broken (its key in DNS is a different key): the second must count.
inner = sign(msg(), "sender", b"s1", GMAIL_H)
V["two_sigs_top_broken"] = sign(inner, "relay_wrong", b"r1", [b"from", b"to", b"subject"])
# 5. Two valid signatures, the top one by a relay (not aligned with From): the aligned one should be reported.
V["two_sigs_top_unaligned"] = sign(inner, "relay", b"r1", [b"from", b"to", b"subject"])
# 6. Negative: over-signed message whose Subject was changed after signing.
V["tampered_subject"] = V["gmail_oversigned"].replace(b"Subject: Anjal inbound test 1", b"Subject: Anjal inbound test 2")
# 7. Negative: a second From added above an over-signed message (what over-signing exists to catch).
V["injected_from"] = b"From: Attacker <boss@sender.test>\r\n" + V["gmail_oversigned"]

# dkimpy's own verdicts are the expected results.
def verify_all(m):
    d = dkim.DKIM(m); n = sum(1 for (k, _) in d.headers if k.lower() == b"dkim-signature")
    return [d.verify(idx=i, dnsfunc=dnsfunc) for i in range(n)]
out = {"sender_key": K["sender"][1], "relay_key": K["relay"][1], "vectors": {}}
for name, m in V.items():
    r = verify_all(m)
    out["vectors"][name] = {"b64": base64.b64encode(m).decode(), "dkimpy": r}
    print(f"{name:<24} dkimpy per signature (top first): {r}")
json.dump(out, open("vectors.json", "w"), indent=1)
