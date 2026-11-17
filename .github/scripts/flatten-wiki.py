"""Flatten wiki/ into a single folder of GitHub wiki pages.

GitHub wikis are flat and link pages by name, so this copies every page to the
output folder by file name (README.md becomes Home.md) and rewrites relative
Markdown links such as ../guides/Indexes.md#rebuilds to Indexes#rebuilds, in
inline links (with or without a title) and reference definitions.
It fails if two pages share a name, a link points at a page that doesn't exist,
or a relative .md link is in a form it can't rewrite (such as HTML).

Usage: python flatten-wiki.py <source-dir> <output-dir>
"""

import re
import sys
from pathlib import Path

# Inline links, with an optional <destination> and "title": [text](dest "title")
LINK = re.compile(r"""\]\(\s*(<[^<>\n]*>|[^()\s<>]+)((?:\s+(?:"[^"]*"|'[^']*'|\([^()]*\)))?\s*)\)""")
# Reference definitions: [label]: dest "title"
REF_DEF = re.compile(r"""^(\s{0,3}\[[^\]]+\]:[ \t]*)(<[^<>\n]*>|\S+)(.*)$""", re.S)
# Anything still pointing at a relative .md page after rewriting, e.g. HTML or link forms not handled above.
LEFTOVER = re.compile(r"""(?:\]\(\s*<?|\]:\s*<?|(?:href|src)\s*=\s*["']?)(?![a-z][a-z0-9+.-]*:|#)[^)\s"'>]*\.md\b""", re.I)
FENCE = re.compile(r"^\s*(```|~~~)")



def page_name(path: Path) -> str:
    return "Home" if path.stem == "README" else path.stem


def rewrite(target: str, source: Path, pages: dict[str, Path], errors: list[str]) -> str:
    bracketed = target.startswith("<") and target.endswith(">")
    if bracketed:
        target = target[1:-1]
    if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I) or target.startswith("#"):
        return f"<{target}>" if bracketed else target
    path, _, fragment = target.partition("#")
    if not path.endswith(".md"):
        errors.append(f"{source}: relative link to a non-page {target}")
        return target
    resolved = (source.parent / path).resolve()
    name = page_name(resolved)
    if pages.get(name) != resolved:
        errors.append(f"{source}: link to missing page {target}")
        return target
    return name + (f"#{fragment}" if fragment else "")


def main(source_dir: str, output_dir: str) -> int:
    source_root = Path(source_dir).resolve()
    output_root = Path(output_dir)
    errors: list[str] = []

    pages: dict[str, Path] = {}
    for path in sorted(source_root.rglob("*.md")):
        name = page_name(path)
        if name in pages:
            errors.append(f"{path}: page name {name} is already used by {pages[name]}")
        pages[name] = path.resolve()

    output_root.mkdir(parents=True, exist_ok=True)
    for name, path in pages.items():
        lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
        in_fence = False
        for i, line in enumerate(lines):
            if FENCE.match(line):
                in_fence = not in_fence
            elif not in_fence:
                fix = lambda target: rewrite(target, path, pages, errors)
                before = len(errors)
                # Rewrite only outside `code spans`, which show link syntax literally.
                parts = re.split(r"(`[^`\n]*`)", line)
                for j in range(0, len(parts), 2):
                    part = LINK.sub(lambda m: f"]({fix(m.group(1))}{m.group(2)})", parts[j])
                    part = REF_DEF.sub(lambda m: f"{m.group(1)}{fix(m.group(2))}{m.group(3)}", part) if j == 0 else part
                    if len(errors) == before and LEFTOVER.search(part):
                        errors.append(f"{path}:{i + 1}: link to a .md page that can't be rewritten: {line.strip()}")
                    parts[j] = part
                lines[i] = "".join(parts)
        (output_root / f"{name}.md").write_text("".join(lines), encoding="utf-8")

    for error in errors:
        print(f"::error::{error}")
    print(f"Flattened {len(pages)} pages into {output_root}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
