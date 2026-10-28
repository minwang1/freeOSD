import ctypes
import json
import logging
from logging.handlers import RotatingFileHandler
import os
import re
import sys
import time
from datetime import datetime
from ctypes import wintypes
from dataclasses import dataclass

from PySide6.QtCore import (
    Qt, QEvent, QSettings, QTimer, QObject, QRunnable, QThreadPool, Signal, Slot, QStandardPaths
)
from PySide6.QtGui import QAction
from PySide6.QtWidgets import (
    QApplication, QComboBox, QFormLayout, QGroupBox, QHBoxLayout, QLabel,
    QMainWindow, QMessageBox, QPushButton, QSlider, QSpinBox, QVBoxLayout,
    QWidget, QCheckBox, QSystemTrayIcon, QMenu, QInputDialog,
    QScrollArea, QFrame, QDialog, QDialogButtonBox, QFileDialog, QLineEdit, QListWidget,
    QListWidgetItem, QPlainTextEdit
)
from identification import analyze_osd_controls, compare_snapshots, format_analysis_summary
from vcp_test import read_vcp_with_fallback, run_vcp_value_test
from display_settings import (
    available_refresh_rates, get_hdr_state, restore_display_mode,
    set_hdr_state, set_refresh_rate,
)

if sys.platform != "win32":
    raise SystemExit("This application must run on native Windows, not WSL.")

user32 = ctypes.WinDLL("user32", use_last_error=True)
dxva2 = ctypes.WinDLL("dxva2", use_last_error=True)

MONITORENUMPROC = ctypes.WINFUNCTYPE(
    wintypes.BOOL, wintypes.HANDLE, wintypes.HDC,
    ctypes.POINTER(wintypes.RECT), wintypes.LPARAM
)

class MONITORINFOEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
        ("szDevice", wintypes.WCHAR * 32),
    ]

class PHYSICAL_MONITOR(ctypes.Structure):
    _fields_ = [
        ("hPhysicalMonitor", wintypes.HANDLE),
        ("szPhysicalMonitorDescription", wintypes.WCHAR * 128),
    ]

user32.EnumDisplayMonitors.argtypes = [
    wintypes.HDC, ctypes.POINTER(wintypes.RECT), MONITORENUMPROC, wintypes.LPARAM
]
user32.EnumDisplayMonitors.restype = wintypes.BOOL
user32.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MONITORINFOEXW)]
user32.GetMonitorInfoW.restype = wintypes.BOOL
user32.GetForegroundWindow.argtypes = []
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL

class TaskSignals(QObject):
    finished = Signal(int, object, object)
    progress = Signal(int, object)

class FunctionTask(QRunnable):
    def __init__(self, task_id, function, signals, reports_progress=False):
        super().__init__()
        self.task_id = task_id
        self.function = function
        self.signals = signals
        self.reports_progress = reports_progress

    def run(self):
        try:
            if self.reports_progress:
                report = lambda message: self.signals.progress.emit(self.task_id, message)
                result = self.function(report)
            else:
                result = self.function()
            self.signals.finished.emit(self.task_id, result, None)
        except Exception as exc:
            self.signals.finished.emit(self.task_id, None, exc)

class JsonLogFormatter(logging.Formatter):
    def format(self, record):
        entry = {
            "time": self.formatTime(record, "%Y-%m-%dT%H:%M:%S"),
            "level": record.levelname,
            "event": record.getMessage(),
        }
        if record.exc_info:
            entry["exception"] = self.formatException(record.exc_info)
        return json.dumps(entry, ensure_ascii=False)

def configure_logging():
    app_data = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppDataLocation)
    os.makedirs(app_data, exist_ok=True)
    logger = logging.getLogger("monitor_control")
    logger.setLevel(logging.INFO)
    if not logger.handlers:
        handler = RotatingFileHandler(
            os.path.join(app_data, "monitor-control.log"), maxBytes=2_000_000, backupCount=3, encoding="utf-8"
        )
        handler.setFormatter(JsonLogFormatter())
        logger.addHandler(handler)
    return logger

logger = logging.getLogger("monitor_control")

APP_STYLESHEET = """
QMainWindow, QWidget {
    background: #f4f6fa;
    color: #202633;
    font-family: "Segoe UI", sans-serif;
    font-size: 10pt;
}
QLabel { background: transparent; }
QLabel#pageTitle { font-size: 22pt; font-weight: 600; color: #172033; }
QLabel#pageSubtitle { color: #667085; font-size: 10pt; }
QGroupBox {
    background: #ffffff;
    border: 1px solid #e2e7ef;
    border-radius: 10px;
    margin-top: 12px;
    padding: 14px 14px 12px 14px;
    font-weight: 600;
}
QGroupBox::title {
    subcontrol-origin: margin;
    left: 12px;
    padding: 0 5px;
    color: #344054;
}
QComboBox, QSpinBox {
    background: #ffffff;
    border: 1px solid #cfd6e2;
    border-radius: 6px;
    min-height: 32px;
    padding: 0 9px;
    selection-background-color: #1769d2;
}
QComboBox:hover, QSpinBox:hover { border-color: #8ca8cc; }
QComboBox:focus, QSpinBox:focus { border: 2px solid #1769d2; }
QComboBox::drop-down { border: 0; width: 28px; }
QPushButton {
    background: #ffffff;
    border: 1px solid #cfd6e2;
    border-radius: 6px;
    min-height: 34px;
    padding: 0 13px;
    font-weight: 500;
}
QPushButton:hover { background: #f2f6fc; border-color: #9bb2d1; }
QPushButton:pressed { background: #e8eef8; }
QPushButton:disabled { color: #98a2b3; background: #f4f5f7; border-color: #e4e7ec; }
QPushButton#primaryButton { background: #1769d2; color: #ffffff; border-color: #1769d2; }
QPushButton#primaryButton:hover { background: #0f5dbf; }
QCheckBox { spacing: 8px; }
QCheckBox::indicator { width: 17px; height: 17px; }
QSlider::groove:horizontal { height: 4px; background: #d9e0ea; border-radius: 2px; }
QSlider::sub-page:horizontal { background: #1769d2; border-radius: 2px; }
QSlider::handle:horizontal {
    background: #ffffff;
    border: 2px solid #1769d2;
    width: 14px;
    height: 14px;
    margin: -7px 0;
    border-radius: 8px;
}
QLabel#statusLabel {
    background: #ffffff;
    border: 1px solid #e2e7ef;
    border-radius: 7px;
    padding: 9px 12px;
    color: #475467;
}
QLabel#detailsLabel { color: #667085; font-size: 9pt; }
"""

dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)
]
dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR.restype = wintypes.BOOL
dxva2.GetPhysicalMonitorsFromHMONITOR.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(PHYSICAL_MONITOR)
]
dxva2.GetPhysicalMonitorsFromHMONITOR.restype = wintypes.BOOL
dxva2.DestroyPhysicalMonitors.argtypes = [wintypes.DWORD, ctypes.POINTER(PHYSICAL_MONITOR)]
dxva2.DestroyPhysicalMonitors.restype = wintypes.BOOL
dxva2.DestroyPhysicalMonitor.argtypes = [wintypes.HANDLE]
dxva2.DestroyPhysicalMonitor.restype = wintypes.BOOL
dxva2.GetCapabilitiesStringLength.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)
]
dxva2.GetCapabilitiesStringLength.restype = wintypes.BOOL
dxva2.CapabilitiesRequestAndCapabilitiesReply.argtypes = [
    wintypes.HANDLE, ctypes.c_char_p, wintypes.DWORD
]
dxva2.CapabilitiesRequestAndCapabilitiesReply.restype = wintypes.BOOL
dxva2.GetVCPFeatureAndVCPFeatureReply.argtypes = [
    wintypes.HANDLE, wintypes.BYTE, ctypes.POINTER(wintypes.DWORD),
    ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(wintypes.DWORD)
]
dxva2.GetVCPFeatureAndVCPFeatureReply.restype = wintypes.BOOL
dxva2.SetVCPFeature.argtypes = [wintypes.HANDLE, wintypes.BYTE, wintypes.DWORD]
dxva2.SetVCPFeature.restype = wintypes.BOOL

FEATURES = {
    0x10: "Brightness",
    0x12: "Contrast",
    0x14: "Color preset",
    0x16: "Red gain",
    0x18: "Green gain",
    0x1A: "Blue gain",
    0x60: "Input source",
    0xAA: "Screen orientation",
    0xAC: "Sharpness",
    0xD6: "Power mode (read-only in this app)",
    0xDF: "VCP version (read-only in this app)",
}
VCP_TEST_SETTLE_SECONDS = 4.0
PRESET_NAMES = {
    0x01: "sRGB",
    0x04: "5000 K",
    0x05: "6500 K",
    0x06: "7500 K",
    0x08: "9300 K",
    0x0B: "User 1",
    0x0C: "User 2",
}
INPUT_NAMES = {
    0x0F: "DisplayPort 1",
    0x10: "DisplayPort 2 / DP (monitor-dependent)",
    0x11: "HDMI (monitor-dependent)",
    0x12: "DVI (monitor-dependent)",
    0x1B: "HDMI 2 (monitor-dependent)",
}

OSD_STATE_DEFAULTS = {
    "Brightness": ["Minimum", "25%", "50%", "75%", "Maximum", "Other / custom"],
    "Contrast": ["Minimum", "25%", "50%", "75%", "Maximum", "Other / custom"],
    "Color space / gamut": ["Standard", "sRGB", "Adobe RGB", "DCI-P3", "Rec.709", "Rec.2020", "Native", "User / custom", "Other / custom"],
    "Color preset / picture mode": ["Standard", "Movie / Cinema", "Game", "FPS", "RTS", "Photo", "Reader", "Eco", "User / custom", "Other / custom"],
    "FreeSync / Adaptive-Sync": ["Off", "On", "Auto", "Premium", "Premium Pro", "Other / custom"],
    "HDR": ["Off", "On", "Auto", "HDR10", "DisplayHDR", "Other / custom"],
    "OD / response time": ["Off", "Low", "Normal", "Fast", "Faster", "Extreme", "Other / custom"],
    "Local dimming": ["Off", "Low", "Medium", "High", "Auto", "Other / custom"],
    "Color temperature / white point": ["Warm", "Normal", "Cool", "5000 K", "6500 K", "7500 K", "9300 K", "User / custom", "Other / custom"],
    "Sharpness": ["Minimum", "Low", "Normal", "High", "Maximum", "Other / custom"],
    "Dynamic contrast": ["Off", "Low", "Medium", "High", "Auto", "Other / custom"],
    "Input source": ["DisplayPort", "HDMI 1", "HDMI 2", "USB-C", "DVI", "Other / custom"],
    "Other / custom": ["Off", "On", "Low", "Medium", "High", "Other / custom"],
}
OSD_CONTROL_DEFAULTS = list(OSD_STATE_DEFAULTS)

VCP_NAMES = {
    0x10: "Brightness", 0x12: "Contrast", 0x14: "Color preset",
    0x16: "Red gain", 0x18: "Green gain", 0x1A: "Blue gain",
    0x60: "Input source", 0x62: "Audio speaker volume",
    0xD6: "Power mode", 0xDF: "VCP version",
}
IDENTIFICATION_FALLBACK_CODES = sorted({
    0x10, 0x12, 0x14, 0x16, 0x18, 0x1A, 0x1C, 0x1E, 0x20,
    0x60, 0x62, 0xAA, 0xAC, 0xD6, 0xDF,
    *range(0xC0, 0x100),
})
IDENTIFICATION_VENDOR_CODES = range(0xC0, 0x100)

def profile_file_path():
    app_data = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppDataLocation)
    os.makedirs(app_data, exist_ok=True)
    return os.path.join(app_data, "profiles.json")

def load_profiles():
    path = profile_file_path()
    if not os.path.exists(path):
        return {}
    with open(path, "r", encoding="utf-8") as file:
        profiles = json.load(file)
    if not isinstance(profiles, dict):
        raise ValueError("Profiles file must contain a JSON object")
    return profiles

def save_profiles(profiles):
    path = profile_file_path()
    temporary_path = path + ".tmp"
    with open(temporary_path, "w", encoding="utf-8") as file:
        json.dump(profiles, file, ensure_ascii=False, indent=2)
    os.replace(temporary_path, path)

def automation_file_path():
    app_data = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppDataLocation)
    os.makedirs(app_data, exist_ok=True)
    return os.path.join(app_data, "automation.json")

def load_automation():
    path = automation_file_path()
    if not os.path.exists(path):
        return {"enabled": False, "rules": {}, "default_profile": None}
    with open(path, "r", encoding="utf-8") as file:
        data = json.load(file)
    if not isinstance(data, dict) or not isinstance(data.get("rules", {}), dict):
        raise ValueError("Automation file has an invalid format")
    return {
        "enabled": bool(data.get("enabled", False)),
        "rules": data.get("rules", {}),
        "default_profile": data.get("default_profile"),
    }

