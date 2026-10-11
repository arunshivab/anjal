#!/usr/bin/env python3
"""Line endings and byte-order marks, checked the same way everywhere.

Owner, 11 Oct 2026: line-ending faults kept coming back for weeks (style
failures on CI, "different" files in release checks, a BOM in a commit
subject). They had four causes, each closed here:

  1. File types with no rule in .gitattributes took whatever the machine's
     git settings gave them, so Windows and the Linux CI saw different bytes.
     Now every tracked file must match an explicit rule ("text eol=crlf",
     "text eol=lf" or "binary"). A new file type with no rule fails this
     check and names the line to add.
  2. .editorconfig said CRLF for every file, while .gitattributes said LF for
     scripts and workflows, so editors and git disagreed. The two must now
     agree for every tracked file.
  3. Edits and generators wrote LF, CRLF or a stray CR into CRLF files
     (app.css held three "CR CR LF" lines). Every text file must now use its
     rule's ending throughout, with no lone CR.
  4. Windows PowerShell 5 adds a byte-order mark. No text file may start
     with one.

Usage:
  python tools/check_eol.py          check; exit 1 with a list of faults
  python tools/check_eol.py --fix    rewrite working files to their rule,
                                     then check

Run on every CI change (style workflow) and in the local gate before a
commit. Uses only git and the Python standard library.
"""

import fnmatch
import os
import re
import subprocess
import sys

BOM = b"\xef\xbb\xbf"
LONE_CR = re.compile(rb"\r(?!\n)")
BARE_LF = re.compile(rb"(?<!\r)\n")


def git(*args, stdin=None):
    out = subprocess.run(["git", *args], input=stdin, check=True, capture_output=True)
    return out.stdout


def tracked_files():
    return [f for f in git("ls-files", "-z").decode("utf-8").split("\0") if f]


def rules_for(files):
    """Map each file to 'crlf', 'lf', 'binary' or None (no explicit rule)."""
    # Paths go in on stdin: on Windows, 900+ paths on the command line pass its
    # 32,767-character limit (WinError 206).
    data = git("check-attr", "-z", "--stdin", "text", "eol", "binary",
               stdin="\0".join(files).encode("utf-8") + b"\0")
    parts = data.decode("utf-8").split("\0")
    attrs = {}
    for i in range(0, len(parts) - 2, 3):
        path, name, value = parts[i], parts[i + 1], parts[i + 2]
        attrs.setdefault(path, {})[name] = value
    rules = {}
    for f in files:
        a = attrs.get(f, {})
        if a.get("binary") == "set" or a.get("text") == "unset":
            rules[f] = "binary"
        elif a.get("text") == "set" and a.get("eol") in ("crlf", "lf"):
            rules[f] = a["eol"]
        else:
            rules[f] = None
    return rules


def editorconfig_sections(path=".editorconfig"):
    sections = []
    current = None
    with open(path, encoding="utf-8-sig") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith(("#", ";")):
                continue
            if line.startswith("[") and line.endswith("]"):
                current = {"glob": line[1:-1], "props": {}}
                sections.append(current)
            elif current is not None and "=" in line:
                key, value = (s.strip() for s in line.split("=", 1))
                current["props"][key.lower()] = value.lower()
    return sections


def expand_braces(glob):
    m = re.search(r"\{([^{}]*)\}", glob)
    if not m:
        return [glob]
    out = []
    for choice in m.group(1).split(","):
        out.extend(expand_braces(glob[: m.start()] + choice + glob[m.end():]))
    return out


def editorconfig_eol(path, sections):
    """The end_of_line .editorconfig gives a file (last matching section wins)."""
    name = os.path.basename(path)
    eol = None
    for s in sections:
        for g in expand_braces(s["glob"]):
            target = path if "/" in g else name
            if fnmatch.fnmatchcase(target, g.lstrip("/")):
                if "end_of_line" in s["props"]:
                    eol = s["props"]["end_of_line"]
    return eol


def normalise(data, eol):
    if data.startswith(BOM):
        data = data[len(BOM):]
    data = LONE_CR.sub(b"", data)
    data = data.replace(b"\r\n", b"\n")
    if eol == "crlf":
        data = data.replace(b"\n", b"\r\n")
    return data


def faults_in(data, eol):
    found = []
    if data.startswith(BOM):
        found.append("starts with a byte-order mark")
    if LONE_CR.search(data):
        found.append("has a stray CR (not followed by LF)")
    if eol == "crlf" and BARE_LF.search(data):
        found.append("has LF line endings; its rule is CRLF")
    if eol == "lf" and b"\r\n" in data:
        found.append("has CRLF line endings; its rule is LF")
    return found


def main(argv):
    fix = "--fix" in argv
    root = git("rev-parse", "--show-toplevel").decode("utf-8").strip()
    os.chdir(root)
    files = tracked_files()
    rules = rules_for(files)
    sections = editorconfig_sections()
    problems = []
    fixed = 0

    for f in files:
        rule = rules[f]
        if rule is None:
            ext = os.path.splitext(f)[1]
            pattern = f"*{ext}" if ext else os.path.basename(f)
            problems.append(
                f"{f}: no line-ending rule. Add '{pattern} text eol=crlf' (or eol=lf, "
                f"or 'binary') to .gitattributes, and the same ending to .editorconfig")
            continue
        if rule == "binary" or not os.path.isfile(f):
            continue
        ec = editorconfig_eol(f, sections)
        if ec != rule:
            problems.append(
                f"{f}: .gitattributes says {rule}, .editorconfig says {ec or 'nothing'}")
        with open(f, "rb") as fh:
            data = fh.read()
        if b"\0" in data:
            problems.append(f"{f}: has NUL bytes but its rule says text; mark it binary")
            continue
        if fix and faults_in(data, rule):
            with open(f, "wb") as fh:
                fh.write(normalise(data, rule))
            fixed += 1
            data = normalise(data, rule)
        for fault in faults_in(data, rule):
            problems.append(f"{f}: {fault}")

    # What git stores must be LF for every text file, whatever the checkout shows.
    for line in git("ls-files", "--eol").decode("utf-8").splitlines():
        index_state, _, rest = line.partition(" ")
        path = rest.split("\t", 1)[-1]
        if rules.get(path) in ("crlf", "lf") and index_state in ("i/crlf", "i/mixed"):
            problems.append(
                f"{path}: stored in git as {index_state[2:]}; run 'git add --renormalize .' and commit")

    if fixed:
        print(f"check_eol: rewrote {fixed} file(s) to their rule")
    if problems:
        print(f"check_eol: {len(problems)} fault(s)")
        for p in problems:
            print("  " + p)
        return 1
    print(f"check_eol: {len(files)} files, every one matches its rule")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
