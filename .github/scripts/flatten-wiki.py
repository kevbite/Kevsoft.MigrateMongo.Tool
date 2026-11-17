"""Flatten wiki/ into a single folder of GitHub wiki pages.

GitHub wikis are flat and link pages by name, so this copies every page to the
output folder by file name (README.md becomes Home.md) and rewrites relative
Markdown links such as ../guides/Indexes.md#rebuilds to Indexes#rebuilds.
It fails if two pages share a name or a link points at a page that doesn't exist.

Usage: python flatten-wiki.py <source-dir> <output-dir>
"""

import re
import sys
from pathlib import Path

LINK = re.compile(r"\]\(([^()\s]+)\)")
FENCE = re.compile(r"^\s*(```|~~~)")


def page_name(path: Path) -> str:
    return "Home" if path.stem == "README" else path.stem


def rewrite(target: str, source: Path, pages: dict[str, Path], errors: list[str]) -> str:
    if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I) or target.startswith("#"):
        return target
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
                lines[i] = LINK.sub(lambda m: f"]({rewrite(m.group(1), path, pages, errors)})", line)
        (output_root / f"{name}.md").write_text("".join(lines), encoding="utf-8")

    for error in errors:
        print(f"::error::{error}")
    print(f"Flattened {len(pages)} pages into {output_root}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
