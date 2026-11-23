"""Check that the committed build data is self-consistent.

`dist/patch.json` and `dist/english.txt` are committed as the compiled translation so the
installer can be built without the game installed. This verifies they agree:

  * english.txt hashes to the value recorded in patch.json
  * every patch's old bytes differ from its new bytes, and no two patches overlap
  * the length-immediate edits are inside the file and carry sane values

Run by the release workflow before building, and useful locally after editing translations.

Usage: python tools/check_data.py
"""
from __future__ import annotations

import base64
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"


def main() -> None:
    patch = json.loads((DIST / "patch.json").read_text(encoding="utf-8"))
    text = (DIST / "english.txt").read_bytes()

    got = hashlib.sha256(text).hexdigest().upper()
    want = patch["text_sha256_new"].upper()
    print(f"english.txt sha   : {got}")
    print(f"patch.json expects: {want}")
    if got != want:
        raise SystemExit("dist/english.txt does not match the patch data")

    patches = patch["patches"]
    print(f"patches           : {len(patches)}")

    spans = []
    identical = 0
    for off, old_b64, new_b64 in patches:
        off = int(off)
        old = base64.b64decode(old_b64)
        new = base64.b64decode(new_b64)
        if old == new:
            identical += 1
        if not old:
            raise SystemExit(f"empty old bytes at {off:#x}")
        spans.append((off, off + len(old)))

    spans.sort()
    for (a_start, a_end), (b_start, _b_end) in zip(spans, spans[1:]):
        if b_start < a_end:
            raise SystemExit(f"overlapping patches at {b_start:#x}")

    if identical:
        raise SystemExit(f"{identical} patches change nothing")
    print("patch spans       : non-overlapping, all change bytes")

    edits = patch.get("len_edits") or []
    size = int(patch["exe_size"])
    for imm, old_v, new_v in edits:
        imm = int(imm)
        if not (0 <= imm < size - 4):
            raise SystemExit(f"length immediate out of range: {imm:#x}")
        if new_v < old_v:
            raise SystemExit(f"length immediate shrinks at {imm:#x}: {old_v} -> {new_v}")
    print(f"len_edits         : {len(edits)} (all in range, none shrink)")

    print("data OK")


if __name__ == "__main__":
    try:
        main()
    except AssertionError as e:
        sys.exit(str(e))