def save_automation(data):
    path = automation_file_path()
    temporary_path = path + ".tmp"
    with open(temporary_path, "w", encoding="utf-8") as file:
        json.dump(data, file, ensure_ascii=False, indent=2)
    os.replace(temporary_path, path)

def identified_controls_file_path():
    app_data = QStandardPaths.writableLocation(QStandardPaths.StandardLocation.AppDataLocation)
    os.makedirs(app_data, exist_ok=True)
    return os.path.join(app_data, "identified-controls.json")

def load_identified_controls():
    path = identified_controls_file_path()
    if not os.path.exists(path):
        return []
    with open(path, "r", encoding="utf-8") as file:
        data = json.load(file)
    if not isinstance(data, dict) or not isinstance(data.get("panels"), list):
        raise ValueError("Identified controls file has an invalid format")
    return [panel for panel in data["panels"] if isinstance(panel, dict)]

def save_identified_controls(panels):
    path = identified_controls_file_path()
    temporary_path = path + ".tmp"
    with open(temporary_path, "w", encoding="utf-8") as file:
        json.dump({"schema_version": 1, "panels": panels}, file, ensure_ascii=False, indent=2)
    os.replace(temporary_path, path)

def foreground_process_info():
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None, None
    process_id = wintypes.DWORD()
    if not user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id)):
        return None, None
    handle = kernel32.OpenProcess(0x1000, False, process_id.value)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None, process_id.value
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buffer))
        if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return None, process_id.value
        return os.path.basename(buffer.value).casefold(), process_id.value
    finally:
        kernel32.CloseHandle(handle)

def installed_applications():
    import winreg

    uninstall_path = r"Software\Microsoft\Windows\CurrentVersion\Uninstall"
    access_views = [0]
    access_views.extend(
        view for view in (getattr(winreg, "KEY_WOW64_64KEY", 0), getattr(winreg, "KEY_WOW64_32KEY", 0))
        if view
    )
    applications = {}
    for root in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        for view in access_views:
            try:
                uninstall_key = winreg.OpenKey(root, uninstall_path, 0, winreg.KEY_READ | view)
            except OSError:
                continue
            with uninstall_key:
                index = 0
                while True:
                    try:
                        subkey_name = winreg.EnumKey(uninstall_key, index)
                    except OSError:
                        break
                    index += 1
                    try:
                        with winreg.OpenKey(uninstall_key, subkey_name) as app_key:
                            display_name = winreg.QueryValueEx(app_key, "DisplayName")[0]
                    except OSError:
                        continue
                    try:
                        with winreg.OpenKey(uninstall_key, subkey_name) as app_key:
                            display_icon = winreg.QueryValueEx(app_key, "DisplayIcon")[0]
                    except OSError:
                        display_icon = ""
                    try:
                        with winreg.OpenKey(uninstall_key, subkey_name) as app_key:
                            system_component = winreg.QueryValueEx(app_key, "SystemComponent")[0]
                    except OSError:
                        system_component = 0
                    if system_component == 1 or not isinstance(display_name, str):
                        continue
                    icon_value = os.path.expandvars(str(display_icon).strip())
                    match = re.match(r'^\s*"?(.*?\.exe)"?(?:\s*,\s*-?\d+)?\s*$', icon_value, re.I)
                    if not match:
                        continue
                    executable = os.path.normpath(match.group(1).strip().strip('"'))
                    if not os.path.isfile(executable):
                        continue
                    process_name = os.path.basename(executable).casefold()
                    applications.setdefault(process_name, (display_name.strip(), executable))
    return sorted(
        ((name, process, path) for process, (name, path) in applications.items()),
        key=lambda app: app[0].casefold(),
    )

@dataclass
class MonitorRecord:
    display: str
    description: str
    handle: int
    capabilities: str
    supported: dict

def get_caps(handle):
    length = wintypes.DWORD()
    if not dxva2.GetCapabilitiesStringLength(handle, ctypes.byref(length)):
        return ""
    if length.value <= 0 or length.value > 65536:
        return ""
    buf = ctypes.create_string_buffer(length.value + 1)
    if not dxva2.CapabilitiesRequestAndCapabilitiesReply(handle, buf, length.value):
        return ""
    return buf.value.decode("ascii", errors="replace")

def parse_supported_vcp(caps):
    # Extract the balanced vcp(...) section because feature enum lists contain nested parentheses.
    start_match = re.search(r"vcp\(", caps, re.I)
    if not start_match:
        return {}
    body_start = start_match.end()
    depth = 1
    pos = body_start
    while pos < len(caps) and depth:
        if caps[pos] == "(":
            depth += 1
        elif caps[pos] == ")":
            depth -= 1
        pos += 1
    if depth != 0:
        return {}
    body = caps[body_start:pos - 1]
    result = {}
    token = re.compile(r"([0-9A-F]{2})(?:\(([^()]*)\))?", re.I)
    for match in token.finditer(body):
        code = int(match.group(1), 16)
        enum_text = match.group(2)
        enums = [int(x, 16) for x in re.findall(r"[0-9A-F]{2}", enum_text, re.I)] if enum_text else []
        result[code] = enums
    return result

def read_vcp(handle, code):
    typ, current, maximum = wintypes.DWORD(), wintypes.DWORD(), wintypes.DWORD()
    ok = dxva2.GetVCPFeatureAndVCPFeatureReply(
        handle, code, ctypes.byref(typ), ctypes.byref(current), ctypes.byref(maximum)
    )
    if not ok:
        raise OSError(ctypes.get_last_error(), f"Could not read VCP 0x{code:02X}")
    return {"type": typ.value, "current": current.value, "maximum": maximum.value}

def read_vcp_current(record, code, progress=None):
    def read_all_codes():
        values = {}
        for candidate_code in range(0x100):
            if progress and candidate_code % 32 == 0:
                progress(f"Full VCP scan in progress: checking codes from 0x{candidate_code:02X}…")
            try:
                values[candidate_code] = read_vcp(record.handle, candidate_code)["current"]
            except OSError:
                continue
        logger.info(
            "vcp_full_scan_completed monitor=%s display=%s readable_count=%d target=0x%02X found=%s",
            record.description, record.display, len(values), code, code in values,
        )
        return values

    return read_vcp_with_fallback(
        code=code,
        direct_read=lambda: read_vcp(record.handle, code)["current"],
        read_all=read_all_codes,
        logger=logger,
        progress=progress,
    )

def write_vcp(handle, code, value):
    if not dxva2.SetVCPFeature(handle, code, value):
        raise OSError(ctypes.get_last_error(), f"Could not write VCP 0x{code:02X}")

def enumerate_monitors():
    found = []
    @MONITORENUMPROC
    def callback(hmonitor, _hdc, _rect, _data):
        info = MONITORINFOEXW()
        info.cbSize = ctypes.sizeof(info)
        display = ""
        if user32.GetMonitorInfoW(hmonitor, ctypes.byref(info)):
            display = info.szDevice
        count = wintypes.DWORD()
        if not dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, ctypes.byref(count)):
            return True
        if not count.value:
            return True
        arr = (PHYSICAL_MONITOR * count.value)()
        if not dxva2.GetPhysicalMonitorsFromHMONITOR(hmonitor, count.value, arr):
            return True
        try:
            for pm in arr:
                handle = pm.hPhysicalMonitor
                caps = get_caps(handle)
                found.append(MonitorRecord(
                    display=display,
                    description=pm.szPhysicalMonitorDescription,
                    handle=handle,
                    capabilities=caps,
                    supported=parse_supported_vcp(caps),
                ))
        except Exception:
            dxva2.DestroyPhysicalMonitors(count.value, arr)
            raise
        return True

    if not user32.EnumDisplayMonitors(None, None, callback, 0):
        raise ctypes.WinError(ctypes.get_last_error())
    return found

def close_records(records):
    seen = set()
    for record in records or []:
        handle = record.handle
        key = handle if isinstance(handle, int) else getattr(handle, "value", None)
        if not key or key in seen:
            continue
        seen.add(key)
        if not dxva2.DestroyPhysicalMonitor(handle):
            logger.warning(
                "physical_monitor_close_failed handle=0x%X error=%d",
                key, ctypes.get_last_error(),
            )

def read_monitor_state(record):
    codes = (0x10, 0x12, 0x16, 0x18, 0x1A, 0x14, 0x60)
    state = {}
    for code in codes:
        if code not in record.supported:
            continue
        try:
            state[code] = read_vcp(record.handle, code)
        except OSError:
            state[code] = None
    return state

def capture_identification_state(record):
    """Read advertised features and probe vendor codes, with a fallback when caps are absent."""
    codes = set(record.supported)
    codes.update(IDENTIFICATION_VENDOR_CODES)
    if not record.supported:
        codes.update(IDENTIFICATION_FALLBACK_CODES)
    codes = sorted(codes)
    snapshot = {}
    for code in codes:
        key = f"0x{code:02X}"
        item = {
            "name": VCP_NAMES.get(code, "Unmapped / monitor-specific feature"),
            "advertised_values": [f"0x{value:02X}" for value in record.supported.get(code, [])],
            "probe_only": code not in record.supported,
        }
        try:
            item["read"] = read_vcp(record.handle, code)
        except OSError as exc:
            item["read_error"] = str(exc)
        snapshot[key] = item
    return snapshot

