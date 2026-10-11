#!/usr/bin/env python3
"""Generate the webmail's word lists from the owner's six-language sheet.

Source: tools/design/words.xlsx (the sheet "Anjal Interface Words - Six
Languages", committed exactly as the owner returns it). Switch: the
"enabled" list in tools/design/languages.json - a language is added there
only after the owner has verified its words (D-92).

Output: src/Anjal.Webmail/Resources/Words/<code>.json for English (the list
of every word) and for each enabled language; and <code>.preview.json for each
language not yet switched on, holding its drafts as they stand, which only the
service's operators can choose, to look at a language before it is checked
(rc.15, item 41). The owner's wording is carried
exactly as written: nothing is trimmed, re-spaced or corrected. A row marked
"Changed" takes its correction for a language from a line in "Your
correction" that starts with that language's name and a colon, such as
"Tamil: <text>"; the text after the label is used as typed.
"""
import json
import pathlib
import re
import sys

from openpyxl import load_workbook

ROOT = pathlib.Path(__file__).resolve().parent.parent
SHEET = ROOT / "tools" / "design" / "words.xlsx"
SWITCH = ROOT / "tools" / "design" / "languages.json"
OUT = ROOT / "src" / "Anjal.Webmail" / "Resources" / "Words"
LANGUAGES = {"Tamil": "ta", "Malayalam": "ml", "Hindi": "hi", "Marathi": "mr", "Gujarati": "gu"}

# Each language's own Unicode block. Punctuation shared by every script (the
# General Punctuation block: dashes, curly quotes, the ellipsis; and Latin-1
# punctuation such as the middle dot) is allowed.
# Checks only report; nothing is changed.
SCRIPTS = {"ta": (0x0B80, 0x0BFF), "ml": (0x0D00, 0x0D7F), "hi": (0x0900, 0x097F), "mr": (0x0900, 0x097F), "gu": (0x0A80, 0x0AFF)}
JOINERS = {"\u200c", "\u200d"}


def check(word, code, text):
    """Problems with one translation: another script's letters, or placeholders that differ from the English."""
    problems = []
    low, high = SCRIPTS[code]
    for ch in text:
        if ord(ch) > 0x7F and not (low <= ord(ch) <= high) and ch not in JOINERS and not (0x2000 <= ord(ch) <= 0x206F) and not (0x00A0 <= ord(ch) <= 0x00BF):
            problems.append(f"'{word}' ({code}): character U+{ord(ch):04X} is not in this language's script")
            break
    if sorted(re.findall(r"\{[^}]+\}", word)) != sorted(re.findall(r"\{[^}]+\}", text)):
        problems.append(f"'{word}' ({code}): placeholders differ from the English")
    return problems


def correction_for(correction, language):
    """The corrected text for one language, or None. Only the label is read."""
    if not correction:
        return None
    for line in str(correction).splitlines():
        label = language + ":"
        if line.startswith(label):
            text = line[len(label):]
            return text[1:] if text.startswith(" ") else text
    return None


def write(code, words, preview=False):
    payload = {"language": code, "preview": True, "words": words} if preview else {"language": code, "words": words}
    text = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    name = f"{code}.preview.json" if preview else f"{code}.json"
    (OUT / name).write_bytes(text.replace("\n", "\r\n").encode("utf-8"))


def main():
    enabled = json.loads(SWITCH.read_text(encoding="utf-8"))["enabled"]
    sheet = load_workbook(SHEET, read_only=True)["Words"]
    rows = list(sheet.iter_rows(values_only=True))
    head = [str(h) if h is not None else "" for h in rows[0]]
    col = {name: head.index(name) for name in head if name}
    lang_col = {code: next(i for i, h in enumerate(head) if h.startswith(name + " ")) for name, code in LANGUAGES.items()}
    english, per_language, seen, problems = {}, {code: {} for code in LANGUAGES.values()}, set(), []
    for row in rows[1:]:
        word = row[col["English"]]
        if word is None:
            continue
        word = str(word)
        if word in seen:
            print(f"error: '{word}' appears twice in the sheet", file=sys.stderr)
            return 1
        seen.add(word)
        english[word] = word
        changed = row[col["Your check"]] == "Changed"
        for name, code in LANGUAGES.items():
            fixed = correction_for(row[col["Your correction"]], name) if changed else None
            value = fixed if fixed is not None else row[lang_col[code]]
            if value is not None:
                per_language[code][word] = str(value)
                problems.extend(check(word, code, str(value)))
    for problem in problems:
        print("check: " + problem, file=sys.stderr)
    OUT.mkdir(parents=True, exist_ok=True)
    for stale in OUT.glob("*.json"):
        stale.unlink()
    write("en", english)
    for code in enabled:
        if code != "en":
            write(code, per_language[code])
    previews = [code for code in LANGUAGES.values() if code not in enabled]
    for code in previews:
        write(code, per_language[code], preview=True)
    print(f"{len(english)} words; languages switched on: {', '.join(enabled)}; operators' preview: {', '.join(previews) or 'none'}; checks: {len(problems)} problem(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
