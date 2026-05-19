"""
Generate API reference Markdown from C# XML documentation files.

Walks src/<Module>/bin/Release/net10.0/<Module>.xml and emits per-type
Markdown to docs/api/<Module>/<TypeName>.md plus a top-level index.

Run from repo root:

    python tools/gen_api_docs.py

The docs CI workflow runs this and fails if docs/api has any uncommitted
changes, so regenerate and commit after any public API change.
"""
from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
DOCS = ROOT / "docs" / "api"

KIND_MAP = {"M": "method", "P": "property", "F": "field", "E": "event"}


def find_module_xml_files() -> list[tuple[str, Path]]:
    """Return (module_name, xml_path) for each module's own XML doc file.

    A module is a directory under src/. We only return XML files whose
    basename matches the module directory name - this avoids picking up
    XML files that the build copies into referencing projects' bin folders.
    """
    results: list[tuple[str, Path]] = []
    for module_dir in sorted(SRC.iterdir()):
        if not module_dir.is_dir():
            continue
        module_name = module_dir.name
        xml_path = module_dir / "bin" / "Release" / "net10.0" / f"{module_name}.xml"
        if xml_path.exists():
            results.append((module_name, xml_path))
    return results


def parse_member(name: str) -> tuple[str, str, str] | None:
    """Parse an XML doc member name. Returns (kind, owning_namespace, member_or_type_name)."""
    if len(name) < 3 or name[1] != ":":
        return None
    kind_char, full = name[0], name[2:]
    if kind_char == "T":
        if "." not in full:
            return None
        ns, type_name = full.rsplit(".", 1)
        return ("type", ns, type_name)
    if kind_char not in KIND_MAP:
        return None
    match = re.match(r"([A-Za-z0-9_.]+)\.([A-Za-z0-9_#]+)(\(.*\))?", full)
    if not match:
        return None
    type_full = match.group(1)
    member_name = match.group(2)
    return (KIND_MAP[kind_char], type_full, member_name)


def inner_text(elem: ET.Element | None) -> str:
    if elem is None:
        return ""
    return " ".join("".join(elem.itertext()).split())


def clean_docs_dir() -> None:
    """Empty docs/api so deleted types do not leave stale .md files behind."""
    if not DOCS.exists():
        return
    for md in DOCS.rglob("*.md"):
        md.unlink()
    for sub in sorted([p for p in DOCS.rglob("*") if p.is_dir()], reverse=True):
        try:
            sub.rmdir()
        except OSError:
            pass


def generate() -> None:
    DOCS.mkdir(parents=True, exist_ok=True)
    clean_docs_dir()

    index_lines: list[str] = ["# Anjal API Reference", ""]
    total_types = 0
    modules_with_types = 0

    for module_name, xml_file in find_module_xml_files():
        tree = ET.parse(xml_file)
        types: dict[str, dict] = {}

        for member in tree.findall(".//member"):
            parsed = parse_member(member.get("name", ""))
            if not parsed:
                continue
            kind, owner, name = parsed
            summary = inner_text(member.find("summary"))
            if kind == "type":
                types.setdefault(name, {"namespace": owner, "summary": "", "members": []})
                types[name]["summary"] = summary
            else:
                type_name = owner.split(".")[-1]
                type_ns = owner.rsplit(".", 1)[0] if "." in owner else ""
                types.setdefault(type_name, {"namespace": type_ns, "summary": "", "members": []})
                types[type_name]["members"].append({
                    "kind": kind,
                    "name": name,
                    "summary": summary,
                })

        if not types:
            continue

        modules_with_types += 1
        module_dir = DOCS / module_name
        module_dir.mkdir(parents=True, exist_ok=True)
        index_lines.append(f"## {module_name}")
        index_lines.append("")

        for type_name in sorted(types):
            info = types[type_name]
            md: list[str] = [f"# {type_name}", "", f"**Namespace:** `{info['namespace']}`", ""]
            if info["summary"]:
                md.extend([info["summary"], ""])
            if info["members"]:
                md.extend(["## Members", ""])
                ordered = sorted(info["members"], key=lambda m: (m["kind"], m["name"]))
                for m in ordered:
                    desc = m["summary"] or "_(no description)_"
                    md.append(f"- **{m['name']}** *({m['kind']})* - {desc}")
                md.append("")
            (module_dir / f"{type_name}.md").write_text("\n".join(md), encoding="utf-8")
            index_lines.append(f"- [{module_name}.{type_name}]({module_name}/{type_name}.md)")
            total_types += 1
        index_lines.append("")

    (DOCS / "index.md").write_text("\n".join(index_lines), encoding="utf-8")
    print(f"Generated docs for {total_types} types across {modules_with_types} modules.")


if __name__ == "__main__":
    generate()
