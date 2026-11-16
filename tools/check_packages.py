#!/usr/bin/env python3
"""The packages in localpackages/ are the ones that were checked, byte for byte.

rc.29 (DES-22 §11): Anjal's own feed holds LiPicons and the Chuvadi libraries
(chuvadi-pdf, Docs, Sheets and the two font packs), copied from the verified
set the Anjal coordinator checked on 11 Nov 2026. CI runs this before every
restore: every .nupkg in the folder must be listed in ALL-SHA256SUMS.txt with
the same SHA-256, and every listed file must be there. A package changed,
added or removed without its line fails the build before it is used.

Usage:
  python tools/check_packages.py
Uses only the Python standard library.
"""

import hashlib
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
FEED = ROOT / "localpackages"
SUMS = FEED / "ALL-SHA256SUMS.txt"


def main():
    listed = {}
    for line in SUMS.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        digest, name = line.split(None, 1)
        listed[name.strip().lstrip("*")] = digest.lower()
    faults = []
    present = sorted(p.name for p in FEED.glob("*.nupkg"))
    for name in present:
        actual = hashlib.sha256((FEED / name).read_bytes()).hexdigest()
        if name not in listed:
            faults.append(f"{name}: not in ALL-SHA256SUMS.txt")
        elif listed[name] != actual:
            faults.append(f"{name}: SHA-256 {actual} differs from the listed {listed[name]}")
    for name in sorted(set(listed) - set(present)):
        faults.append(f"{name}: listed but not in localpackages/")
    if faults:
        print(f"check_packages: {len(faults)} fault(s)")
        for f in faults:
            print("  " + f)
        sys.exit(1)
    print(f"check_packages: {len(present)} packages, every one matches its SHA-256")


if __name__ == "__main__":
    main()
