#!/usr/bin/env python3
"""Contrast audit for every theme (rc.11, UX-09; SPEC-11 item 40).

Computes the WCAG 2.1 contrast ratio of each text-and-background pair the
webmail uses, in every theme of tools/design/tokens.json. Normal text needs
4.5:1; large or bold labels, icons and the focus ring need 3:1. Exit code 1
when any pair falls short, with the list printed. Reads only; changes nothing.
"""
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TOKENS = ROOT / "tools" / "design" / "tokens.json"

# (foreground, background, minimum, where it is used)
PAIRS = [
    ("ink", "surface-000", 4.5, "text on the page ground"),
    ("ink", "surface-100", 4.5, "text in lists and the letter's ground"),
    ("ink", "surface-200", 4.5, "text on cards, rows and the envelope"),
    ("ink", "surface-300", 4.5, "text on a hovered row"),
    ("ink-muted", "surface-100", 4.5, "secondary text: times, previews"),
    ("ink-muted", "surface-200", 4.5, "secondary text on cards"),
    ("on-brand", "brand", 4.5, "Compose, Reply and primary buttons"),
    ("brand", "surface-200", 4.5, "links and the current folder"),
    ("brand", "brand-soft", 3.0, "the current folder's icon in the rail (D-112: its text is ink)"),
    ("ink", "brand-soft", 4.5, "the open message's row"),
    ("ink", "warning-soft", 4.5, "amber score and notices"),
    ("ink", "danger-soft", 4.5, "red score"),
    ("danger", "surface-200", 4.5, "danger text: Delete permanently"),
    ("on-seal", "seal", 4.5, "the stamp's words"),
    ("seal", "surface-200", 3.0, "the stamp's panel against the envelope"),
    ("focus-ring", "surface-200", 3.0, "the focus ring on cards"),
    ("focus-ring", "surface-100", 3.0, "the focus ring in lists"),
    ("line-strong", "surface-200", 3.0, "borders of fields and buttons"),
]


def channel(c):
    c = c / 255.0
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4


def luminance(hex_colour):
    h = hex_colour.strip().lstrip("#")
    if len(h) == 3:
        h = "".join(ch * 2 for ch in h)
    r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)


def ratio(a, b):
    la, lb = luminance(a), luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def main():
    tokens = json.loads(TOKENS.read_text(encoding="utf-8"))
    themes = [t["id"] for t in tokens["color"]["themes"]]
    values = {t["name"]: t["value"] for t in tokens["color"]["tokens"]}
    failures, worst = [], {}
    for theme in themes:
        for fg, bg, minimum, where in PAIRS:
            if fg not in values or bg not in values:
                failures.append(f"{theme}: token missing for {fg} on {bg}")
                continue
            r = ratio(values[fg][theme], values[bg][theme])
            key = (fg, bg)
            if key not in worst or r < worst[key][0]:
                worst[key] = (r, theme)
            if r < minimum:
                failures.append(f"{theme:16} {fg:12} on {bg:12} {r:5.2f} : 1, needs {minimum} ({where})")
    print(f"{len(themes)} themes x {len(PAIRS)} pairs checked ('-auto' themes use these same values)")
    for (fg, bg, minimum, where) in PAIRS:
        r, theme = worst[(fg, bg)]
        print(f"  lowest {fg:12} on {bg:12} {r:5.2f} : 1 in {theme:16} (needs {minimum})")
    for f in failures:
        print("  FAILS: " + f)
    print(f"{len(failures)} pair(s) below the minimum")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
