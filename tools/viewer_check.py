#!/usr/bin/env python3
"""rc.29 (DES-22 section 4): asks the running anjal-viewer service to read a small PDF, the way the webmail does.

Used by deploy-smoke and by DEPLOY.md's rc.29 checks. Run it as the webmail's user, so it also proves that user may
reach the viewer's socket:

    sudo -u anjal python3 /opt/anjal/bin/viewer_check.py /run/anjal-viewer/viewer.sock

Prints "viewer ok: 1 page, 2 locked archives, DICOM" and exits 0, or prints what went wrong and exits 1. It sends a PDF it
makes itself and two small locked ZIPs it carries (rc.29 part 2: one AES, as WinZip and 7-Zip lock them, one ZipCrypto,
the old ZIP password), so the published viewer - trimmed as it ships - is seen to open both; and (rc.30) a made-up
DICOM slice it makes itself, previewed, and a ZIP of two slices grouped as one series, so SIAARA's native library is
seen to load in the trimmed viewer. No mail and no key is involved, and no real patient's data.
"""
import base64
import io
import json
import socket
import struct
import sys
import zipfile


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

def dicom_slice(number: int, z: float, series: str = "1", modality: str = "CT", description: str = "", patient: str = "CHECK^ONLY", size: int = 16) -> bytes:
    """rc.30: a made-up CT slice (Explicit VR Little Endian, 16-bit MONOCHROME2), as the viewer's tests make. The
    sample mail of the screen checks (tools/seed_sample_mail.py) makes its study with it too."""

    def element(group: int, el: int, vr: str, value: bytes) -> bytes:
        head = struct.pack("<HH", group, el) + vr.encode()
        if vr in ("OB", "OW", "SQ", "UN", "UT"):
            return head + b"\0\0" + struct.pack("<I", len(value)) + value
        return head + struct.pack("<H", len(value)) + value

    def uid(u: str) -> bytes:
        return (u if len(u) % 2 == 0 else u + "\0").encode()

    def text(s: str) -> bytes:
        return (s if len(s) % 2 == 0 else s + " ").encode()

    sop = "1.2.826.0.1.3680043.10.999.3." + series + "." + str(number)
    meta = (element(0x0002, 0x0001, "OB", b"\0\1") + element(0x0002, 0x0002, "UI", uid("1.2.840.10008.5.1.4.1.1.2"))
            + element(0x0002, 0x0003, "UI", uid(sop)) + element(0x0002, 0x0010, "UI", uid("1.2.840.10008.1.2.1")))
    # A gradient with a ring, so a slice reads as a picture and slices differ from each other.
    pixels = b"".join(struct.pack("<H", (4095 if abs(((x - size / 2) ** 2 + (y - size / 2) ** 2) ** 0.5 - size * (0.15 + 0.03 * number)) < 1.5 else (x + y) * 2047 // size)) for y in range(size) for x in range(size))
    data = [
        (0x0008, 0x0016, "UI", uid("1.2.840.10008.5.1.4.1.1.2")),
        (0x0008, 0x0018, "UI", uid(sop)),
        (0x0008, 0x0020, "DA", text("20261002")),
        (0x0008, 0x0060, "CS", text(modality)),
        (0x0008, 0x103E, "LO", text(description)),
        (0x0010, 0x0010, "PN", text(patient)),
        (0x0020, 0x000D, "UI", uid("1.2.826.0.1.3680043.10.999.1")),
        (0x0020, 0x000E, "UI", uid("1.2.826.0.1.3680043.10.999.2." + series)),
        (0x0020, 0x0011, "IS", text(series)),
        (0x0020, 0x0013, "IS", text(str(number))),
        (0x0020, 0x0032, "DS", text("0\\0\\" + format(z, ".1f"))),
        (0x0020, 0x0037, "DS", text("1\\0\\0\\0\\1\\0")),
        (0x0028, 0x0002, "US", struct.pack("<H", 1)),
        (0x0028, 0x0004, "CS", text("MONOCHROME2")),
        (0x0028, 0x0010, "US", struct.pack("<H", size)),
        (0x0028, 0x0011, "US", struct.pack("<H", size)),
        (0x0028, 0x0100, "US", struct.pack("<H", 16)),
        (0x0028, 0x0101, "US", struct.pack("<H", 12)),
        (0x0028, 0x0102, "US", struct.pack("<H", 11)),
        (0x0028, 0x0103, "US", struct.pack("<H", 0)),
        (0x7FE0, 0x0010, "OW", pixels),
    ]
    out = b"\0" * 128 + b"DICM" + element(0x0002, 0x0000, "UL", struct.pack("<I", len(meta))) + meta
    return out + b"".join(element(*d) for d in data)


def dicom_study() -> bytes:
    """rc.30: a ZIP of two slices of one series."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("study/slice-2.dcm", dicom_slice(2, 5.0))
        z.writestr("study/slice-1.dcm", dicom_slice(1, 0.0))
    return buffer.getvalue()


def check_dicom(path: str) -> str | None:
    """rc.30: the preview of one slice and the grouping of two; None when both are right, else what went wrong."""
    answer, files = ask(path, {"Kind": "dicom", "Name": "check.dcm", "ContentType": "application/dicom", "Who": "check"}, dicom_slice(1, 0.0), 30.0)
    view = json.loads(answer.get("Json") or "{}")
    if answer.get("Status") != "ok" or view.get("Outcome") != "Ok" or not files or files[0][:8] != b"\x89PNG\r\n\x1a\n":
        return f"viewer could not preview the DICOM check file: {answer.get('Status')} {view.get('Outcome')} {answer.get('Detail') or ''}"
    answer, _ = ask(path, {"Kind": "dicom.group", "Name": "check.zip", "ContentType": "application/zip", "Who": "check"}, dicom_study(), 30.0)
    study = json.loads(answer.get("Json") or "{}")
    slices = [s.get("Slices") for p in study.get("Patients", []) for st in p.get("Studies", []) for s in st.get("Series", [])]
    if answer.get("Status") != "ok" or slices != [2]:
        return f"viewer grouped the DICOM check study as {slices}: {answer.get('Status')} {answer.get('Detail') or ''}"
    return None



# check.txt ("Anjal viewer check"), password "anjal-check": AES-256 written by Chuvadi.Zip, and ZipCrypto by Info-ZIP's
# "zip -P".
LOCKED = {
    "AES": "UEsDBDMAAQBjAOx7cF0AAAAAMAAAABIAAAAJABQAY2hlY2sudHh0VVQFAAHMIftqAZkHAAIAQUUDCAAR+YGeOKoLUxmXawIbPYhSYKGqT1iMAcSWZeCpQVVqUKRP3sPzSSSiMyACwJTjkYBQSwECPwMzAAEAYwDse3BdAAAAADAAAAASAAAACQAUAAAAAAAAAAAApIEAAAAAY2hlY2sudHh0VVQFAAHMIftqAZkHAAIAQUUDCABQSwUGAAAAAAEAAQBLAAAAawAAAAAA",
    "ZipCrypto": "UEsDBAoACQAAAABIcF1hhnp+HgAAABIAAAAJAAAAY2hlY2sudHh0aeiJ3oP+EAKD7EX16xTy5D8dHnbD+1D3faugalk2UEsHCGGGen4eAAAAEgAAAFBLAQIeAwoACQAAAABIcF1hhnp+HgAAABIAAAAJAAAAAAAAAAEAAACkgQAAAABjaGVjay50eHRQSwUGAAAAAAEAAQA3AAAAVQAAAAAA",
}
LOCKED_PASSWORD = "anjal-check"


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
    for name, data in LOCKED.items():
        request = {"Kind": "archive.list", "Name": "check.zip", "ContentType": "application/zip", "Who": "check"}
        try:
            asked, _ = ask(path, request, base64.b64decode(data), 30.0)
            answer, _ = ask(path, dict(request, Password=LOCKED_PASSWORD), base64.b64decode(data), 30.0)
        except (OSError, ValueError, EOFError) as e:
            print(f"viewer not reachable at {path}: {e}")
            return 1
        if asked.get("Status") != "needs-password":
            print(f"viewer did not ask for the {name} archive's password: {asked.get('Status')}")
            return 1
        if answer.get("Status") != "ok":
            print(f"viewer could not open the {name} archive: {answer.get('Status')} {answer.get('Detail') or ''}")
            return 1
        view = json.loads(answer.get("Json") or "{}")
        names = [e.get("Name") for e in view.get("Entries", [])]
        if not view.get("Locked") or names != ["check.txt"]:
            print(f"viewer listed the {name} archive as {names}, locked {view.get('Locked')}")
            return 1
    try:
        dicom = check_dicom(path)
    except (OSError, ValueError, EOFError) as e:
        print(f"viewer not reachable at {path}: {e}")
        return 1
    if dicom is not None:
        print(dicom)
        return 1
    print("viewer ok: 1 page, 2 locked archives, DICOM")
    return 0


if __name__ == "__main__":
    sys.exit(main())
