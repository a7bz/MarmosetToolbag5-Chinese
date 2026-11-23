"""Build the release artifacts from committed data only.

This needs neither the installed game nor the original executable: it takes the compiled
translation data (`dist/patch.json`) plus the translated text (`dist/english.txt`) and produces

    dist/payload.bin(.gz)          binary payload embedded in the installer
    dist/Toolbag5_汉化补丁.exe      the installer
    release/…                          the delivery folder (installer, portable zip, readme)

This is what the release workflow runs, so the published artifacts are reproducible from the
repository alone.

Usage:
    python tools/build_release.py
"""
from __future__ import annotations

import base64
import gzip
import hashlib
import json
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
PATCH_JSON = DIST / "patch.json"
TEXT = DIST / "english.txt"

MAGIC = b"TBZ2"


def load_patch() -> dict:
    if not PATCH_JSON.exists():
        raise SystemExit(
            f"missing {PATCH_JSON}\n"
            "This file holds the compiled translation and is committed on purpose."
        )
    if not TEXT.exists():
        raise SystemExit(f"missing {TEXT}\nrun tools/build_text.py first")
    return json.loads(PATCH_JSON.read_text(encoding="utf-8"))


def build_payload(patch: dict) -> bytes:
    """Serialise the patch set into the TBZ2 container the installer parses."""
    text = TEXT.read_bytes()
    orig_exe_sha = bytes.fromhex(patch["exe_sha256_orig"])
    orig_text_sha = bytes.fromhex(patch["text_sha256_orig"])

    out = bytearray()
    out += MAGIC
    out += struct.pack("<i", len(patch["patches"]))
    for off, old_b64, new_b64 in patch["patches"]:
        old = base64.b64decode(old_b64)
        new = base64.b64decode(new_b64)
        out += struct.pack("<i", int(off))
        out += struct.pack("<i", len(old)) + old
        out += struct.pack("<i", len(new)) + new

    out += struct.pack("<i", len(text)) + text
    ver = patch["app_version"].encode("utf-8")
    out += struct.pack("<i", len(ver)) + ver
    out += struct.pack("<i", int(patch["exe_size"]))
    out += struct.pack("<i", 32) + orig_exe_sha
    out += struct.pack("<i", 32) + orig_text_sha

    # v2 trailer: length immediates that must be widened with the strings
    edits = patch.get("len_edits") or []
    out += struct.pack("<i", len(edits))
    for imm, old_v, new_v in edits:
        out += struct.pack("<iii", int(imm), int(old_v), int(new_v))

    return bytes(out)


def verify_payload(blob: bytes, patch: dict) -> None:
    """Round-trip the payload so a malformed build can never be published."""
    assert blob[:4] == MAGIC
    pos = 4
    n = struct.unpack_from("<i", blob, pos)[0]
    pos += 4
    assert n == len(patch["patches"]), "patch count mismatch"

    for i in range(n):
        off = struct.unpack_from("<i", blob, pos)[0]
        pos += 4
        ol = struct.unpack_from("<i", blob, pos)[0]
        pos += 4
        old = blob[pos:pos + ol]
        pos += ol
        nl = struct.unpack_from("<i", blob, pos)[0]
        pos += 4
        new = blob[pos:pos + nl]
        pos += nl
        assert off == int(patch["patches"][i][0]), f"patch {i} offset"
        assert old == base64.b64decode(patch["patches"][i][1]), f"patch {i} old"
        assert new == base64.b64decode(patch["patches"][i][2]), f"patch {i} new"

    tl = struct.unpack_from("<i", blob, pos)[0]
    pos += 4
    assert blob[pos:pos + tl] == TEXT.read_bytes(), "text mismatch"
    pos += tl
    vl = struct.unpack_from("<i", blob, pos)[0]
    pos += 4
    assert blob[pos:pos + vl].decode("utf-8") == patch["app_version"], "version mismatch"
    pos += vl
    pos += 4                                     # original exe size
    pos += 4 + 32                                # exe sha256
    pos += 4 + 32                                # text sha256
    n_imm = struct.unpack_from("<i", blob, pos)[0]
    pos += 4
    assert n_imm == len(patch.get("len_edits") or []), "length-immediate count mismatch"
    pos += n_imm * 12
    assert pos == len(blob), f"trailing bytes: {len(blob) - pos}"


def find_csc() -> Path:
    candidates = [
        Path(r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    ]
    for c in candidates:
        if c.exists():
            return c
    raise SystemExit(
        "no C# compiler found; install Visual Studio Build Tools (Roslyn csc is required, "
        "the in-box .NET Framework compiler is too old for C# 7.3)"
    )


def find_reference_assemblies() -> Path:
    base = Path(r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework")
    for v in ("v4.8", "v4.7.2", "v4.7.1", "v4.7", "v4.6.2"):
        p = base / v
        if p.exists():
            return p
    raise SystemExit("no .NET Framework reference assemblies found")


def compile_installer() -> Path:
    csc = find_csc()
    ref = find_reference_assemblies()

    exe_out = DIST / "Toolbag5_汉化补丁.exe"
    cmd = [
        str(csc), "/nologo", "/target:winexe", "/platform:anycpu", "/optimize+",
        "/langversion:7.3",
        "/win32manifest:" + str(ROOT / "installer" / "app.manifest"),
        "/win32icon:" + str(ROOT / "installer" / "app.ico"),
        f"/r:{ref / 'System.dll'}",
        f"/r:{ref / 'System.Core.dll'}",
        f"/r:{ref / 'System.Drawing.dll'}",
        f"/r:{ref / 'System.Windows.Forms.dll'}",
        f"/r:{ref / 'System.IO.Compression.dll'}",
        f"/r:{ref / 'System.IO.Compression.FileSystem.dll'}",
        "/resource:" + str(DIST / "payload.bin.gz") + ",payload",
        "/out:" + str(exe_out),
        str(ROOT / "installer" / "Setup.cs"),
    ]
    print("compiling installer…")
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(r.stdout or "")
        print(r.stderr or "")
        raise SystemExit("compile failed")
    print(f"wrote {exe_out.name}  {exe_out.stat().st_size} bytes")
    return exe_out


def main() -> None:
    # The Windows console is often GBK; avoid crashing on the Chinese file name in subprocess
    # output.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    patch = load_patch()
    print(f"patches: {len(patch['patches'])}  len_edits: {len(patch.get('len_edits') or [])}")

    blob = build_payload(patch)
    verify_payload(blob, patch)
    print(f"payload: {len(blob)} bytes  round-trip OK")

    (DIST / "payload.bin").write_bytes(blob)
    with gzip.GzipFile(filename="", mode="wb", fileobj=open(DIST / "payload.bin.gz", "wb"),
                       compresslevel=9, mtime=0) as gz:
        gz.write(blob)
    gz_size = (DIST / "payload.bin.gz").stat().st_size
    print(f"payload.bin.gz: {gz_size} bytes")

    compile_installer()

    # package the delivery folder (installer + portable zip + readme)
    r = subprocess.run([sys.executable, str(ROOT / "tools" / "package.py")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(r.stdout or "")
    if r.returncode != 0:
        print(r.stderr or "")
        raise SystemExit("packaging failed")

    print("release artifacts ready")


if __name__ == "__main__":
    main()
