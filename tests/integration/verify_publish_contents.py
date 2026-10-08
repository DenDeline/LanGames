"""Reject development settings and debug symbols from release publications."""

import sys
from pathlib import Path


publish_dir = Path(sys.argv[1])
if not publish_dir.is_dir():
    raise SystemExit(f"Publish directory does not exist: {publish_dir}")

excluded_names = {"appsettings.Development.json", ".DS_Store"}
excluded_suffixes = {".pdb", ".dbg", ".dsym"}
unexpected = [
    path.relative_to(publish_dir)
    for path in publish_dir.rglob("*")
    if path.name in excluded_names or path.suffix.lower() in excluded_suffixes
]

if unexpected:
    raise SystemExit("Unexpected release files: " + ", ".join(map(str, unexpected)))

print("Release publication contains no development settings or debug symbols")