class MonitorIdentificationDialog(QDialog):
    def __init__(self, parent, record):
        super().__init__(parent)
        self.owner = parent
        self.record = record
        self.osd_controls = {}
        self.busy = False
        self.setWindowTitle("Monitor setting identification")
        self.setMinimumWidth(620)
        layout = QVBoxLayout(self)
        self.instructions = QLabel()
        self.instructions.setWordWrap(True)
        self.instructions.setText(
            "Choose an OSD option and its current state, then capture it. Change only that option on the monitor, "
            "choose the new state, and capture again. Repeat to record as many states as needed. "
            "The option and state lists have common defaults and accept custom text."
        )
        layout.addWidget(self.instructions)

        form = QFormLayout()
        self.osd_control = QComboBox()
        self.osd_control.setEditable(True)
        self.osd_control.addItems(OSD_CONTROL_DEFAULTS)
        self.osd_control.setCurrentText("Brightness")
        self.osd_control.setToolTip("Choose the monitor OSD menu option, or type its exact name")
        self.osd_state = QComboBox()
        self.osd_state.setEditable(True)
        self.osd_state.setToolTip("Choose or type the exact current value shown in the monitor OSD")
        form.addRow("OSD option", self.osd_control)
        form.addRow("OSD state", self.osd_state)
        layout.addLayout(form)
        self.state_hint = QLabel()
        self.state_hint.setObjectName("detailsLabel")
        self.state_hint.setWordWrap(True)
        layout.addWidget(self.state_hint)

        self.history = QListWidget()
        self.history.setMinimumHeight(180)
        layout.addWidget(self.history)
        buttons = QHBoxLayout()
        self.capture_button = QPushButton("Capture this state")
        self.capture_button.setObjectName("primaryButton")
        buttons.addWidget(self.capture_button)
        buttons.addStretch(1)
        self.finish_button = QPushButton("Finish and export…")
        layout.addLayout(buttons)
        buttons.addWidget(self.finish_button)
        self.capture_button.clicked.connect(self.capture_state)
        self.finish_button.clicked.connect(self.export_report)
        self.osd_control.activated.connect(self.update_state_choices)
        self.osd_control.lineEdit().editingFinished.connect(self.update_state_choices)
        self.update_state_choices()

    def _set_busy(self, busy):
        self.busy = busy
        self.osd_control.setEnabled(not busy)
        self.osd_state.setEnabled(not busy)
        self.history.setEnabled(not busy)
        self.capture_button.setEnabled(not busy)
        self.finish_button.setEnabled(not busy)

    def reject(self):
        if self.busy:
            return
        if self.osd_controls and QMessageBox.question(
            self, "Discard identification data?",
            "The captured data has not been exported. Close and discard it?",
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        ) != QMessageBox.Yes:
            return
        super().reject()

    def update_state_choices(self, *_args):
        option = self.osd_control.currentText().strip()
        states = OSD_STATE_DEFAULTS.get(option, OSD_STATE_DEFAULTS["Other / custom"])
        current_state = self.osd_state.currentText()
        self.osd_state.blockSignals(True)
        self.osd_state.clear()
        self.osd_state.addItems(states)
        if current_state and current_state in states:
            self.osd_state.setCurrentText(current_state)
        elif states:
            self.osd_state.setCurrentIndex(0)
        self.osd_state.blockSignals(False)
        if option in ("Brightness", "Contrast"):
            self.state_hint.setText(
                "Suggested percentage labels are approximate. Enter the exact value shown by this monitor if its scale differs."
            )
        else:
            self.state_hint.setText(
                "State labels are for analysis. Use the exact wording shown in the monitor OSD; custom values are accepted."
            )

    @staticmethod
    def _compare_snapshots(before, after):
        return compare_snapshots(before, after)

    def _refresh_history(self):
        entries = []
        for key, group in self.osd_controls.items():
            comparisons = []
            observations = group["observations"]
            for index, observation in enumerate(observations):
                comparison = None
                if index:
                    previous = observations[index - 1]
                    comparison = {
                        "from_state": previous["state"],
                        "to_state": observation["state"],
                        "from_captured_at": previous["captured_at"],
                        "to_captured_at": observation["captured_at"],
                        **self._compare_snapshots(previous["snapshot"], observation["snapshot"]),
                    }
                    comparisons.append(comparison)
                entries.append((observation["captured_at"], key, observation, comparison))
            group["comparisons"] = comparisons

        entries.sort(key=lambda entry: entry[0])
        self.history.clear()
        for captured_at, key, observation, comparison in entries:
            option = self.osd_controls[key]["osd_option"]
            changed_summary = "baseline"
            if comparison:
                changed_items = []
                for code, change in comparison["changed_vcp"].items():
                    before_value = change.get("before", {}).get("current")
                    after_value = change.get("after", {}).get("current")
                    if before_value is not None and after_value is not None:
                        changed_items.append(f"{code} {before_value}→{after_value}")
                    else:
                        changed_items.append(code)
                shown = ", ".join(changed_items[:5])
                if len(changed_items) > 5:
                    shown += f", +{len(changed_items) - 5} more"
                changed_summary = (
                    f"{len(changed_items)} changed VCP code(s) vs previous"
                    + (f": {shown}" if shown else "")
                )

            item = QListWidgetItem()
            observation_id = id(observation)
            item.setData(Qt.ItemDataRole.UserRole, (key, observation_id))
            self.history.addItem(item)
            row = QWidget()
            row_layout = QHBoxLayout(row)
            row_layout.setContentsMargins(6, 3, 6, 3)
            label = QLabel(f"{option} — {observation['state']} · {changed_summary}")
            label.setWordWrap(True)
            delete_button = QPushButton("Delete")
            delete_button.setToolTip("Delete this captured record")
            delete_button.clicked.connect(
                lambda _checked=False, record_key=key, record_id=observation_id:
                    self.delete_capture_record(record_key, record_id)
            )
            row_layout.addWidget(label, 1)
            row_layout.addWidget(delete_button)
            self.history.setItemWidget(item, row)
            item.setSizeHint(row.sizeHint())

    def delete_capture_record(self, key, observation_id):
        group = self.osd_controls.get(key)
        if not group:
            return
        group["observations"] = [
            observation for observation in group["observations"]
            if id(observation) != observation_id
        ]
        if not group["observations"]:
            del self.osd_controls[key]
        self._refresh_history()
        self.instructions.setText("Record deleted. Capture another state or continue with the remaining records.")

    def capture_state(self):
        option = self.osd_control.currentText().strip()
        state_label = self.osd_state.currentText().strip()
        if not option or not state_label:
            QMessageBox.information(self, "Enter OSD labels", "Choose or enter both an OSD option and its current state.")
            return
        self._set_busy(True)
        self.instructions.setText(
            f'Reacquiring the monitor and reading VCP values for "{option} — {state_label}"… '
            "The app will retry briefly if the display is reconnecting."
        )

        old_records = list(self.owner.records)
        previous_display = self.record.display
        previous_description = self.record.description
        def reacquire_and_capture():
            close_records(old_records)
            last_candidates = []
            last_error = None
            for attempt in range(10):
                try:
                    records = enumerate_monitors()
                except Exception as exc:
                    close_records([])
                    records = []
                    last_error = str(exc)
                if records:
                    exact = [candidate for candidate in records
                             if candidate.display == previous_display
                             and candidate.description == previous_description]
                    same_description = [candidate for candidate in records
                                        if candidate.description == previous_description]
                    same_display = [candidate for candidate in records
                                    if candidate.display == previous_display]
                    if exact:
                        record = exact[0]
                    elif len(same_description) == 1:
                        record = same_description[0]
                    elif len(same_display) == 1:
                        record = same_display[0]
                    elif len(records) == 1:
                        record = records[0]
                    else:
                        record = None
                    if record is not None:
                        try:
                            snapshot = capture_identification_state(record)
                        except Exception as exc:
                            return records, record, None, exc
                        return records, record, snapshot, None
                    last_candidates = [f"{candidate.description} — {candidate.display}" for candidate in records]
                    close_records(records)
                if attempt < 9:
                    time.sleep(0.8)
            detail = f" Last enumeration error: {last_error}." if last_error else ""
            detected = f" Detected: {', '.join(last_candidates)}." if last_candidates else " No physical monitor handles were returned."
            return [], None, None, RuntimeError(
                "Could not match the selected monitor after retrying. Wait for the picture to return; the app will rescan automatically. You can also press Rescan."
                + detected + detail
            )

        def completed(snapshot, error):
            self._set_busy(False)
            if snapshot is None:
                QMessageBox.warning(self, "Could not reacquire monitor", str(error))
                return
            records, record, values, capture_error = snapshot
            self.owner.records = records
            self.owner.current = record
            self.owner.refresh_identified_control_panels()
            if record is None:
                self.owner.set_controls_enabled(False)
                self.owner.info.setText(
                    f"{self.record.description} — reconnecting after a display signal change; rescanning automatically."
                )
                self.owner.status.setText("Monitor temporarily unavailable; waiting for it to reappear…")
                self.owner.schedule_monitor_recovery()
                QMessageBox.warning(self, "Monitor unavailable", str(capture_error))
                return
            self.record = record
            self.owner.monitor_combo.blockSignals(True)
            self.owner.monitor_combo.clear()
            for index, candidate in enumerate(records):
                self.owner.monitor_combo.addItem(f"{candidate.description} — {candidate.display}", index)
            self.owner.monitor_combo.setCurrentIndex(records.index(record))
            self.owner.monitor_combo.blockSignals(False)
            self.owner.info.setText(f"{record.description}  ·  {record.display}")
            if capture_error:
                QMessageBox.warning(self, "Could not read monitor", str(capture_error))
                return
            snapshot = values
            key = option.casefold()
            group = self.osd_controls.setdefault(key, {
                "osd_option": option,
                "observations": [],
                "comparisons": [],
            })
            observation = {
                "state": state_label,
                "captured_at": datetime.now().astimezone().isoformat(timespec="seconds"),
                "snapshot": snapshot,
            }
            group["observations"].append(observation)
            self._refresh_history()
            self.instructions.setText(
                f'Captured "{option} — {state_label}". Change only this OSD option and capture another state, '
                "or select a different option to start another comparison group."
            )
        self.owner._submit_task(reacquire_and_capture, completed)

    def export_report(self):
        if not self.osd_controls:
            QMessageBox.information(self, "No captured data", "Capture at least one labeled OSD state before exporting.")
            return
        suggested = f"monitor-identification-{datetime.now().strftime('%Y%m%d-%H%M%S')}.json"
        path, _ = QFileDialog.getSaveFileName(self, "Save identification report", suggested, "JSON files (*.json)")
        if not path:
            return
        if not path.lower().endswith(".json"):
            path += ".json"
        report = {
            "schema_version": 2,
            "created_at": datetime.now().astimezone().isoformat(timespec="seconds"),
            "application": "Monitor Control — DDC/CI",
            "monitor": {"description": self.record.description, "display": self.record.display},
            "capabilities_raw": self.record.capabilities,
            "capture_mode": "advertised_plus_vendor_probe" if self.record.supported else "fallback_probe",
            "advertised_features": {
                f"0x{code:02X}": {
                    "name": VCP_NAMES.get(code, "Unmapped / monitor-specific feature"),
                    "values": [f"0x{value:02X}" for value in values],
                }
                for code, values in sorted(self.record.supported.items())
            },
            "osd_controls": list(self.osd_controls.values()),
            "analysis": analyze_osd_controls(list(self.osd_controls.values())),
            "analysis_guide": [
                "Each observation is a labeled OSD option state and a complete VCP snapshot. Each comparison is between consecutive observations for the same OSD option.",
                "Snapshots read all advertised VCP feature codes and probe VCP codes 0xC0–0xFF for vendor-specific controls. If no capability string was returned, the app also probes common control codes; probe_only marks codes that were not advertised.",
                "A read contains type, current, and maximum as returned by Windows DXVA2. Values are raw integers; enum values are not assumed to have universal meanings.",
                "changed_vcp lists codes whose current, maximum, or type changed; current_difference is arithmetic only and must not be treated as semantic distance for enumerated values. unchanged_readable_codes and unreadable_codes are also included.",
                "Use the user-supplied OSD option and state labels to associate raw changes with the monitor menu. Change one OSD option at a time. Repeatedly capturing the same state can help check consistency.",
                "Some monitors do not advertise or expose HDR, Adaptive-Sync/FreeSync, or OD through DDC/CI. Such controls may have no corresponding snapshot delta; use the raw capability string and monitor model as context, and do not infer a standard VCP mapping without evidence.",
                "One VCP code may represent several OSD states, and an OSD option may change multiple VCP codes. Analyze the full state-to-code table and label uncertain or vendor-specific mappings explicitly.",
                "For analysis, provide the monitor model, capabilities_raw, and osd_controls to ChatGPT. Ask it to infer likely mappings from repeated labeled states and distinguish advertised, probed, readable, changed, and uncertain codes.",
            ],
        }
        try:
            temporary_path = path + ".tmp"
            with open(temporary_path, "w", encoding="utf-8") as file:
                json.dump(report, file, ensure_ascii=False, indent=2)
            os.replace(temporary_path, path)
        except OSError as exc:
            QMessageBox.warning(self, "Could not save report", str(exc))
            return
        self.accept()

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Monitor Control — DDC/CI")
        self.settings = QSettings("MonitorControl", "MonitorControl")
        self.resize(920, 780)
        self.setMinimumSize(760, 620)
        self._allow_close = False
        self._identification_active = False
        self._identification_recovery_active = False
        self.records = []
        self.current = None
        self._updating = False
        self._last_external_process = None
        self._task_pool = QThreadPool(self)
        self._task_pool.setMaxThreadCount(1)
        self._task_signals = TaskSignals(self)
        self._task_signals.finished.connect(self._deliver_task)
        self._task_signals.progress.connect(self._deliver_task_progress)
        self._next_task_id = 0
        self._task_callbacks = {}
        self._task_progress_callbacks = {}
        self._refreshing = False
        self._activation_refresh_scheduled = False
        self._initial_scan_completed = False

        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        central = QWidget()
        root = QVBoxLayout(central)
        root.setContentsMargins(24, 20, 24, 18)
        root.setSpacing(12)
        scroll.setWidget(central)
        self.setCentralWidget(scroll)
        self.setStyleSheet(APP_STYLESHEET)

        heading = QVBoxLayout()
        heading.setSpacing(2)
        title = QLabel("Monitor Control")
        title.setObjectName("pageTitle")
        subtitle = QLabel("Adjust your display settings with DDC/CI")
        subtitle.setObjectName("pageSubtitle")
        heading.addWidget(title)
        heading.addWidget(subtitle)
        root.addLayout(heading)

        display_group = QGroupBox("Display")
        display_layout = QVBoxLayout(display_group)
        display_layout.setSpacing(8)
        top = QHBoxLayout()
        self.monitor_combo = QComboBox()
        self.refresh_btn = QPushButton("Rescan")
        top.addWidget(self.monitor_combo, 1)
        top.addWidget(self.refresh_btn)
        self.monitor_combo.setToolTip("Select a physical monitor exposed by Windows")
        display_layout.addLayout(top)

        self.info = QLabel("Detecting monitors...")
        self.info.setWordWrap(True)
        self.info.setObjectName("detailsLabel")
        display_layout.addWidget(self.info)
        root.addWidget(display_group)

        windows_display_group = QGroupBox("Windows display settings")
        windows_display_layout = QFormLayout(windows_display_group)
        refresh_row = QHBoxLayout()
        self.refresh_rate_combo = QComboBox()
        self.refresh_rate_btn = QPushButton("Apply refresh rate")
        refresh_row.addWidget(self.refresh_rate_combo, 1)
        refresh_row.addWidget(self.refresh_rate_btn)
        windows_display_layout.addRow("Refresh rate", refresh_row)
        hdr_row = QHBoxLayout()
        self.hdr_state_label = QLabel("Detecting…")
        self.hdr_toggle_btn = QPushButton("Turn HDR on/off")
        hdr_row.addWidget(self.hdr_state_label, 1)
        hdr_row.addWidget(self.hdr_toggle_btn)
        windows_display_layout.addRow("HDR", hdr_row)
        windows_display_hint = QLabel(
            "These options use Windows display configuration APIs and are separate from monitor DDC/CI controls."
        )
        windows_display_hint.setWordWrap(True)
        windows_display_hint.setObjectName("detailsLabel")
        windows_display_layout.addRow(windows_display_hint)
        root.addWidget(windows_display_group)
        self._windows_display_ready = False
        self._windows_display_name = None

        group = QGroupBox("Monitor controls")
        form = QFormLayout(group)
        self.brightness = QSlider()
        self.brightness.setOrientation(Qt.Orientation.Horizontal)
        self.brightness.setRange(0, 100)
        self.brightness_label = QLabel("—")
        row1 = QHBoxLayout()
        row1.addWidget(self.brightness, 1)
        row1.addWidget(self.brightness_label)
        form.addRow("Brightness", row1)

        self.contrast = QSlider()
        self.contrast.setOrientation(Qt.Orientation.Horizontal)
        self.contrast.setRange(0, 100)
        self.contrast_label = QLabel("—")
        row2 = QHBoxLayout()
        row2.addWidget(self.contrast, 1)
        row2.addWidget(self.contrast_label)
        form.addRow("Contrast", row2)

        self.preset = QComboBox()
        form.addRow("Color preset", self.preset)

        self.input_source = QComboBox()
        form.addRow("Input source", self.input_source)

        rgb_row = QHBoxLayout()
        self.rgb = {}
        for code, label in ((0x16, "R"), (0x18, "G"), (0x1A, "B")):
            spin = QSpinBox()
            spin.setRange(0, 100)
            spin.setPrefix(label + " ")
            self.rgb[code] = spin
            rgb_row.addWidget(spin)
        form.addRow("RGB gain", rgb_row)

        root.addWidget(group)
        self.identified_panels_group = QGroupBox("Identified control panels")
        self.identified_panels_layout = QVBoxLayout(self.identified_panels_group)
        self.identified_panels_note = QLabel("These controls write the selected raw VCP value when Apply is pressed.")
        self.identified_panels_note.setWordWrap(True)
        self.identified_panels_note.setObjectName("detailsLabel")
        self.identified_panels_layout.addWidget(self.identified_panels_note)
        self.identified_panels_group.setVisible(False)
        root.addWidget(self.identified_panels_group)
        self.identified_control_panels = []
        self.identified_control_widgets = []
        self.identified_panel_state_widgets = []
        self._monitor_controls_ready = False
        self.apply_note = QLabel(
            "Available controls depend on your monitor. Changes apply to the display immediately."
        )
        self.apply_note.setWordWrap(True)
        self.apply_note.setObjectName("detailsLabel")
        root.addWidget(self.apply_note)

        identification_group = QGroupBox("Identify monitor settings")
        identification_layout = QVBoxLayout(identification_group)
        self.identify_btn = QPushButton("Start before/after capture…")
        self.identify_btn.setObjectName("primaryButton")
        identification_hint = QLabel(
            "Choose an OSD option and label each state as you capture it. Change one setting at a time; "
            "the exported JSON compares consecutive states and includes an analysis guide for ChatGPT."
        )
        identification_hint.setWordWrap(True)
        identification_hint.setObjectName("detailsLabel")
        identification_layout.addWidget(identification_hint)
        identification_actions = QHBoxLayout()
        self.analyze_json_btn = QPushButton("Import JSON for analysis…")
        identification_actions.addWidget(self.identify_btn)
        identification_actions.addWidget(self.analyze_json_btn)
        identification_layout.addLayout(identification_actions)
        root.addWidget(identification_group)

        self.startup_checkbox = QCheckBox("Run at Windows startup")
        self.startup_checkbox.setChecked(self._startup_enabled())
        self.startup_checkbox.toggled.connect(self.set_startup_enabled)
        self.startup_checkbox.setToolTip("Add or remove Monitor Control from your Windows user startup entries")

        profile_group = QGroupBox("Profiles")
        profile_layout = QHBoxLayout(profile_group)
        profile_row = QHBoxLayout()
        self.profile_combo = QComboBox()
        self.profile_save_btn = QPushButton("Save current…")
        self.profile_apply_btn = QPushButton("Apply")
        self.profile_delete_btn = QPushButton("Delete")
        profile_row.addWidget(self.profile_combo, 1)
        profile_row.addWidget(self.profile_save_btn)
        profile_row.addWidget(self.profile_apply_btn)
        profile_row.addWidget(self.profile_delete_btn)
        self.profile_combo.setToolTip("Choose a saved set of monitor settings")
        self.profile_apply_btn.setObjectName("primaryButton")
        profile_layout.addLayout(profile_row)
        root.addWidget(profile_group)

        automation_group = QGroupBox("App switching")
        automation_layout = QVBoxLayout(automation_group)
        automation_row = QHBoxLayout()
        self.automation_checkbox = QCheckBox("Switch profiles for foreground apps")
        self.automation_rules = QComboBox()
        self.automation_bind_btn = QPushButton("Bind last app")
        self.automation_add_btn = QPushButton("Add rule…")
        self.automation_remove_btn = QPushButton("Remove rule")
        self.automation_bind_btn.setToolTip("Bind the most recently active app to the selected profile")
        self.automation_add_btn.setToolTip("Choose an installed desktop app and add it to the rule list")
        automation_row.addWidget(self.automation_checkbox)
        automation_row.addWidget(self.automation_rules, 1)
        automation_row.addWidget(self.automation_bind_btn)
        automation_row.addWidget(self.automation_add_btn)
        automation_row.addWidget(self.automation_remove_btn)
        automation_layout.addLayout(automation_row)
        default_row = QHBoxLayout()
        default_row.addWidget(QLabel("When no app rule matches:"))
        self.default_profile_combo = QComboBox()
        self.default_profile_combo.addItem("Do nothing", None)
        self.default_profile_combo.setToolTip("Profile to restore after a bound app loses focus")
        default_row.addWidget(self.default_profile_combo, 1)
        automation_layout.addLayout(default_row)
        root.addWidget(automation_group)

        startup_row = QHBoxLayout()
        startup_row.addWidget(self.startup_checkbox)
        startup_row.addStretch(1)
        root.addLayout(startup_row)

        self.raw = QLabel()
        self.raw.setWordWrap(True)
        self.raw.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        self.raw.setObjectName("detailsLabel")
        self.raw.setVisible(False)
        self.details_checkbox = QCheckBox("Show raw monitor capabilities")
        self.details_checkbox.toggled.connect(self.raw.setVisible)
        root.addWidget(self.details_checkbox)
        root.addWidget(self.raw)

        self.status = QLabel("Ready")
        self.status.setObjectName("statusLabel")
        root.addWidget(self.status)
        root.addStretch(1)

        self.refresh_btn.clicked.connect(self.refresh)
        self.identify_btn.clicked.connect(self.start_identification)
        self.analyze_json_btn.clicked.connect(self.analyze_identification_file)
        self.monitor_combo.currentIndexChanged.connect(self.select_monitor)
        self.refresh_rate_btn.clicked.connect(self.apply_refresh_rate)
        self.hdr_toggle_btn.clicked.connect(self.toggle_hdr)
        self.brightness.valueChanged.connect(lambda v: self.brightness_label.setText(str(v)))
        self.contrast.valueChanged.connect(lambda v: self.contrast_label.setText(str(v)))
        self.brightness.sliderReleased.connect(lambda: self.write_current(0x10, self.brightness.value()))
        self.contrast.sliderReleased.connect(lambda: self.write_current(0x12, self.contrast.value()))
        self.preset.activated.connect(self.write_preset)
        self.input_source.activated.connect(self.write_input)
        self.profile_save_btn.clicked.connect(self.save_current_profile)
        self.profile_apply_btn.clicked.connect(self.apply_selected_profile)
        self.profile_delete_btn.clicked.connect(self.delete_selected_profile)
        self.automation_checkbox.toggled.connect(self.set_automation_enabled)
        self.automation_bind_btn.clicked.connect(self.bind_foreground_app)
        self.automation_add_btn.clicked.connect(self.add_process_rule)
        self.automation_remove_btn.clicked.connect(self.remove_automation_rule)
        self.default_profile_combo.currentIndexChanged.connect(self.set_default_profile)
        for code, spin in self.rgb.items():
            spin.editingFinished.connect(lambda c=code, s=spin: self.write_current(c, s.value()))

        self._setup_tray()
        self._restore_window_state()
        try:
            self.identified_control_panels = load_identified_controls()
        except Exception as exc:
            QMessageBox.warning(self, "Could not load identified controls", str(exc))
        self.refresh_identified_control_panels()
        self.refresh()
        self.reload_profiles()
        self.load_automation_settings()
        self.automation_timer = QTimer(self)
        self.automation_timer.setInterval(1200)
        self.automation_timer.timeout.connect(self.check_foreground_application)
        self.automation_timer.start()

    def _submit_task(self, function, callback, progress_callback=None):
        self._next_task_id += 1
        task_id = self._next_task_id
        self._task_callbacks[task_id] = callback
        if progress_callback:
            self._task_progress_callbacks[task_id] = progress_callback
        self._task_pool.start(FunctionTask(task_id, function, self._task_signals, bool(progress_callback)))

    def event(self, event):
        activated = event.type() == QEvent.Type.WindowActivate
        result = super().event(event)
        if activated and hasattr(self, "monitor_combo"):
            self._schedule_activation_refresh()
        return result

    def _schedule_activation_refresh(self):
        if self._activation_refresh_scheduled:
            return
        self._activation_refresh_scheduled = True
        QTimer.singleShot(350, self._refresh_after_activation)

    def _refresh_after_activation(self):
        self._activation_refresh_scheduled = False
        if (not self.isActiveWindow() or self._identification_active
                or not self._initial_scan_completed):
            return
        if self._refreshing or self._task_pool.activeThreadCount() or self._task_callbacks:
            self._activation_refresh_scheduled = True
            QTimer.singleShot(500, self._refresh_after_activation)
            return
        # Reacquire monitor handles as Windows mode changes can invalidate old DXVA2 handles.
        self.refresh()

    @Slot(int, object)
    def _deliver_task_progress(self, task_id, progress):
        callback = self._task_progress_callbacks.get(task_id)
        if callback:
            callback(progress)

    @Slot(int, object, object)
    def _deliver_task(self, task_id, result, error):
        callback = self._task_callbacks.pop(task_id, None)
        self._task_progress_callbacks.pop(task_id, None)
        if error is not None:
            logger.exception("background_task_failed", exc_info=(type(error), error, error.__traceback__))
        if callback:
            callback(result, error)

    def _setup_tray(self):
        self.tray = QSystemTrayIcon(self)
        self.tray.setToolTip("Monitor Control")
        self.tray.setIcon(self.style().standardIcon(self.style().StandardPixmap.SP_ComputerIcon))
        menu = QMenu()
        show_action = QAction("Open Monitor Control", self)
        show_action.triggered.connect(self.show_window)
        menu.addAction(show_action)
        quit_action = QAction("Exit", self)
        quit_action.triggered.connect(self.exit_application)
        menu.addAction(quit_action)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(self._tray_activated)
        self.tray.show()

    def _restore_window_state(self):
        geometry = self.settings.value("window/geometry")
        if geometry:
            self.restoreGeometry(geometry)
        state = self.settings.value("window/state")
        if state:
            self.restoreState(state)

    def _save_window_state(self):
        self.settings.setValue("window/geometry", self.saveGeometry())
        self.settings.setValue("window/state", self.saveState())
        self.settings.setValue("window/maximized", self.isMaximized())

    def _startup_enabled(self):
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", 0, winreg.KEY_READ) as key:
                winreg.QueryValueEx(key, "MonitorControl")
            return True
        except (OSError, ImportError):
            return False

    def set_startup_enabled(self, enabled):
        try:
            import winreg
            key_path = r"Software\Microsoft\Windows\CurrentVersion\Run"
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_SET_VALUE) as key:
                if enabled:
                    executable = sys.executable
                    script = os.path.abspath(sys.argv[0])
                    if getattr(sys, "frozen", False):
                        command = f'"{executable}"'
                    else:
                        pythonw = os.path.join(os.path.dirname(executable), "pythonw.exe")
                        launcher = pythonw if os.path.isfile(pythonw) else executable
                        command = f'"{launcher}" "{script}"'
                    winreg.SetValueEx(key, "MonitorControl", 0, winreg.REG_SZ, command)
                else:
                    try:
                        winreg.DeleteValue(key, "MonitorControl")
                    except FileNotFoundError:
                        pass
            self.status.setText("Windows startup setting updated.")
        except Exception as exc:
            self.startup_checkbox.blockSignals(True)
            self.startup_checkbox.setChecked(not enabled)
            self.startup_checkbox.blockSignals(False)
            QMessageBox.warning(self, "Startup setting failed", str(exc))

    def show_window(self):
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def _tray_activated(self, reason):
        if reason in (QSystemTrayIcon.ActivationReason.Trigger, QSystemTrayIcon.ActivationReason.DoubleClick):
            self.show_window()

    def exit_application(self):
        self._allow_close = True
        self.tray.hide()
        QApplication.quit()

    def shutdown(self):
        self.automation_timer.stop()
        self._task_pool.waitForDone()
        close_records(self.records)
        logger.info("application_stopped")

    def closeEvent(self, event):
        self._save_window_state()
        if not self._allow_close and self.tray.isVisible():
            event.ignore()
            self.hide()
            self.tray.showMessage("Monitor Control", "Still running in the system tray.", QSystemTrayIcon.MessageIcon.Information, 2500)
            return
        super().closeEvent(event)

    def refresh(self):
        if self._refreshing:
            return
        self._refreshing = True
        self.refresh_btn.setEnabled(False)
        self.set_controls_enabled(False)
        self.status.setText("Scanning monitors…")
        old_records = self.records
        previous_monitor = self.current
        self.current = None
        self.monitor_combo.blockSignals(True)
        self.monitor_combo.clear()
        self.monitor_combo.blockSignals(False)

        def scan():
            close_records(old_records)
            return enumerate_monitors()

        def scanned(records, error):
            self._refreshing = False
            self._initial_scan_completed = True
            self.refresh_btn.setEnabled(True)
            self.records = records or []
            if error:
                logger.exception("monitor_enumeration_failed", exc_info=(type(error), error, error.__traceback__))
                QMessageBox.critical(self, "Monitor enumeration failed", str(error))
            for i, rec in enumerate(self.records):
                self.monitor_combo.addItem(f"{rec.description} — {rec.display}", i)
            if self.records:
                selected_index = 0
                if previous_monitor:
                    previous_match = next((
                        i for i, rec in enumerate(self.records)
                        if rec.description == previous_monitor.description
                        and rec.display == previous_monitor.display
                    ), None)
                    if previous_match is not None:
                        selected_index = previous_match
                dialog = getattr(self, "_identification_dialog", None)
                if self._identification_recovery_active and dialog:
                    target = dialog.record
                    exact = [i for i, rec in enumerate(self.records)
                             if rec.display == target.display and rec.description == target.description]
                    same_description = [i for i, rec in enumerate(self.records)
                                        if rec.description == target.description]
                    same_display = [i for i, rec in enumerate(self.records)
                                    if rec.display == target.display]
                    if exact:
                        selected_index = exact[0]
                    elif len(same_description) == 1:
                        selected_index = same_description[0]
                    elif len(same_display) == 1:
                        selected_index = same_display[0]
                    elif len(self.records) == 1:
                        selected_index = 0
                self.monitor_combo.setCurrentIndex(selected_index)
                self.select_monitor(selected_index)
                if self._identification_active and dialog and self.current:
                    dialog.record = self.current
            else:
                self.current = None
                self.refresh_identified_control_panels()
                self.info.setText("No physical monitor handles were found. Check Windows display settings and DDC/CI.")
                self.set_controls_enabled(False)
                self.status.setText("No monitors found.")

        self._submit_task(scan, scanned)

    def schedule_monitor_recovery(self):
        if self._identification_recovery_active:
            return
        self._identification_recovery_active = True
        attempts = {"count": 0}

        def retry_scan():
            if self.current or attempts["count"] >= 5:
                self._identification_recovery_active = False
                return
            if self._refreshing:
                QTimer.singleShot(1200, retry_scan)
                return
            attempts["count"] += 1
            self.refresh()
            QTimer.singleShot(1800, retry_scan)

        QTimer.singleShot(800, retry_scan)

    def set_controls_enabled(self, enabled):
        self._monitor_controls_ready = bool(enabled and self.current is not None)
        for w in (self.brightness, self.contrast, self.preset, self.input_source, *self.rgb.values()):
            w.setEnabled(enabled)
        self.profile_save_btn.setEnabled(enabled)
        self.profile_apply_btn.setEnabled(enabled and self.profile_combo.count() > 0)
        self.identify_btn.setEnabled(enabled and self.current is not None)
        windows_ready = bool(enabled and self.current is not None and self._windows_display_ready)
        self.refresh_rate_combo.setEnabled(windows_ready and self.refresh_rate_combo.count() > 0)
        self.refresh_rate_btn.setEnabled(windows_ready and self.refresh_rate_combo.count() > 0)
        self.hdr_toggle_btn.setEnabled(windows_ready and self.hdr_state_label.property("supported") is True)
        for control_widget in self.identified_control_widgets:
            control_widget.setEnabled(self._monitor_controls_ready)

    def start_identification(self):
        if not self.current:
            return
        dialog = MonitorIdentificationDialog(self, self.current)
        self._identification_dialog = dialog
        self._identification_active = True
        self.automation_timer.stop()
        try:
            dialog.exec()
        finally:
            self._identification_active = False
            self._identification_dialog = None
            self.automation_timer.start()

    def analyze_identification_file(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "Open monitor identification report", "", "JSON files (*.json)"
        )
        if not path:
            return
        try:
            with open(path, "r", encoding="utf-8") as file:
                report = json.load(file)
            if not isinstance(report, dict) or not isinstance(report.get("osd_controls"), list):
                raise ValueError("This file is not a current monitor identification report (missing osd_controls).")
            if not report["osd_controls"]:
                raise ValueError("The report contains no captured OSD states to analyze.")
            analyzed = analyze_osd_controls(report["osd_controls"])
            summary = format_analysis_summary(report, analyzed)
        except Exception as exc:
            QMessageBox.warning(self, "Could not analyze report", str(exc))
            return

        dialog = QDialog(self)
        dialog.setWindowTitle(f"Identification analysis — {os.path.basename(path)}")
        dialog.resize(920, 760)
        layout = QVBoxLayout(dialog)
        layout.addWidget(QLabel("Analysis summary"))
        result = QPlainTextEdit()
        result.setReadOnly(True)
        result.setPlainText(summary)
        result.setMaximumHeight(280)
        layout.addWidget(result)
        test_note = QLabel(
            "Test values writes each raw value, waits 4 seconds, and checks the VCP readback, then restores the starting value. "
            "The display may change temporarily. Test a mapping before selecting it to add."
        )
        test_note.setWordWrap(True)
        test_note.setObjectName("detailsLabel")
        layout.addWidget(test_note)
        mapping_list = QListWidget()
        mapping_checks = []
        for control in analyzed:
            option = str(control.get("osd_option", "")).strip() or "Unnamed OSD option"
            for correlation in control.get("vcp_correlations", []):
                try:
                    code = int(str(correlation.get("code", "")), 16)
                    states = []
                    for value_info in correlation.get("value_to_osd_states", []):
                        raw_value = int(value_info["current"])
                        if code < 0 or code > 0xFF or raw_value < 0 or raw_value > 0xFFFFFFFF:
                            continue
                        labels = [str(label) for label in value_info.get("osd_states", []) if str(label).strip()]
                        label = " / ".join(labels) if labels else f"Value {raw_value}"
                        states.append({"label": label, "value": raw_value})
                    if code < 0 or code > 0xFF or len(states) < 2:
                        continue
                    name = str(correlation.get("name", "VCP control"))
                    panel = {
                        "monitor": report.get("monitor", {}),
                        "osd_option": option,
                        "vcp_code": code,
                        "vcp_name": name,
                        "states": states,
                    }
                    values_summary = "; ".join(f"{state['value']} → {state['label']}" for state in states)
                    item = QListWidgetItem()
                    item.setData(Qt.ItemDataRole.UserRole, panel)
                    mapping_list.addItem(item)
                    row_widget = QWidget()
                    row_layout = QVBoxLayout(row_widget)
                    row_layout.setContentsMargins(6, 5, 6, 5)
                    mapping_action_row = QHBoxLayout()
                    check = QCheckBox(f"{option} — {name} (0x{code:02X}): {values_summary}")
                    check.setToolTip("Add this OSD/VCP mapping to the main control panel")
                    test_button = QPushButton("Test")
                    test_result = QLabel("")
                    test_result.setWordWrap(True)
                    test_result.setObjectName("detailsLabel")
                    mapping_action_row.addWidget(check, 1)
                    mapping_action_row.addWidget(test_button)
                    row_layout.addLayout(mapping_action_row)
                    row_layout.addWidget(test_result)
                    mapping_list.setItemWidget(item, row_widget)
                    item.setSizeHint(row_widget.sizeHint())
                    mapping_checks.append((item, check))
                    test_button.clicked.connect(
                        lambda checked=False, spec=panel, label=test_result, widgets=(check, test_button), row_item=item:
                        self.test_identified_control(spec, label, widgets, row_item)
                    )
                except (KeyError, TypeError, ValueError):
                    continue
        layout.addWidget(mapping_list, 1)
        action_row = QHBoxLayout()
        add_button = QPushButton("Add selected control panels")
        add_button.setObjectName("primaryButton")
        selection_status = QLabel("")
        selection_status.setObjectName("detailsLabel")
        close_button = QPushButton("Close")
        action_row.addWidget(add_button)
        action_row.addWidget(selection_status, 1)
        action_row.addWidget(close_button)
        layout.addLayout(action_row)

        def add_selected_panels():
            selected = [
                item.data(Qt.ItemDataRole.UserRole)
                for item, check in mapping_checks
                if check.isChecked()
            ]
            if not selected:
                selection_status.setText("Select at least one mapping.")
                return
            added, duplicates = self.add_identified_control_panels(selected)
            selection_status.setText(f"Added {added}; {duplicates} already existed.")

        add_button.clicked.connect(add_selected_panels)
        close_button.clicked.connect(dialog.accept)
        dialog.exec()

    @staticmethod
    def _identified_panel_key(panel):
        monitor = panel.get("monitor", {})
        return (
            str(monitor.get("description", "")).casefold(),
            str(monitor.get("display", "")).casefold(),
            str(panel.get("osd_option", "")).casefold(),
            int(panel.get("vcp_code", -1)),
        )

    def add_identified_control_panels(self, panels):
        existing = {self._identified_panel_key(panel) for panel in self.identified_control_panels}
        additions = []
        duplicates = 0
        for panel in panels:
            code = int(panel["vcp_code"])
            states = panel.get("states", [])
            if code < 0 or code > 0xFF or len(states) < 2:
                continue
            normalized = dict(panel)
            normalized["vcp_code"] = code
            normalized["states"] = [
                {"label": str(state["label"]), "value": int(state["value"])}
                for state in states
                if 0 <= int(state["value"]) <= 0xFFFFFFFF
            ]
            if len(normalized["states"]) < 2:
                continue
            key = self._identified_panel_key(normalized)
            if key in existing:
                duplicates += 1
                continue
            existing.add(key)
            additions.append(normalized)
        updated = self.identified_control_panels + additions
        try:
            save_identified_controls(updated)
        except Exception as exc:
            QMessageBox.warning(self, "Could not save control panels", str(exc))
            return 0, duplicates
        self.identified_control_panels = updated
        self.refresh_identified_control_panels()
        return len(additions), duplicates

    def _panel_matches_current_monitor(self, panel):
        if not self.current:
            return False
        monitor = panel.get("monitor", {})
        return (
            str(monitor.get("description", "")).casefold() == self.current.description.casefold()
            and str(monitor.get("display", "")).casefold() == self.current.display.casefold()
        )

    def refresh_identified_control_panels(self):
        while self.identified_panels_layout.count() > 1:
            item = self.identified_panels_layout.takeAt(1)
            widget = item.widget()
            if widget:
                widget.deleteLater()
        self.identified_control_widgets = []
        self.identified_panel_state_widgets = []
        matching = [panel for panel in self.identified_control_panels if self._panel_matches_current_monitor(panel)]
        for panel in matching:
            code = int(panel["vcp_code"])
            name = str(panel.get("vcp_name", "VCP control"))
            option = str(panel.get("osd_option", "OSD control"))
            panel_box = QGroupBox(f"{option} — {name} (0x{code:02X})")
            row = QHBoxLayout(panel_box)
            state_combo = QComboBox()
            for state in panel.get("states", []):
                state_combo.addItem(f"{state['label']} (VCP value {state['value']})", int(state["value"]))
            apply_button = QPushButton("Apply")
            apply_button.setObjectName("primaryButton")
            apply_button.setToolTip("Write this raw VCP value to the selected monitor immediately")
            delete_button = QPushButton("Delete panel")
            row.addWidget(state_combo, 1)
            row.addWidget(apply_button)
            row.addWidget(delete_button)
            self.identified_panels_layout.addWidget(panel_box)
            apply_button.clicked.connect(
                lambda checked=False, spec=panel, combo=state_combo, button=apply_button:
                self.apply_identified_control(spec, combo, button)
            )
            delete_button.clicked.connect(lambda checked=False, spec=panel: self.remove_identified_control_panel(spec))
            self.identified_control_widgets.extend((state_combo, apply_button, delete_button))
            self.identified_panel_state_widgets.append((panel, state_combo))
            for widget in (state_combo, apply_button, delete_button):
                widget.setEnabled(self._monitor_controls_ready)
        self.identified_panels_group.setVisible(bool(matching))

    def load_identified_panel_values(self, record):
        panel_widgets = [
            (panel, combo) for panel, combo in self.identified_panel_state_widgets
            if self._panel_matches_current_monitor(panel)
        ]
        codes = sorted({int(panel["vcp_code"]) for panel, _combo in panel_widgets})
        if not codes:
            return

        def read_values():
            values = {}
            for code in codes:
                try:
                    values[code] = read_vcp(record.handle, code)["current"]
                except (OSError, ValueError):
                    values[code] = None
            return values

        def loaded(values, error):
            if error or record is not self.current:
                if error:
                    logger.warning("identified_panel_refresh_failed monitor=%s error=%s", record.description, error)
                return
            marker_role = int(Qt.ItemDataRole.UserRole) + 1
            for panel, combo in panel_widgets:
                code = int(panel["vcp_code"])
                current_value = values.get(code)
                if current_value is None:
                    combo.setToolTip(f"Could not read the current value of VCP 0x{code:02X}.")
                    continue
                for index in reversed(range(combo.count())):
                    if combo.itemData(index, marker_role) == "observed-current":
                        combo.removeItem(index)
                index = combo.findData(current_value)
                if index >= 0:
                    combo.setCurrentIndex(index)
                    combo.setToolTip(f"Current VCP value: {current_value}.")
                else:
                    combo.insertItem(0, f"Current: {current_value} (not mapped)", current_value)
                    combo.setItemData(0, "observed-current", marker_role)
                    combo.setCurrentIndex(0)
                    combo.setToolTip(f"Current VCP value {current_value} is not one of the identified OSD states.")

        self._submit_task(read_values, loaded)

    def apply_identified_control(self, panel, state_combo, apply_button):
        if not self.current or not self._panel_matches_current_monitor(panel):
            QMessageBox.warning(self, "Monitor mismatch", "Select the monitor used for this mapping before applying it.")
            return
        value = state_combo.currentData()
        if value is None:
            return
        record = self.current
        code = int(panel["vcp_code"])
        apply_button.setEnabled(False)

        def applied(result, error):
            apply_button.setEnabled(self._monitor_controls_ready)
            if error:
                logger.exception("identified_control_write_failed", exc_info=(type(error), error, error.__traceback__))
                QMessageBox.warning(self, "Could not apply setting", str(error))
                self.refresh()
                return
            state = state_combo.currentText()
            self.status.setText(f"Applied {panel.get('osd_option')} using VCP 0x{code:02X}: {state}")
            if record is self.current:
                self.load_controls()

        self._submit_task(lambda: write_vcp(record.handle, code, int(value)), applied)

    def test_identified_control(self, panel, result_label, panel_widgets, result_item=None):
        def show_result(text, style=None):
            result_label.setText(text)
            if style is not None:
                result_label.setStyleSheet(style)
            row_widget = result_label.parentWidget()
            if row_widget:
                row_widget.adjustSize()
                if result_item is not None:
                    result_item.setSizeHint(row_widget.sizeHint())

        if not self.current or not self._panel_matches_current_monitor(panel):
            QMessageBox.warning(self, "Monitor mismatch", "Select the monitor used for this mapping before testing it.")
            return
        record = self.current
        code = int(panel["vcp_code"])
        values = list(dict.fromkeys(int(state["value"]) for state in panel.get("states", [])))
        if not values:
            show_result("No values are available to test.")
            return
        for widget in panel_widgets:
            widget.setEnabled(False)
        dialog = result_label.window()
        close_buttons = [
            button for button in dialog.findChildren(QPushButton)
            if button.text().casefold() == "close"
        ] if dialog else []
        for button in close_buttons:
            button.setEnabled(False)
        progress_lines = []

        def on_progress(message):
            progress_lines.append(str(message))
            show_result("\n".join(progress_lines[-10:]), "color: #b54708;")

        on_progress("Test running. The display may change temporarily…")

        def run_test(progress):
            progress(f"Reading initial VCP 0x{code:02X} value…")
            initial = read_vcp_current(record, code, progress)
            progress(f"Initial VCP 0x{code:02X} value is {initial}.")
            results = []
            owned_record = None
            logger.info(
                "vcp_test_started monitor=%s display=%s code=0x%02X initial=%d values=%s",
                record.description, record.display, code, initial, values,
            )

            def reacquire_test_record():
                nonlocal owned_record
                candidates = enumerate_monitors()
                exact = [candidate for candidate in candidates
                         if candidate.description == record.description and candidate.display == record.display]
                same_description = [candidate for candidate in candidates
                                    if candidate.description == record.description]
                same_display = [candidate for candidate in candidates if candidate.display == record.display]
                if exact:
                    selected = exact[0]
                elif len(same_description) == 1:
                    selected = same_description[0]
                elif len(same_display) == 1:
                    selected = same_display[0]
                elif len(candidates) == 1:
                    selected = candidates[0]
                else:
                    close_records(candidates)
                    raise RuntimeError("Could not reacquire the tested monitor after the display changed.")
                for candidate in candidates:
                    if candidate is not selected:
                        close_records([candidate])
                if owned_record is not None:
                    close_records([owned_record])
                owned_record = selected
                logger.info(
                    "vcp_test_monitor_reacquired monitor=%s display=%s candidate_count=%d",
                    selected.description, selected.display, len(candidates),
                )
                return selected

            try:
                for value in values:
                    results.append(run_vcp_value_test(
                        code=code,
                        initial_value=initial,
                        target_value=value,
                        reacquire=reacquire_test_record,
                        write_value=lambda active, vcp_code, target: write_vcp(active.handle, vcp_code, target),
                        read_value=lambda active, vcp_code: read_vcp_current(active, vcp_code, progress),
                        wait=time.sleep,
                        logger=logger,
                        progress=progress,
                        restore_attempts=5,
                        settle_seconds=VCP_TEST_SETTLE_SECONDS,
                    ))
                return {"initial": initial, "results": results}
            finally:
                if owned_record is not None:
                    close_records([owned_record])

        def tested(outcome, error):
            for widget in panel_widgets:
                widget.setEnabled(self._monitor_controls_ready)
            for button in close_buttons:
                button.setEnabled(True)
            if error:
                logger.exception("identified_control_test_failed", exc_info=(type(error), error, error.__traceback__))
                error_text = f"Test could not start: {error}. The starting value could not be confirmed."
                error_style = "color: #b42318;"
                show_result(error_text, error_style)
                QMessageBox.warning(self, "Control test failed", str(error))
                self.refresh()
                return
            all_passed = bool(outcome["results"]) and all(item["passed"] for item in outcome["results"])
            lines = [
                f"OVERALL: {'PASS' if all_passed else 'FAIL'} — starting value {outcome['initial']}."
            ]
            for result in outcome["results"]:
                target_value = result["target_value"]
                tested_value = result["test_readback"]
                restored_value = result["restore_readback"]
                test_text = (
                    f"TEST {'PASS' if result['test_passed'] else 'FAIL'} "
                    f"(read {tested_value}, expected {target_value})"
                    if tested_value is not None
                    else f"TEST FAIL (readback error: {result['test_error']})"
                )
                restore_text = (
                    f"RESTORE {'PASS' if result['restore_passed'] else 'FAIL'} "
                    f"(read {restored_value}, expected {outcome['initial']})"
                    if restored_value is not None
                    else f"RESTORE FAIL (readback error: {result['restore_error']})"
                )
                lines.append(
                    f"Value {target_value}: {test_text}; {restore_text}; "
                    f"RESULT {'PASS' if result['passed'] else 'FAIL'}"
                )
            result_style = "color: #067647;" if all_passed else "color: #b42318;"
            result_text = "\n".join(lines)
            show_result(result_text, result_style)
            self.status.setText(
                f"VCP 0x{code:02X} test {'passed' if all_passed else 'failed'}; "
                "each candidate was checked and restored to the initial value."
            )
            logger.info(
                "vcp_test_finished monitor=%s display=%s code=0x%02X initial=%d passed=%s results=%s",
                record.description, record.display, code, outcome["initial"], all_passed, outcome["results"],
            )
            if record is self.current:
                self.refresh()

        self._submit_task(run_test, tested, progress_callback=on_progress)

    def remove_identified_control_panel(self, panel):
        answer = QMessageBox.question(
            self, "Delete control panel", f"Delete the {panel.get('osd_option')} control panel?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if answer != QMessageBox.StandardButton.Yes:
            return
        key = self._identified_panel_key(panel)
        updated = [item for item in self.identified_control_panels if self._identified_panel_key(item) != key]
        try:
            save_identified_controls(updated)
        except Exception as exc:
            QMessageBox.warning(self, "Could not delete control panel", str(exc))
            return
        self.identified_control_panels = updated
        self.refresh_identified_control_panels()
        self.status.setText(f"Deleted {panel.get('osd_option')} control panel.")

    def reload_profiles(self):
        try:
            profiles = load_profiles()
        except Exception as exc:
            QMessageBox.warning(self, "Could not load profiles", str(exc))
            profiles = {}
        self.profile_combo.clear()
        for name in sorted(profiles, key=str.casefold):
            self.profile_combo.addItem(name, name)
        self.available_profiles = profiles
        previous_default = self.automation_settings.get("default_profile") if hasattr(self, "automation_settings") else None
        selected_default = self.refresh_default_profile_options(previous_default)
        if hasattr(self, "automation_settings"):
            self.automation_settings["default_profile"] = selected_default
            old_rules = self.automation_settings.get("rules", {})
            valid_profiles = set(profiles)
            cleaned_rules = {
                process_name: profile_name
                for process_name, profile_name in old_rules.items()
                if isinstance(profile_name, str) and profile_name in valid_profiles
            }
            self.automation_settings["rules"] = cleaned_rules
            if selected_default != previous_default or cleaned_rules != old_rules:
                try:
                    save_automation(self.automation_settings)
                except Exception:
                    logger.exception("deleted_profile_reference_cleanup_failed")
            if cleaned_rules != old_rules:
                self._last_auto_profile = None
            if hasattr(self, "automation_rules"):
                self.refresh_automation_rules()
        self.profile_apply_btn.setEnabled(bool(self.current and profiles))
        self.profile_delete_btn.setEnabled(bool(profiles))

    def refresh_default_profile_options(self, selected_profile):
        self.default_profile_combo.blockSignals(True)
        self.default_profile_combo.clear()
        self.default_profile_combo.addItem("Do nothing", None)
        for name in sorted(getattr(self, "available_profiles", {}), key=str.casefold):
            self.default_profile_combo.addItem(name, name)
        index = self.default_profile_combo.findData(selected_profile)
        if index < 0:
            index = 0
        self.default_profile_combo.setCurrentIndex(index)
        self.default_profile_combo.blockSignals(False)
        return self.default_profile_combo.currentData()

    def load_automation_settings(self):
        try:
            self.automation_settings = load_automation()
        except Exception as exc:
            QMessageBox.warning(self, "Could not load app switching rules", str(exc))
            self.automation_settings = {"enabled": False, "rules": {}, "default_profile": None}
        self._last_auto_profile = None
        default_profile = self.automation_settings.get("default_profile")
        selected_default = self.refresh_default_profile_options(default_profile)
        if selected_default != default_profile:
            self.automation_settings["default_profile"] = selected_default
            try:
                save_automation(self.automation_settings)
            except OSError:
                logger.exception("invalid_default_profile_cleanup_failed")
        valid_profiles = set(getattr(self, "available_profiles", {}))
        old_rules = self.automation_settings.get("rules", {})
        self.automation_settings["rules"] = {
            process_name: profile_name
            for process_name, profile_name in old_rules.items()
            if isinstance(profile_name, str) and profile_name in valid_profiles
        }
        if self.automation_settings["rules"] != old_rules:
            self._last_auto_profile = None
            try:
                save_automation(self.automation_settings)
            except OSError:
                logger.exception("invalid_app_profile_rules_cleanup_failed")
        self.automation_checkbox.blockSignals(True)
        self.automation_checkbox.setChecked(self.automation_settings["enabled"])
        self.automation_checkbox.blockSignals(False)
        self.refresh_automation_rules()

    def set_default_profile(self, _index):
        if not hasattr(self, "automation_settings"):
            return
        self.automation_settings["default_profile"] = self.default_profile_combo.currentData()
        try:
            save_automation(self.automation_settings)
            self.status.setText("Default profile updated.")
        except Exception as exc:
            logger.exception("default_profile_save_failed")
            QMessageBox.warning(self, "Could not save default profile", str(exc))

    def refresh_automation_rules(self):
        self.automation_rules.clear()
        valid_profiles = set(getattr(self, "available_profiles", {}))
        for process_name, profile_name in sorted(self.automation_settings["rules"].items()):
            if isinstance(profile_name, str) and profile_name in valid_profiles:
                self.automation_rules.addItem(f"{process_name} → {profile_name}", process_name)
        self.automation_remove_btn.setEnabled(self.automation_rules.count() > 0)

    def set_automation_enabled(self, enabled):
        self.automation_settings["enabled"] = bool(enabled)
        try:
            save_automation(self.automation_settings)
            self._last_auto_profile = None
            self.status.setText("App profile switching enabled." if enabled else "App profile switching disabled.")
        except Exception as exc:
            self.automation_checkbox.blockSignals(True)
            self.automation_checkbox.setChecked(not enabled)
            self.automation_checkbox.blockSignals(False)
            QMessageBox.warning(self, "Could not save app switching setting", str(exc))

    def bind_foreground_app(self):
        process_name = self._last_external_process
        profile_name = self.profile_combo.currentData()
        if not process_name:
            QMessageBox.warning(self, "Could not identify app", "Windows could not identify the foreground process.")
            return
        if not profile_name:
            QMessageBox.information(self, "Choose a profile", "Select a profile to bind to this app first.")
            return
        self._save_process_rule(process_name, profile_name)

    def add_process_rule(self):
        profile_name = self.profile_combo.currentData()
        if not profile_name:
            QMessageBox.information(self, "Choose a profile", "Select the profile this app should use first.")
            return
        try:
            applications = installed_applications()
        except Exception as exc:
            logger.exception("installed_app_list_failed")
            QMessageBox.warning(self, "Could not load installed apps", str(exc))
            return
        if not applications:
            QMessageBox.information(
                self, "No selectable apps found",
                "No installed desktop apps with a registered executable were found. "
                "You can run an app and use “Bind last app” instead."
            )
            return
        dialog = QDialog(self)
        dialog.setWindowTitle("Add app rule")
        dialog.setMinimumSize(480, 520)
        layout = QVBoxLayout(dialog)
        layout.addWidget(QLabel(f"Choose an installed app to use profile “{profile_name}”."))
        search = QLineEdit()
        search.setPlaceholderText("Search installed apps")
        layout.addWidget(search)
        app_list = QListWidget()
        for display_name, process_name, executable in applications:
            item = QListWidgetItem(f"{display_name}  ·  {process_name}")
            item.setData(Qt.ItemDataRole.UserRole, process_name)
            item.setToolTip(executable)
            app_list.addItem(item)
        layout.addWidget(app_list, 1)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        ok_button = buttons.button(QDialogButtonBox.StandardButton.Ok)
        ok_button.setEnabled(False)
        buttons.accepted.connect(dialog.accept)
        buttons.rejected.connect(dialog.reject)
        app_list.currentRowChanged.connect(lambda row: ok_button.setEnabled(row >= 0))
        app_list.itemDoubleClicked.connect(lambda _item: dialog.accept())

        def filter_apps(text):
            query = text.casefold().strip()
            for row in range(app_list.count()):
                item = app_list.item(row)
                item.setHidden(query not in item.text().casefold())
            current_item = app_list.currentItem()
            if current_item and current_item.isHidden():
                app_list.setCurrentRow(-1)

        search.textChanged.connect(filter_apps)
        layout.addWidget(buttons)
        if dialog.exec() != QDialog.DialogCode.Accepted or not app_list.currentItem():
            return
        process_name = app_list.currentItem().data(Qt.ItemDataRole.UserRole)
        self._save_process_rule(process_name, profile_name)

    def _save_process_rule(self, process_name, profile_name):
        previous_profile = self.automation_settings["rules"].get(process_name)
        if previous_profile and previous_profile != profile_name:
            answer = QMessageBox.question(
                self, "Replace app rule?",
                f"{process_name} already uses profile {previous_profile}. Replace it with {profile_name}?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            )
            if answer != QMessageBox.Yes:
                return
        self.automation_settings["rules"][process_name] = profile_name
        try:
            save_automation(self.automation_settings)
            self.refresh_automation_rules()
            self.automation_rules.setCurrentIndex(self.automation_rules.findData(process_name))
            self.status.setText(f"Saved rule: {process_name} → {profile_name}.")
        except Exception as exc:
            QMessageBox.warning(self, "Could not save app rule", str(exc))

    def remove_automation_rule(self):
        process_name = self.automation_rules.currentData()
        if not process_name:
            return
        self.automation_settings["rules"].pop(process_name, None)
        try:
            save_automation(self.automation_settings)
            self.refresh_automation_rules()
            self._last_auto_profile = None
        except Exception as exc:
            QMessageBox.warning(self, "Could not remove app rule", str(exc))

    def check_foreground_application(self):
        process_name, process_id = foreground_process_info()
        if process_id == os.getpid():
            process_name = None
        elif process_name:
            self._last_external_process = process_name
        if not self.automation_settings.get("enabled") or not self.current or self._refreshing:
            return
        app_profile = self.automation_settings["rules"].get(process_name)
        if app_profile:
            if app_profile == self._last_auto_profile:
                return
            profile_name = app_profile
            self._last_auto_profile = app_profile
        else:
            previous_app_profile = self._last_auto_profile
            if previous_app_profile is None:
                return
            self._last_auto_profile = None
            profile_name = self.automation_settings.get("default_profile")
            if not profile_name or profile_name == previous_app_profile:
                return
        try:
            profile = load_profiles().get(profile_name)
            if not isinstance(profile, dict) or not isinstance(profile.get("values"), dict):
                self.status.setText(f'No valid profile "{profile_name}" for the foreground app.')
                return
            if profile.get("monitor") != self.current.description:
                self.status.setText(f'Profile "{profile_name}" is for a different monitor; skipped.')
                return
            if app_profile:
                logger.info(f"app_profile_activated:{process_name}:{profile_name}")
            else:
                logger.info(f"default_profile_restored:{profile_name}")
            self.apply_profile_values(
                profile_name, profile["values"],
                windows_settings=profile.get("windows_settings", {}), automatic=True,
            )
        except Exception as exc:
            self.status.setText(f"Automatic profile switch failed: {exc}")

    def apply_profile_values(self, name, values, windows_settings=None, automatic=False):
        rec = self.current
        windows_settings = windows_settings if isinstance(windows_settings, dict) else {}
        pending = []
        identified_codes = set()
        for panel in self.identified_control_panels:
            monitor = panel.get("monitor", {})
            if (str(monitor.get("description", "")).casefold() == rec.description.casefold()
                    and str(monitor.get("display", "")).casefold() == rec.display.casefold()):
                try:
                    identified_codes.add(int(panel["vcp_code"]))
                except (KeyError, TypeError, ValueError):
                    continue
        for key, raw_value in values.items():
            try:
                code = int(str(key), 16)
                value = int(raw_value)
                if not 0 <= value <= 0xFFFFFFFF:
                    continue
            except (TypeError, ValueError):
                continue
            if code == 0x60 and automatic:
                continue  # Automatic switching must not interrupt the active video signal.
            if (code in rec.supported or code in identified_codes) and code not in (0xD6, 0xDF):
                pending.append((code, value))
        pending.sort(key=lambda item: 3 if item[0] == 0x60 else 2 if item[0] == 0x14 else 1)
        self.status.setText(f'Applying profile "{name}"…')

        def completed(failures, error):
            if error:
                logger.exception("profile_apply_failed", exc_info=(type(error), error, error.__traceback__))
                self.status.setText(f"Profile apply failed: {error}")
                return
            failures, windows_changed = failures
            if failures:
                self.status.setText(f'Profile "{name}" applied with errors: ' + "; ".join(failures))
                if not automatic:
                    QMessageBox.warning(self, "Profile partially applied", "Some settings failed:\n" + "\n".join(failures))
            else:
                self.status.setText(f'Applied profile "{name}"' + (" automatically." if automatic else "."))
            if windows_changed:
                self.refresh()
            else:
                self.load_controls()

        def write_values():
            failures = []
            windows_changed = False
            for code, value in pending:
                try:
                    write_vcp(rec.handle, code, value)
                except OSError as exc:
                    failures.append(f"0x{code:02X}: {exc}")
            try:
                target_rate = windows_settings.get("refresh_rate_hz")
                if target_rate is not None:
                    current_mode, _rates = available_refresh_rates(rec.display)
                    if int(current_mode.dmDisplayFrequency) != int(target_rate):
                        set_refresh_rate(rec.display, int(target_rate))
                        windows_changed = True
            except Exception as exc:
                failures.append(f"Windows refresh rate: {exc}")
            try:
                target_hdr = windows_settings.get("hdr_enabled")
                if isinstance(target_hdr, bool):
                    current_hdr = get_hdr_state(rec.display)
                    if current_hdr["supported"] and current_hdr["enabled"] != target_hdr:
                        set_hdr_state(rec.display, target_hdr)
                        windows_changed = True
                        time.sleep(1.0)
                        verified_hdr = get_hdr_state(rec.display)
                        if verified_hdr["enabled"] != target_hdr:
                            failures.append("Windows HDR state did not match the profile after the change")
                    elif not current_hdr["supported"]:
                        failures.append("Windows HDR is not supported by the current display path")
            except Exception as exc:
                failures.append(f"Windows HDR: {exc}")
            return failures, windows_changed

        self._submit_task(write_values, completed)

    def save_current_profile(self):
        if not self.current:
            return
        name, accepted = QInputDialog.getText(self, "Save profile", "Profile name:")
        name = name.strip()
        if not accepted or not name:
            return
        try:
            profiles = load_profiles()
            if name in profiles and QMessageBox.question(
                self, "Replace profile?", f'A profile named "{name}" already exists. Replace it?',
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            ) != QMessageBox.Yes:
                return
            rec = self.current
            profile_codes = set(rec.supported)
            for panel in self.identified_control_panels:
                monitor = panel.get("monitor", {})
                if (str(monitor.get("description", "")).casefold() == rec.description.casefold()
                        and str(monitor.get("display", "")).casefold() == rec.display.casefold()):
                    try:
                        profile_codes.add(int(panel["vcp_code"]))
                    except (KeyError, TypeError, ValueError):
                        continue
            profile_codes.difference_update((0xD6, 0xDF))
            self.profile_save_btn.setEnabled(False)
            self.status.setText("Reading all supported monitor settings…")

            def capture_all_settings():
                values = {}
                errors = []
                for code in sorted(profile_codes):
                    try:
                        values[f"{code:02X}"] = read_vcp(rec.handle, code)["current"]
                    except OSError as error:
                        errors.append(f"0x{code:02X}: {error}")
                windows_settings = {"refresh_rate_hz": None, "hdr_enabled": None}
                try:
                    current_mode, _rates = available_refresh_rates(rec.display)
                    windows_settings["refresh_rate_hz"] = int(current_mode.dmDisplayFrequency)
                except Exception as error:
                    errors.append(f"Windows refresh rate: {error}")
                try:
                    hdr_state = get_hdr_state(rec.display)
                    if hdr_state["supported"]:
                        windows_settings["hdr_enabled"] = bool(hdr_state["enabled"])
                except Exception as error:
                    errors.append(f"Windows HDR: {error}")
                return values, errors, windows_settings

            def captured(result, error):
                self.profile_save_btn.setEnabled(True)
                if error:
                    logger.exception("profile_capture_failed", exc_info=(type(error), error, error.__traceback__))
                    QMessageBox.warning(self, "Could not save profile", str(error))
                    return
                values, read_errors, windows_settings = result
                try:
                    profiles[name] = {
                        "monitor": rec.description,
                        "values": values,
                        "windows_settings": windows_settings,
                    }
                    save_profiles(profiles)
                    self.reload_profiles()
                    self.profile_combo.setCurrentIndex(self.profile_combo.findData(name))
                    if read_errors:
                        self.status.setText(
                            f'Saved profile "{name}" with {len(read_errors)} unreadable setting(s).'
                        )
                        logger.warning(
                            "profile_capture_incomplete monitor=%s failures=%s", rec.description, read_errors
                        )
                        QMessageBox.warning(
                            self, "Profile saved with unreadable settings",
                            "Readable settings were saved. These settings could not be read:\n\n"
                            + "\n".join(read_errors),
                        )
                    else:
                        self.status.setText(f'Saved all readable settings in profile "{name}".')
                except Exception as exc:
                    logger.exception("profile_save_failed")
                    QMessageBox.warning(self, "Could not save profile", str(exc))

            self._submit_task(capture_all_settings, captured)
        except Exception as exc:
            QMessageBox.warning(self, "Could not save profile", str(exc))

    def apply_selected_profile(self):
        name = self.profile_combo.currentData()
        if not self.current or not name:
            return
        try:
            profile = load_profiles().get(name)
            if not isinstance(profile, dict) or not isinstance(profile.get("values"), dict):
                raise ValueError("Profile data is invalid")
            values = profile["values"]
            windows_settings = profile.get("windows_settings", {})
            if profile.get("monitor") != self.current.description:
                answer = QMessageBox.question(
                    self, "Different monitor",
                    "This profile was saved for a different monitor. Apply supported settings anyway?",
                    QMessageBox.Yes | QMessageBox.No, QMessageBox.No
                )
                if answer != QMessageBox.Yes:
                    return
            if "60" in values and QMessageBox.question(
                self, "Switch input source?",
                "Applying this profile may switch inputs and disconnect the active video signal. Continue?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            ) != QMessageBox.Yes:
                return
            has_windows_settings = (
                isinstance(windows_settings, dict)
                and any(windows_settings.get(key) is not None for key in ("refresh_rate_hz", "hdr_enabled"))
            )
            if has_windows_settings and QMessageBox.question(
                self, "Apply Windows display settings?",
                "This profile also changes Windows refresh rate or HDR. The display may briefly blank. Continue?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            ) != QMessageBox.Yes:
                return
            self.apply_profile_values(name, values, windows_settings=windows_settings)
        except Exception as exc:
            QMessageBox.warning(self, "Could not apply profile", str(exc))

    def delete_selected_profile(self):
        name = self.profile_combo.currentData()
        if not name or QMessageBox.question(
            self, "Delete profile?", f'Delete profile "{name}"?',
            QMessageBox.Yes | QMessageBox.No, QMessageBox.No
        ) != QMessageBox.Yes:
            return
        try:
            profiles = load_profiles()
            profiles.pop(name, None)
            save_profiles(profiles)
            self.reload_profiles()
            self.status.setText(f'Deleted profile "{name}".')
        except Exception as exc:
            QMessageBox.warning(self, "Could not delete profile", str(exc))

    def select_monitor(self, index):
        if index < 0 or index >= len(self.records):
            return
        self._last_auto_profile = None
        self.current = self.records[index]
        rec = self.current
        self.load_windows_display_settings(rec)
        self.refresh_identified_control_panels()
        self.load_identified_panel_values(rec)
        supported_names = [FEATURES.get(code, f"VCP 0x{code:02X}") for code in sorted(rec.supported)]
        self.info.setText(
            f"{rec.description}  ·  {rec.display}\n"
            f"Available controls: {', '.join(supported_names) or 'none reported'}"
        )
        self.raw.setText("Raw capabilities: " + (rec.capabilities or "(unavailable)"))
        self.load_controls()

    def load_windows_display_settings(self, record):
        self._windows_display_ready = False
        self._windows_display_name = record.display
        self.refresh_rate_combo.clear()
        self.refresh_rate_combo.setEnabled(False)
        self.refresh_rate_btn.setEnabled(False)
        self.hdr_toggle_btn.setEnabled(False)
        self.hdr_state_label.setText("Reading Windows display capabilities…")
        self.hdr_state_label.setProperty("supported", False)
        display_name = record.display

        def read_settings():
            current_mode = rates = rate_error = hdr_state = hdr_error = None
            try:
                current_mode, rates = available_refresh_rates(display_name)
            except Exception as exc:
                rate_error = exc
            try:
                hdr_state = get_hdr_state(display_name)
            except Exception as exc:
                hdr_error = exc
            return current_mode, rates, rate_error, hdr_state, hdr_error

        def loaded(result, error):
            if record is not self.current or display_name != self._windows_display_name:
                return
            if error:
                logger.exception("windows_display_settings_read_failed", exc_info=(type(error), error, error.__traceback__))
                self.hdr_state_label.setText(f"Windows settings unavailable: {error}")
                self.hdr_state_label.setProperty("supported", False)
                self._windows_display_ready = False
                return
            current_mode, rates, rate_error, hdr_state, hdr_error = result
            self.refresh_rate_combo.clear()
            if rate_error:
                logger.warning("refresh_rate_query_failed display=%s error=%s", display_name, rate_error)
            else:
                for rate in rates:
                    self.refresh_rate_combo.addItem(f"{rate} Hz", rate)
                current_index = self.refresh_rate_combo.findData(int(current_mode.dmDisplayFrequency))
                if current_index >= 0:
                    self.refresh_rate_combo.setCurrentIndex(current_index)
            if hdr_error:
                logger.warning("hdr_query_failed display=%s error=%s", display_name, hdr_error)
                self.hdr_state_label.setText(f"HDR status unavailable: {hdr_error}")
                self.hdr_state_label.setProperty("supported", False)
                self.hdr_toggle_btn.setText("HDR unavailable")
            elif hdr_state["supported"]:
                self.hdr_state_label.setProperty("supported", True)
                self.hdr_state_label.setText("On" if hdr_state["enabled"] else "Off")
                self.hdr_toggle_btn.setText("Turn HDR off" if hdr_state["enabled"] else "Turn HDR on")
            else:
                self.hdr_state_label.setProperty("supported", False)
                self.hdr_state_label.setText("Not supported by this display path")
                self.hdr_toggle_btn.setText("HDR unavailable")
            self._windows_display_ready = True
            self.refresh_rate_combo.setEnabled(bool(rates) and rate_error is None)
            self.refresh_rate_btn.setEnabled(bool(rates) and rate_error is None)
            self.hdr_toggle_btn.setEnabled(bool(hdr_state and hdr_state["supported"]))

        self._submit_task(read_settings, loaded)

    def apply_refresh_rate(self):
        if not self.current or not self._windows_display_ready:
            return
        target_rate = self.refresh_rate_combo.currentData()
        display_name = self._windows_display_name
        if target_rate is None:
            return
        self.refresh_rate_btn.setEnabled(False)
        self.status.setText(f"Applying {target_rate} Hz…")

        def applied(previous_mode, error):
            if error:
                logger.exception("refresh_rate_apply_failed", exc_info=(type(error), error, error.__traceback__))
                self.status.setText(f"Could not apply {target_rate} Hz: {error}")
                QMessageBox.warning(self, "Refresh rate change failed", str(error))
                self.refresh_rate_btn.setEnabled(True)
                return
            self._confirm_refresh_rate(display_name, previous_mode, target_rate)

        self._submit_task(lambda: set_refresh_rate(display_name, target_rate), applied)

    def _confirm_refresh_rate(self, display_name, previous_mode, target_rate):
        dialog = QDialog(self)
        dialog.setWindowTitle("Confirm refresh rate")
        layout = QVBoxLayout(dialog)
        countdown_label = QLabel(
            f"The display is set to {target_rate} Hz. Keep this setting? It will revert automatically in 15 seconds."
        )
        countdown_label.setWordWrap(True)
        layout.addWidget(countdown_label)
        buttons = QHBoxLayout()
        keep_button = QPushButton("Keep this setting")
        revert_button = QPushButton("Revert now")
        keep_button.setObjectName("primaryButton")
        buttons.addWidget(keep_button)
        buttons.addWidget(revert_button)
        layout.addLayout(buttons)
        remaining = {"seconds": 15}
        timer = QTimer(dialog)

        def update_countdown():
            remaining["seconds"] -= 1
            if remaining["seconds"] <= 0:
                timer.stop()
                dialog.reject()
                return
            countdown_label.setText(
                f"The display is set to {target_rate} Hz. Keep this setting? "
                f"It will revert automatically in {remaining['seconds']} seconds."
            )

        timer.timeout.connect(update_countdown)
        keep_button.clicked.connect(dialog.accept)
        revert_button.clicked.connect(dialog.reject)
        timer.start(1000)
        accepted = dialog.exec() == QDialog.DialogCode.Accepted
        timer.stop()
        self.refresh_rate_btn.setEnabled(True)
        if accepted:
            self.status.setText(f"Kept {target_rate} Hz.")
            self.load_windows_display_settings(self.current)
            return
        self.status.setText("Restoring the previous refresh rate…")

        def restored(_result, error):
            if error:
                logger.exception("refresh_rate_restore_failed", exc_info=(type(error), error, error.__traceback__))
                self.status.setText(f"Could not restore previous refresh rate: {error}")
                QMessageBox.critical(self, "Refresh rate restore failed", str(error))
            else:
                self.status.setText("Previous refresh rate restored.")
            if self.current:
                self.load_windows_display_settings(self.current)

        self._submit_task(lambda: restore_display_mode(display_name, previous_mode), restored)

    def toggle_hdr(self):
        if not self.current or not self._windows_display_ready:
            return
        display_name = self._windows_display_name
        currently_enabled = self.hdr_state_label.text() == "On"
        target_enabled = not currently_enabled
        self.hdr_toggle_btn.setEnabled(False)
        self.hdr_state_label.setText("Changing HDR…")
        self.status.setText("Changing Windows HDR state…")

        def change_and_verify():
            api = set_hdr_state(display_name, target_enabled)
            time.sleep(1.0)
            state = get_hdr_state(display_name)
            return api, state

        def changed(result, error):
            if error:
                logger.exception("hdr_toggle_failed", exc_info=(type(error), error, error.__traceback__))
                self.hdr_state_label.setText(f"HDR change failed: {error}")
                self.status.setText(f"Could not change Windows HDR state: {error}")
                self.hdr_toggle_btn.setEnabled(True)
                QMessageBox.warning(self, "HDR change failed", str(error))
                return
            api, state = result
            self.hdr_state_label.setText("On" if state["enabled"] else "Off")
            self.hdr_toggle_btn.setText("Turn HDR off" if state["enabled"] else "Turn HDR on")
            self.hdr_toggle_btn.setEnabled(state["supported"])
            self.status.setText(
                f"Windows reports HDR {'on' if state['enabled'] else 'off'} after the change ({api} API)."
            )
            if state["enabled"] != target_enabled:
                QMessageBox.warning(self, "HDR state not confirmed", "Windows did not report the requested HDR state.")

        self._submit_task(change_and_verify, changed)

    def load_controls(self):
        rec = self.current
        if not rec:
            self.set_controls_enabled(False)
            return
        self._updating = True
        self.set_controls_enabled(False)
        self.status.setText("Reading monitor controls…")

        def loaded(state, error):
            if rec is not self.current:
                return
            self._updating = False
            if error:
                logger.exception("monitor_read_failed", exc_info=(type(error), error, error.__traceback__))
                self.status.setText(f"Could not read monitor controls: {error}")
                self.set_controls_enabled(True)
                return
            self._populate_controls(rec, state)

        self._submit_task(lambda: read_monitor_state(rec), loaded)

    def _populate_controls(self, rec, state):
        self._updating = True
        self.set_controls_enabled(True)
        self.preset.clear()
        self.input_source.clear()

        for code, slider, label in ((0x10, self.brightness, self.brightness_label),
                                    (0x12, self.contrast, self.contrast_label)):
            value = state.get(code)
            if value:
                maximum = value["maximum"] or 100
                slider.setRange(0, maximum)
                slider.setValue(value["current"])
                label.setText(f'{value["current"]} / {maximum}')
                slider.setEnabled(True)
            else:
                slider.setEnabled(False)
                label.setText("unsupported / unreadable")

        for code, spin in self.rgb.items():
            value = state.get(code)
            if value:
                spin.setValue(value["current"])
                spin.setEnabled(True)
            else:
                spin.setEnabled(False)

        preset_codes = rec.supported.get(0x14, [])
        for code in preset_codes:
            self.preset.addItem(PRESET_NAMES.get(code, f"Preset 0x{code:02X}"), code)
        value14 = state.get(0x14)
        if value14:
            index = self.preset.findData(value14["current"])
            if index >= 0:
                self.preset.setCurrentIndex(index)
        self.preset.setEnabled(bool(preset_codes and value14))

        input_codes = rec.supported.get(0x60, [])
        for code in input_codes:
            self.input_source.addItem(INPUT_NAMES.get(code, f"Input 0x{code:02X}"), code)
        input_value = state.get(0x60)
        if input_value:
            index = self.input_source.findData(input_value["current"])
            if index >= 0:
                self.input_source.setCurrentIndex(index)
        self.input_source.setEnabled(bool(input_codes and input_value))
        self.status.setText("Values loaded. Slider changes apply on release.")
        self._updating = False

    def write_current(self, code, value):
        if self._updating or not self.current:
            return
        if code not in self.current.supported or code in (0xD6, 0xDF):
            return
        rec = self.current
        self.status.setText(f"Writing VCP 0x{code:02X}…")

        def written(_result, error):
            if error:
                logger.exception("vcp_write_failed", exc_info=(type(error), error, error.__traceback__))
                self.status.setText(f"Write failed for 0x{code:02X}: {error}")
                QMessageBox.warning(self, "DDC/CI write failed", str(error))
            else:
                self.status.setText(f"Wrote VCP 0x{code:02X} = {value}.")

        self._submit_task(lambda: write_vcp(rec.handle, code, value), written)

    def write_preset(self, index):
        if index < 0:
            return
        code = self.preset.itemData(index)
        if code is not None:
            self.write_current(0x14, code)

    def write_input(self, index):
        if index < 0:
            return
        code = self.input_source.itemData(index)
        if code is not None:
            if QMessageBox.question(
                self, "Switch input source?",
                "Changing input source may disconnect the active video signal. Continue?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No
            ) == QMessageBox.Yes:
                self.write_current(0x60, code)
            else:
                self.load_controls()

def main():
    app = QApplication(sys.argv)
    app.setOrganizationName("MonitorControl")
    app.setApplicationName("MonitorControl")
    configure_logging()
    logger.info("application_started")
    window = MainWindow()
    window.show()
    app.aboutToQuit.connect(window.shutdown)
    sys.exit(app.exec())

if __name__ == "__main__":
    main()
