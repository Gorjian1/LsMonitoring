"""PyInstaller build helper.

Run:  python build_exe.py

Produces dist/LSMonitoring.exe (single-file, windowed).
"""
from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
APP_NAME = "LSMonitoring"


def main() -> int:
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("Installing PyInstaller…")
        subprocess.check_call([sys.executable, "-m", "pip", "install", "pyinstaller"])

    # Clean previous build
    for d in ("build", "dist"):
        p = ROOT / d
        if p.exists():
            shutil.rmtree(p)
    spec = ROOT / f"{APP_NAME}.spec"
    if spec.exists():
        spec.unlink()

    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onefile",
        "--windowed",
        "--name", APP_NAME,
        "--collect-submodules", "PySide6",
        "--collect-submodules", "pyqtgraph",
        "--hidden-import", "openpyxl",
        "--hidden-import", "pandas",
        str(ROOT / "main.py"),
    ]
    print(" ".join(cmd))
    return subprocess.call(cmd, cwd=ROOT)


if __name__ == "__main__":
    sys.exit(main())
