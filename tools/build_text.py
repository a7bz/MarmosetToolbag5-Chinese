"""Build the translated english.txt from per-batch translations.

Reads work/batch_N.zh.json (English -> Chinese maps) and rebuilds english.txt
preserving every original line, key, separator and ordering; only values change.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORK = ROOT / "work"
ORIG = ROOT / "backup" / "english.txt.orig"


def load_map() -> dict[str, str]:
    m: dict[str, str] = {}
    for i in range(8):
        p = WORK / f"batch_{i}.zh.json"
        if not p.exists():
            print(f"  ! missing {p.name}")
            continue
        d = json.loads(p.read_text(encoding="utf-8"))
        m.update(d)
        print(f"  {p.name}: {len(d)}")
    return m


def main() -> None:
    if not ORIG.exists():
        raise SystemExit("backup/english.txt.orig missing")
    tr = load_map()
    print("total translations:", len(tr))

    raw = ORIG.read_bytes().decode("utf-8")
    lines = raw.split("\r\n")

    out = []
    changed = 0
    missing = 0
    for line in lines:
        if not line or line.startswith(";") or ";" not in line:
            out.append(line)
            continue
        k, _, v = line.partition(";")
        if v in tr:
            nv = tr[v]
            if nv != v:
                changed += 1
            out.append(k + ";" + nv)
        else:
            missing += 1
            out.append(line)

    print(f"changed lines: {changed}  untranslated: {missing}")

    dst = ROOT / "dist" / "english.txt"
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_bytes("\r\n".join(out).encode("utf-8"))
    print("wrote", dst, dst.stat().st_size, "bytes")

    # verify structural integrity
    new = dst.read_bytes().decode("utf-8").split("\r\n")
    assert len(new) == len(lines), f"line count changed: {len(new)} vs {len(lines)}"
    for a, b in zip(lines, new):
        if ";" in a and not a.startswith(";"):
            assert a.split(";", 1)[0] == b.split(";", 1)[0], f"key changed: {a!r} -> {b!r}"
    print("structure verified: line count and keys unchanged")


if __name__ == "__main__":
    main()
