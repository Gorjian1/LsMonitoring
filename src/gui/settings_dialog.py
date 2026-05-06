"""Settings dialog — gateway connection, thresholds, alarm behavior."""
from __future__ import annotations

from PySide6.QtWidgets import (
    QCheckBox, QComboBox, QDialog, QDialogButtonBox, QDoubleSpinBox, QFormLayout,
    QGroupBox, QLineEdit, QSpinBox, QTabWidget, QVBoxLayout, QWidget,
)

from ..config import AppConfig
from ..i18n import t


class SettingsDialog(QDialog):
    def __init__(self, cfg: AppConfig, parent=None):
        super().__init__(parent)
        self.setWindowTitle(t("settings"))
        self.cfg = cfg
        self.setMinimumWidth(440)

        tabs = QTabWidget()
        tabs.addTab(self._build_connection_tab(), t("gateway_ip").split(" ")[0] if False else "Connection")
        tabs.addTab(self._build_thresholds_tab(), t("thresholds"))
        tabs.addTab(self._build_alarm_tab(), t("alarm_actions"))

        buttons = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        buttons.button(QDialogButtonBox.Save).setText(t("save"))
        buttons.button(QDialogButtonBox.Cancel).setText(t("cancel"))
        buttons.accepted.connect(self._save_and_accept)
        buttons.rejected.connect(self.reject)

        layout = QVBoxLayout(self)
        layout.addWidget(tabs)
        layout.addWidget(buttons)

    def _build_connection_tab(self) -> QWidget:
        w = QWidget()
        f = QFormLayout(w)
        self.ip_edit = QLineEdit(self.cfg.connection.gateway_ip)
        self.user_edit = QLineEdit(self.cfg.connection.username)
        self.pw_edit = QLineEdit(self.cfg.connection.password)
        self.pw_edit.setEchoMode(QLineEdit.Password)
        self.poll_spin = QSpinBox()
        self.poll_spin.setRange(5, 300)
        self.poll_spin.setValue(self.cfg.connection.polling_interval_s)
        self.poll_spin.setSuffix(" s")
        self.buf_spin = QSpinBox()
        self.buf_spin.setRange(100, 100000)
        self.buf_spin.setValue(self.cfg.plot_buffer_points)

        f.addRow(t("gateway_ip"), self.ip_edit)
        f.addRow(t("username"), self.user_edit)
        f.addRow(t("password"), self.pw_edit)
        f.addRow(t("polling_seconds"), self.poll_spin)
        f.addRow(t("buffer_points"), self.buf_spin)
        return w

    def _build_thresholds_tab(self) -> QWidget:
        w = QWidget()
        outer = QVBoxLayout(w)
        f = QFormLayout()

        self.mode_cb = QComboBox()
        self.mode_cb.addItem(t("absolute"), "absolute")
        self.mode_cb.addItem(t("variation"), "variation")
        idx = 0 if self.cfg.thresholds.mode == "absolute" else 1
        self.mode_cb.setCurrentIndex(idx)

        self.same_cb = QCheckBox(t("same_for_ab"))
        self.same_cb.setChecked(self.cfg.thresholds.same_for_ab)

        def _spin(val: float) -> QDoubleSpinBox:
            s = QDoubleSpinBox()
            s.setRange(0.0, 90.0)
            s.setDecimals(3)
            s.setSingleStep(0.1)
            s.setSuffix(" °")
            s.setValue(val)
            return s

        self.warn_a = _spin(self.cfg.thresholds.warning_a)
        self.crit_a = _spin(self.cfg.thresholds.critical_a)
        self.warn_b = _spin(self.cfg.thresholds.warning_b)
        self.crit_b = _spin(self.cfg.thresholds.critical_b)

        f.addRow(t("threshold_mode"), self.mode_cb)
        f.addRow("", self.same_cb)
        f.addRow(f"A — {t('warning')}", self.warn_a)
        f.addRow(f"A — {t('critical')}", self.crit_a)
        f.addRow(f"B — {t('warning')}", self.warn_b)
        f.addRow(f"B — {t('critical')}", self.crit_b)

        self.same_cb.toggled.connect(self._sync_b_enabled)
        self._sync_b_enabled(self.same_cb.isChecked())

        outer.addLayout(f)
        return w

    def _sync_b_enabled(self, same: bool) -> None:
        self.warn_b.setEnabled(not same)
        self.crit_b.setEnabled(not same)

    def _build_alarm_tab(self) -> QWidget:
        w = QWidget()
        outer = QVBoxLayout(w)

        actions = QGroupBox(t("alarm_actions"))
        af = QFormLayout(actions)
        self.sound_cb = QCheckBox(t("sound"))
        self.sound_cb.setChecked(self.cfg.alarm.sound)
        self.popup_cb = QCheckBox(t("popup"))
        self.popup_cb.setChecked(self.cfg.alarm.popup)
        self.log_cb = QCheckBox(t("log"))
        self.log_cb.setChecked(self.cfg.alarm.log_to_csv)
        af.addRow(self.sound_cb)
        af.addRow(self.popup_cb)
        af.addRow(self.log_cb)

        invalid = QGroupBox(t("invalid_behavior"))
        inv_f = QFormLayout(invalid)
        self.invalid_cb = QComboBox()
        self.invalid_cb.addItem(t("invalid_hide"), "hide")
        self.invalid_cb.addItem(t("invalid_mark"), "mark")
        self.invalid_cb.addItem(t("invalid_alarm"), "alarm")
        idx = {"hide": 0, "mark": 1, "alarm": 2}.get(self.cfg.alarm.invalid_behavior, 1)
        self.invalid_cb.setCurrentIndex(idx)
        self.invalid_min = QSpinBox()
        self.invalid_min.setRange(1, 1440)
        self.invalid_min.setValue(self.cfg.alarm.invalid_alarm_minutes)
        self.invalid_min.setSuffix(" min")
        inv_f.addRow(t("invalid_behavior"), self.invalid_cb)
        inv_f.addRow(t("invalid_alarm"), self.invalid_min)

        outer.addWidget(actions)
        outer.addWidget(invalid)
        outer.addStretch(1)
        return w

    def _save_and_accept(self) -> None:
        self.cfg.connection.gateway_ip = self.ip_edit.text().strip()
        self.cfg.connection.username = self.user_edit.text().strip()
        self.cfg.connection.password = self.pw_edit.text()
        self.cfg.connection.polling_interval_s = int(self.poll_spin.value())
        self.cfg.plot_buffer_points = int(self.buf_spin.value())

        self.cfg.thresholds.mode = self.mode_cb.currentData()
        self.cfg.thresholds.same_for_ab = self.same_cb.isChecked()
        self.cfg.thresholds.warning_a = float(self.warn_a.value())
        self.cfg.thresholds.critical_a = float(self.crit_a.value())
        self.cfg.thresholds.warning_b = float(self.warn_b.value())
        self.cfg.thresholds.critical_b = float(self.crit_b.value())

        self.cfg.alarm.sound = self.sound_cb.isChecked()
        self.cfg.alarm.popup = self.popup_cb.isChecked()
        self.cfg.alarm.log_to_csv = self.log_cb.isChecked()
        self.cfg.alarm.invalid_behavior = self.invalid_cb.currentData()
        self.cfg.alarm.invalid_alarm_minutes = int(self.invalid_min.value())

        self.cfg.save()
        self.accept()
