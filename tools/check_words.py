#!/usr/bin/env python3
"""List every word the webmail's screens ask for that is not in the word list.

Screens look words up as L["..."]. A word missing from the owner's sheet
still shows in English, but has no translation to verify, so it must be
added to the sheet before the languages are switched on. Exit code 1 when
any are missing, with the list printed.

rc.15 (item 41, owner 7 Oct 2026: "sweep all hard-coded English into the word
list and make the check catch it") adds three checks:

  A. English typed straight into a page - text between tags, or a title,
     aria-label, placeholder or alt - instead of looked up with L[...].
  B. Sentences the server sends to a screen (errors and notes in the
     webmail's code): each must be in the list; one with values filled in
     ($"... {x} ...") must match a pattern in the list ("... {n} ...").
  C. Sentences the browser script writes with W("..."): each must be in the
     page's word block (JsWords.razor), which takes it from the list.

What screens look up at run time (lists of words, server messages) is
checked by the test EveryScreenSpeaksTests, which opens every screen.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
WORDS = ROOT / "src" / "Anjal.Webmail" / "Resources" / "Words" / "en.json"
SCREENS = ROOT / "src" / "Anjal.Webmail" / "Components"


WEBMAIL = ROOT / "src" / "Anjal.Webmail"

# Lines for the dashboards and the service's health are made as English with
# blanks ("{n} drafts not finished") and looked up where they are shown, so
# their words are the literals inside these constructors: everything but
# icon names, levels, links, separators and date formats.
MADE_LATER = re.compile(r'new (?:DashLine|HealthTile)?\(')
NOT_A_WORD = re.compile(r'^(?:[a-z0-9-]*|/.*|[A-Z][0-9]?|[dMyHhms :,.]*)$')


def made_later(text):
    """The words in DashLine and HealthTile constructors in one file."""
    for m in MADE_LATER.finditer(text):
        if m.group(0) == "new(" and text[m.end():m.end() + 1] != '"':
            continue
        depth, i = 1, m.end()
        while i < len(text) and depth > 0:
            if text[i] == '"':
                j = i + 1
                while text[j] != '"':
                    j += 2 if text[j] == "\\" else 1
                literal = text[i + 1:j]
                i = j + 1
                if not NOT_A_WORD.match(literal):
                    yield literal
                continue
            depth += {"(": 1, ")": -1}.get(text[i], 0)
            i += 1


def main():
    known = set(json.loads(WORDS.read_text(encoding="utf-8"))["words"])
    used = {}
    for path in sorted(SCREENS.rglob("*.razor")):
        text = path.read_text(encoding="utf-8")
        for m in re.finditer(r'\bL\["((?:[^"\\]|\\.)*)"\]', text):
            used.setdefault(m.group(1), path.relative_to(ROOT).as_posix())
    for path in sorted(list(WEBMAIL.rglob("*.razor")) + list(WEBMAIL.rglob("*.cs"))):
        if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
            continue
        text = path.read_text(encoding="utf-8")
        if "DashLine" not in text and "HealthTile" not in text:
            continue
        for word in made_later(text):
            used.setdefault(word, path.relative_to(ROOT).as_posix())
    missing = {w: where for w, where in used.items() if w not in known}
    print(f"{len(used)} words used on screens; {len(used) - len(missing)} in the word list; {len(missing)} missing")
    for word, where in sorted(missing.items()):
        print(f"  missing: {word!r}  (first used in {where})")
    typed = typed_into_pages()
    unlisted = server_sentences(known) + dashboard_lines(known)
    unmapped = script_sentences()
    print(f"English typed into pages: {len(typed)}; server sentences not in the list: {len(unlisted)}; script sentences not in the page's word block: {len(unmapped)}")
    for where, text in typed:
        print(f"  typed: {text!r}  ({where})")
    for where, text in unlisted:
        print(f"  server: {text!r}  ({where})")
    for text in unmapped:
        print(f"  script: {text!r}  (app.js; add it to Components/JsWords.razor)")
    return 1 if missing or typed or unlisted or unmapped else 0


# ---- A. English typed straight into a page ----------------------------------

# Text that is not words to translate: the product's name after a page title,
# fingerprints, the formatting bar's B and I, keyboard keys, file and record
# types and units (CSV, vCard, TXT, GB), example addresses and domains, codes
# such as abcd-2345, and pieces of code the simple reading below leaves behind.
NOT_WORDS = re.compile(r"^(?:[—-] Anjal|Anjal|SHA-256|SPF|DKIM|DMARC|B|I|Enter|Esc|Shift|Ctrl|Tab|vCard|[A-Z]{2,4}|"
                       r"[\w.-]+\.[a-z]{2,}|_[\w-]+|[a-z]{4}-\d{4}|[?!]?\.[A-Za-z.]+)$")
# A placeholder that is one lower-case word is an example address part (admin, enquiries).
EXAMPLE_PART = re.compile(r"^[a-z]+$")


def typed_into_pages():
    """Text between tags and title/aria-label/placeholder/alt values written as English."""
    found = []
    for path in sorted(SCREENS.rglob("*.razor")):
        text = path.read_text(encoding="utf-8")
        text = re.sub(r"@\*.*?\*@", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
        cut = text.find("@code")
        if cut >= 0:
            text = text[:cut]
        text = re.sub(r"<(script|style)\b.*?</\1>", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
        where = path.relative_to(ROOT).as_posix()
        for m in re.finditer(r">([^<>]*)<", text):
            node = re.sub(r"@\((?:[^()]|\([^()]*\))*\)", " ", m.group(1))
            node = re.sub(r"@[A-Za-z_][\w.]*(?:\[[^\]]*\])?(?:\([^)]*\))?", " ", node)
            node = re.sub(r"&[a-z#0-9]+;", " ", node).strip()
            if any(k in node for k in ("{", "}", "=>", ";", "(", ")")):
                continue
            for piece in re.split(r"\s{2,}", node):
                piece = piece.strip(" .,:·")
                if re.search(r"[A-Za-z]{2,}", piece) and not NOT_WORDS.match(piece):
                    found.append((f"{where}:{text[:m.start()].count(chr(10)) + 1}", piece))
        for m in re.finditer(r'\b(title|aria-label|placeholder|alt)="([^"@]*)"', text):
            value = m.group(2).strip()
            if m.group(1) == "placeholder" and EXAMPLE_PART.match(value):
                continue
            if re.search(r"[A-Za-z]{2,}", value) and not NOT_WORDS.match(value):
                found.append((f"{where}:{text[:m.start()].count(chr(10)) + 1}", value))
    return found


# ---- B. Sentences the server sends to a screen -------------------------------

# Where a sentence is not screen text: log lines and exceptions, the festival
# lines of the mail templates (they have their own sheet), and start-up notes.
SKIP_FILES = {"TemplateCatalogue.cs", "MtaStsPolicy.cs"}
SKIP_LINE = re.compile(r"Console\.|\blog\(|\bLog\(|throw new|Exception\(|^\s*//")
SKIP_TEXT = re.compile(r"^(?:HTTP/3|Evidence:|MTA-STS|Settings:)")
SENTENCE = re.compile(r"^[A-Z][^\n]*\s[^\n]*[.!?]$")
BLANK = re.compile(r"\{[^{}]*\}")


def shape(text):
    """A sentence with every blank made alike, to match "allows {min}" with "allows {MinTrashDays}"."""
    return BLANK.sub("{}", text)


def server_sentences(known):
    shapes = {shape(w) for w in known}
    found = []
    for path in sorted(WEBMAIL.rglob("*.cs")):
        posix = path.as_posix()
        if "/obj/" in posix or "/bin/" in posix or path.name in SKIP_FILES:
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if SKIP_LINE.search(line):
                continue
            for m in re.finditer(r'(\$?)@?"((?:[^"\\\n]|\\.)*)"', line):
                value = m.group(2).replace('\\"', '"').replace("\\\\", "\\").replace("\\u201c", "\u201c").replace("\\u201d", "\u201d")
                if not SENTENCE.match(value) or SKIP_TEXT.match(value):
                    continue
                if m.group(1):
                    if shape(value) not in shapes:
                        found.append((f"{path.relative_to(ROOT).as_posix()}:{number}", value))
                elif value not in known:
                    found.append((f"{path.relative_to(ROOT).as_posix()}:{number}", value))
    return found


# DES-11 D8 (10 Oct 2026): the dashboards' short lines - "{org}: {n} sent out today" - are not
# sentences, so B alone missed them. Every text given to a dashboard line or a health tile, or
# chosen for one in a switch, is looked for too (blanks made alike).
DASH_LINE = re.compile(r'new DashLine\(|new HealthTile\(|new\("[a-z-]+", "|^\s*"[a-z]+"\s*=>\s*"|^\s*_\s*=>\s*"')


def dashboard_lines(known):
    shapes = {shape(w) for w in known}
    found = []
    for path in sorted(list(WEBMAIL.rglob("*.cs")) + list(WEBMAIL.rglob("*.razor"))):
        posix = path.as_posix()
        if "/obj/" in posix or "/bin/" in posix or path.name in SKIP_FILES:
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if not DASH_LINE.search(line) or SKIP_LINE.search(line):
                continue
            for m in re.finditer(r'(\$?)@?"((?:[^"\\\n]|\\.)*)"', line):
                value = m.group(2)
                if " " not in value or value.startswith("/") or "://" in value or not re.match(r"^[A-Z{]", value):
                    continue
                if shape(value) not in shapes:
                    found.append((f"{path.relative_to(ROOT).as_posix()}:{number}", value))
    return found


# ---- C. Sentences the browser script writes ----------------------------------

def script_sentences():
    script = (WEBMAIL / "wwwroot" / "app.js").read_text(encoding="utf-8")
    block = (SCREENS / "JsWords.razor").read_text(encoding="utf-8")
    mapped = set(re.findall(r'\["((?:[^"\\]|\\.)*)"\] = L\[', block))
    asked = set(re.findall(r'\bW\("((?:[^"\\]|\\.)*)"\)', script))
    asked |= set(re.findall(r'passkeyError\([^)]*?"((?:[^"\\]|\\.)*[.])"', script))
    asked = {a.replace("\\u00b7", "\u00b7") for a in asked}
    return sorted(a for a in asked if a not in mapped)


if __name__ == "__main__":
    sys.exit(main())
