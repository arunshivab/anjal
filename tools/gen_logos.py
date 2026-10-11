#!/usr/bin/env python3
"""Draw Anjal's logo files and favicons (DES-11 D6, owner 10 Oct 2026).

The mark is the one the webmail draws on every screen: a tile in Anjal deep
teal, the marigold envelope flap, and under it the first letter of the
reader's language (A, அ, അ, अ or અ; SPEC-11 item 39). The files are drawn
from the same shapes and colours (tools/design/tokens.json, theme
"anjal-light") and from LiPi Sans itself, turned into outlines, so they show
the same everywhere with no font needed.

Writes src/Anjal.Webmail/wwwroot/logos/:
  anjal-mark.svg, anjal-mark-reverse.svg, anjal-mark-mono.svg   (letter A)
  anjal-mark-<script>.svg                                       (each letter)
  anjal-favicon.svg, anjal-favicon-<script>.svg                  (32 x 32)
  anjal-wordmark.svg, anjal-lockup.svg, anjal-lockup-reverse.svg, anjal-lockup-mono.svg

Needs: pip install fonttools brotli uharfbuzz. Run it, look at the files, and
commit them; never edit them by hand.
"""
import io
import json
import pathlib

import uharfbuzz as hb
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.boundsPen import BoundsPen
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

ROOT = pathlib.Path(__file__).resolve().parent.parent
FONTS = ROOT / "src" / "Anjal.Webmail" / "wwwroot" / "fonts"
OUT = ROOT / "src" / "Anjal.Webmail" / "wwwroot" / "logos"
TOKENS = ROOT / "tools" / "design" / "tokens.json"

# The letter each script's mark carries, and the font that draws it.
LETTERS = {
    "latn": ("A", "Latin"),
    "taml": ("அ", "Tamil"),
    "mlym": ("അ", "Malayalam"),
    "deva": ("अ", "Devanagari"),
    "gujr": ("અ", "Gujarati"),
}
WEIGHT = 800

_fonts = {}


def font(name):
    """LiPi Sans for a script, fixed at the mark's weight."""
    if name not in _fonts:
        path = FONTS / f"LiPi-Sans-{name}.woff2"
        tt = TTFont(path)
        axes = {a.axisTag for a in tt["fvar"].axes}
        loc = {"wght": WEIGHT}
        if "opsz" in axes:
            loc["opsz"] = 32
        if "wdth" in axes:
            loc["wdth"] = 100
        inst = instancer.instantiateVariableFont(tt, loc)
        # HarfBuzz reads the plain font, not the compressed web file.
        inst.flavor = None
        data = io.BytesIO()
        inst.save(data)
        hbfont = hb.Font(hb.Face(hb.Blob(data.getvalue())))
        _fonts[name] = (inst, hbfont)
    return _fonts[name]


def shape(text, name):
    """Glyph names and positions (font units) for a run of text."""
    inst, hbfont = font(name)
    buf = hb.Buffer()
    buf.add_str(text)
    buf.guess_segment_properties()
    hb.shape(hbfont, buf, {})
    order = inst.getGlyphOrder()
    x = 0
    out = []
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        out.append((order[info.codepoint], x + pos.x_offset, pos.y_offset))
        x += pos.x_advance
    return out, x


def outline(text, name, scale, dx, dy):
    """The text as one SVG path, y downwards, origin at (dx, dy) on the baseline."""
    inst, _ = font(name)
    gs = inst.getGlyphSet()
    pen = SVGPathPen(gs, ntos=lambda v: ("%.2f" % v).rstrip("0").rstrip("."))
    glyphs, _ = shape(text, name)
    for g, x, y in glyphs:
        gs[g].draw(TransformPen(pen, (scale, 0, 0, -scale, dx + x * scale, dy - y * scale)))
    return pen.getCommands()


def bounds(text, name):
    """The ink box of the text in font units (y upwards)."""
    inst, _ = font(name)
    gs = inst.getGlyphSet()
    bp = BoundsPen(gs)
    glyphs, adv = shape(text, name)
    for g, x, y in glyphs:
        gs[g].draw(TransformPen(bp, (1, 0, 0, 1, x, y)))
    return bp.bounds, adv


