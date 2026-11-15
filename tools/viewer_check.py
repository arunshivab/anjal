#!/usr/bin/env python3
"""rc.29 (DES-22 section 4): asks the running anjal-viewer service to read a small PDF, the way the webmail does.

Used by deploy-smoke and by DEPLOY.md's rc.29 checks. Run it as the webmail's user, so it also proves that user may
reach the viewer's socket:

    sudo -u anjal python3 /opt/anjal/bin/viewer_check.py /run/anjal-viewer/viewer.sock

Prints "viewer ok: 1 page" and exits 0, or prints what went wrong and exits 1. It sends a PDF it makes itself; no
mail and no key is involved.
"""
import json
import socket
import struct
import sys


def small_pdf() -> bytes:
    """One A4 page with one line of text, with a correct cross-reference table."""
    stream = b"BT /F1 24 Tf 72 720 Td (Anjal viewer check) Tj ET"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream",
    ]
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += str(number).encode() + b" 0 obj\n" + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 " + str(len(objects) + 1).encode() + b"\n0000000000 65535 f \n"
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<< /Size " + str(len(objects) + 1).encode() + b" /Root 1 0 R >>\nstartxref\n" + str(xref).encode() + b"\n%%EOF\n"
    return bytes(out)


def ask(path: str, request: dict, data: bytes, timeout: float) -> tuple:
    """One frame out ("AVW1", JSON length, file count, JSON, then each file's length and bytes), one frame back."""
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect(path)
    body = json.dumps(request).encode()
    s.sendall(b"AVW1" + struct.pack("<ii", len(body), 1) + body + struct.pack("<i", len(data)) + data)

    def read(n: int) -> bytes:
        got = b""
        while len(got) < n:
            chunk = s.recv(n - len(got))
            if not chunk:
                raise EOFError("the viewer closed the connection")
            got += chunk
        return got

    head = read(12)
    if head[:4] != b"AVW1":
        raise ValueError("the viewer did not answer in its own format")
    length, count = struct.unpack("<ii", head[4:])
    answer = json.loads(read(length))
    files = []
    for _ in range(count):
        (size,) = struct.unpack("<i", read(4))
        files.append(read(size))
    s.close()
    return answer, files


def main() -> int:
    path = sys.argv[1] if len(sys.argv) > 1 else "/run/anjal-viewer/viewer.sock"
    try:
        answer, _ = ask(path, {"Kind": "pdf.info", "Name": "check.pdf", "ContentType": "application/pdf", "Who": "check"}, small_pdf(), 30.0)
    except (OSError, ValueError, EOFError) as e:
        print(f"viewer not reachable at {path}: {e}")
        return 1
    status = answer.get("Status")
    if status != "ok":
        print(f"viewer answered {status}: {answer.get('Detail') or ''}")
        return 1
    info = json.loads(answer.get("Json") or "{}")
    pages = info.get("PageCount", 0)
    if pages != 1:
        print(f"viewer read the check file as {pages} pages, not 1")
        return 1
    print("viewer ok: 1 page")
    return 0


if __name__ == "__main__":
    sys.exit(main())
