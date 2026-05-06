"""Entry point for both `python main.py` and the PyInstaller-built .exe."""
from __future__ import annotations

import sys

from PySide6.QtWidgets import QApplication

from src.config import AppConfig
from src.gui.main_window import MainWindow
from src.i18n import set_language
from src.logging_setup import setup_logging


def main() -> int:
    setup_logging()
    cfg = AppConfig.load()
    set_language(cfg.language)

    app = QApplication(sys.argv)
    app.setApplicationName("LS-G6 Monitoring")
    win = MainWindow(cfg)
    win.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