def colours():
    tokens = {t["name"]: t["value"] for t in json.loads(TOKENS.read_text(encoding="utf-8"))["color"]["tokens"]}
    return {k: tokens[k]["anjal-light"] for k in ("brand", "accent", "on-brand", "ink")}


def letter_path(script, box=148):
    """The letter, centred in the lower half of the tile, as the screens place it."""
    text, name = LETTERS[script]
    (x0, y0, x1, y1), _ = bounds(text, name)
    # As the screens place it: the ink at most 42 units high and 88 wide in the 148 tile,
    # centred on (74, 108), clear of the flap above.
    scale = min(42 / (y1 - y0), 88 / (x1 - x0))
    cx, cy = 74, 108
    dx = cx - (x0 + x1) / 2 * scale
    dy = cy + (y0 + y1) / 2 * scale
    k = box / 148
    return outline(text, name, scale * k, dx * k, dy * k)


def mark(script, tile, flap, letter, size=148, title="Anjal"):
    k = size / 148
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}" role="img" aria-label="{title}">'
        f"<title>{title}</title>"
        f'<rect width="{size}" height="{size}" rx="{34 * k:g}" fill="{tile}"/>'
        f'<path d="M{18 * k:g} {30 * k:g} L{74 * k:g} {78 * k:g} L{130 * k:g} {30 * k:g}" fill="none" stroke="{flap}" stroke-width="{12 * k:g}" stroke-linecap="round" stroke-linejoin="round"/>'
        f'<path fill="{letter}" d="{letter_path(script, size)}"/>'
        "</svg>\n"
    )


def word(fill, height=64):
    """'Anjal' in LiPi Sans at the mark's weight; cap height fills 62% of the height."""
    (x0, y0, x1, y1), adv = bounds("Anjal", "Latin")
    scale = height * 0.62 / (y1 - 0)
    width = (x1 - x0) * scale
    path = outline("Anjal", "Latin", scale, -x0 * scale, height * 0.80)
    return path, width


def wordmark(fill):
    path, width = word(fill)
    w = round(width + 2)
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} 64" width="{w}" height="64" role="img" aria-label="Anjal">'
        f'<title>Anjal</title><path fill="{fill}" d="{path}"/></svg>\n'
    )


def lockup(tile, flap, letter, text):
    # The mark at 64, a gap of 16, then the word at the same height.
    inner = mark("latn", tile, flap, letter, 64)
    inner = inner[inner.index("<rect"):inner.rindex("</svg>")]
    path, width = word(text)
    w = round(64 + 16 + width + 2)
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} 64" width="{w}" height="64" role="img" aria-label="Anjal">'
        f"<title>Anjal</title>{inner}"
        f'<path fill="{text}" transform="translate(80 0)" d="{path}"/></svg>\n'
    )


def main():
    c = colours()
    files = {
        "anjal-mark.svg": mark("latn", c["brand"], c["accent"], c["on-brand"]),
        "anjal-mark-reverse.svg": mark("latn", "#FFFFFF", c["accent"], c["brand"]),
        "anjal-mark-mono.svg": mark("latn", c["ink"], "#FFFFFF", "#FFFFFF"),
        "anjal-favicon.svg": mark("latn", c["brand"], c["accent"], c["on-brand"], 32),
        "anjal-wordmark.svg": wordmark(c["brand"]),
        "anjal-lockup.svg": lockup(c["brand"], c["accent"], c["on-brand"], c["brand"]),
        "anjal-lockup-reverse.svg": lockup("#FFFFFF", c["accent"], c["brand"], "#FFFFFF"),
        "anjal-lockup-mono.svg": lockup(c["ink"], "#FFFFFF", "#FFFFFF", c["ink"]),
    }
    for script in LETTERS:
        files[f"anjal-mark-{script}.svg"] = mark(script, c["brand"], c["accent"], c["on-brand"])
        files[f"anjal-favicon-{script}.svg"] = mark(script, c["brand"], c["accent"], c["on-brand"], 32)
    for name, svg in files.items():
        (OUT / name).write_text(svg, encoding="utf-8", newline="\n")
    print(f"wrote {len(files)} files to {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
