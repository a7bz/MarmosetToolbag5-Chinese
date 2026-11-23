"""Assemble the delivery folder from the build outputs.

Produces `release/`:

  * Toolbag5_汉化补丁.exe       — the standard installer
  * Toolbag5_汉化补丁_绿色版.zip — the portable script package: the patch script plus the
                                   patch data (dist/patch.json) and the translated english.txt.
                                   It does NOT contain the game executable, so the zip stays small
                                   and the repository never ships Marmoset's binary.
  * 使用说明.txt                — end-user instructions

Everything here is generated; run tools/build_release.py first.
"""
from __future__ import annotations

import shutil
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
GREEN = ROOT / "green"
OUT = ROOT / "release"

INSTALLER = "Toolbag5_汉化补丁.exe"
ZIP_NAME = "Toolbag5_汉化补丁_绿色版.zip"
ZIP_ROOT = "Toolbag5_汉化补丁_绿色版"

# Launcher scripts and the patch engine come from the repo; the patch data and the translated
# text come from dist/.
REPO_FILES = [
    "安装.bat",
    "卸载.bat",
    "切换到中文.bat",
    "切换到英文.bat",
    "查看状态.bat",
    "使用说明.txt",
    "tools/patch.ps1",
]
DIST_FILES = [
    "patch.json",
    "english.txt",
]


def build_zip() -> Path:
    out = OUT / ZIP_NAME
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel in REPO_FILES:
            src = GREEN / rel
            if not src.exists():
                raise SystemExit(f"missing for zip: {src}")
            z.write(src, f"{ZIP_ROOT}/{rel}")
        for rel in DIST_FILES:
            src = DIST / rel
            if not src.exists():
                raise SystemExit(f"missing for zip: {src}\nrun tools/build_release.py first")
            z.write(src, f"{ZIP_ROOT}/dist/{rel}")
    return out


def main() -> None:
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)

    installer = DIST / INSTALLER
    if not installer.exists():
        raise SystemExit(f"missing installer: {installer}\nrun tools/build_release.py first")
    shutil.copy2(installer, OUT / INSTALLER)

    z = build_zip()
    shutil.copy2(GREEN / "使用说明.txt", OUT / "使用说明.txt")

    for p in sorted(OUT.iterdir()):
        print(f"  {p.name:44} {p.stat().st_size:>12,} bytes")


if __name__ == "__main__":
    main()
