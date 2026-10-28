"""Windows display mode and HDR controls, separate from monitor DDC/CI."""

import ctypes
from ctypes import wintypes


user32 = ctypes.WinDLL("user32", use_last_error=True)

ENUM_CURRENT_SETTINGS = -1
ENUM_DISPLAY_SETTINGS = 0
DM_DISPLAYFREQUENCY = 0x00400000
CDS_TEST = 0x00000002
DISP_CHANGE_SUCCESSFUL = 0
QDC_ONLY_ACTIVE_PATHS = 0x00000002
ERROR_INSUFFICIENT_BUFFER = 122


class _DisplayPosition(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]


class _DisplayModeUnion(ctypes.Union):
    _fields_ = [
        ("position", _DisplayPosition),
        ("orientation", wintypes.DWORD),
        ("fixed_output", wintypes.DWORD),
        ("printer_data", ctypes.c_byte * 16),
    ]


class DEVMODEW(ctypes.Structure):
    _fields_ = [
        ("dmDeviceName", wintypes.WCHAR * 32),
        ("dmSpecVersion", wintypes.WORD),
        ("dmDriverVersion", wintypes.WORD),
        ("dmSize", wintypes.WORD),
        ("dmDriverExtra", wintypes.WORD),
        ("dmFields", wintypes.DWORD),
        ("mode", _DisplayModeUnion),
        ("dmColor", ctypes.c_short),
        ("dmDuplex", ctypes.c_short),
        ("dmYResolution", ctypes.c_short),
        ("dmTTOption", ctypes.c_short),
        ("dmCollate", ctypes.c_short),
        ("dmFormName", wintypes.WCHAR * 32),
        ("dmLogPixels", wintypes.WORD),
        ("dmBitsPerPel", wintypes.DWORD),
        ("dmPelsWidth", wintypes.DWORD),
        ("dmPelsHeight", wintypes.DWORD),
        ("dmDisplayFlags", wintypes.DWORD),
        ("dmDisplayFrequency", wintypes.DWORD),
        ("dmICMMethod", wintypes.DWORD),
        ("dmICMIntent", wintypes.DWORD),
        ("dmMediaType", wintypes.DWORD),
        ("dmDitherType", wintypes.DWORD),
        ("dmReserved1", wintypes.DWORD),
        ("dmReserved2", wintypes.DWORD),
        ("dmPanningWidth", wintypes.DWORD),
        ("dmPanningHeight", wintypes.DWORD),
    ]


user32.EnumDisplaySettingsW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(DEVMODEW)]
user32.EnumDisplaySettingsW.restype = wintypes.BOOL
user32.ChangeDisplaySettingsExW.argtypes = [
    wintypes.LPCWSTR, ctypes.POINTER(DEVMODEW), wintypes.HWND, wintypes.DWORD, wintypes.LPVOID
]
user32.ChangeDisplaySettingsExW.restype = ctypes.c_long


def _current_mode(display_name):
    mode = DEVMODEW()
    mode.dmSize = ctypes.sizeof(mode)
    if not user32.EnumDisplaySettingsW(display_name, ENUM_CURRENT_SETTINGS, ctypes.byref(mode)):
        raise OSError(ctypes.get_last_error(), f"Could not read current display mode for {display_name}")
    return mode


def available_refresh_rates(display_name):
    """Return (current_mode, sorted_rates) for the display's current resolution."""
    current = _current_mode(display_name)
    rates = set()
    index = 0
    while index < 10000:
        mode = DEVMODEW()
        mode.dmSize = ctypes.sizeof(mode)
        if not user32.EnumDisplaySettingsW(display_name, index, ctypes.byref(mode)):
            break
        if (mode.dmPelsWidth == current.dmPelsWidth
                and mode.dmPelsHeight == current.dmPelsHeight
                and mode.dmDisplayFrequency > 1):
            rates.add(int(mode.dmDisplayFrequency))
        index += 1
    rates.add(int(current.dmDisplayFrequency))
    return current, sorted(rates)


def set_refresh_rate(display_name, refresh_rate):
    """Test and apply a refresh rate at the current resolution; return rollback mode."""
    previous = _current_mode(display_name)
    candidate = _current_mode(display_name)
    candidate.dmDisplayFrequency = int(refresh_rate)
    candidate.dmFields |= DM_DISPLAYFREQUENCY
    test_result = user32.ChangeDisplaySettingsExW(display_name, ctypes.byref(candidate), None, CDS_TEST, None)
    if test_result != DISP_CHANGE_SUCCESSFUL:
        raise OSError(f"Windows rejected {refresh_rate} Hz (display mode result {test_result})")
    result = user32.ChangeDisplaySettingsExW(display_name, ctypes.byref(candidate), None, 0, None)
    if result != DISP_CHANGE_SUCCESSFUL:
        raise OSError(f"Could not apply {refresh_rate} Hz (display mode result {result})")
    return previous


def restore_display_mode(display_name, previous_mode):
    result = user32.ChangeDisplaySettingsExW(display_name, ctypes.byref(previous_mode), None, 0, None)
    if result != DISP_CHANGE_SUCCESSFUL:
        raise OSError(f"Could not restore the previous display mode (result {result})")


class LUID(ctypes.Structure):
    _fields_ = [("LowPart", wintypes.DWORD), ("HighPart", wintypes.LONG)]


class DISPLAYCONFIG_RATIONAL(ctypes.Structure):
    _fields_ = [("Numerator", wintypes.DWORD), ("Denominator", wintypes.DWORD)]


class DISPLAYCONFIG_PATH_SOURCE_INFO(ctypes.Structure):
    _fields_ = [
        ("adapterId", LUID), ("id", wintypes.DWORD),
        ("modeInfoIdx", wintypes.UINT), ("statusFlags", wintypes.UINT),
    ]


class DISPLAYCONFIG_PATH_TARGET_INFO(ctypes.Structure):
    _fields_ = [
        ("adapterId", LUID), ("id", wintypes.DWORD),
        ("modeInfoIdx", wintypes.UINT), ("outputTechnology", ctypes.c_int32),
        ("rotation", ctypes.c_int32), ("scaling", ctypes.c_int32),
        ("refreshRate", DISPLAYCONFIG_RATIONAL), ("scanLineOrdering", ctypes.c_int32),
        ("targetAvailable", wintypes.BOOL), ("statusFlags", wintypes.UINT),
    ]


class DISPLAYCONFIG_PATH_INFO(ctypes.Structure):
    _fields_ = [
        ("sourceInfo", DISPLAYCONFIG_PATH_SOURCE_INFO),
        ("targetInfo", DISPLAYCONFIG_PATH_TARGET_INFO),
        ("flags", wintypes.UINT),
    ]


class DISPLAYCONFIG_DEVICE_INFO_HEADER(ctypes.Structure):
    _fields_ = [
        ("type", wintypes.UINT), ("size", wintypes.UINT),
        ("adapterId", LUID), ("id", wintypes.UINT),
    ]


class DISPLAYCONFIG_SOURCE_DEVICE_NAME(ctypes.Structure):
    _fields_ = [
        ("header", DISPLAYCONFIG_DEVICE_INFO_HEADER),
        ("viewGdiDeviceName", wintypes.WCHAR * 32),
    ]


class _AdvancedColorBits(ctypes.Structure):
    _fields_ = [
        ("advancedColorSupported", wintypes.UINT, 1),
        ("advancedColorEnabled", wintypes.UINT, 1),
        ("wideColorEnforced", wintypes.UINT, 1),
        ("advancedColorForceDisabled", wintypes.UINT, 1),
        ("reserved", wintypes.UINT, 28),
    ]


class _AdvancedColorValue(ctypes.Union):
    _fields_ = [("bits", _AdvancedColorBits), ("value", wintypes.UINT)]


class DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO(ctypes.Structure):
    _fields_ = [
        ("header", DISPLAYCONFIG_DEVICE_INFO_HEADER),
        ("color", _AdvancedColorValue),
        ("colorEncoding", wintypes.UINT), ("bitsPerColorChannel", wintypes.UINT),
    ]


class DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2(ctypes.Structure):
    _fields_ = [
        ("header", DISPLAYCONFIG_DEVICE_INFO_HEADER),
        ("value", wintypes.UINT),
        ("colorEncoding", wintypes.UINT), ("bitsPerColorChannel", wintypes.UINT),
        ("activeColorMode", wintypes.UINT),
    ]


class DISPLAYCONFIG_SET_COLOR_STATE(ctypes.Structure):
    _fields_ = [("header", DISPLAYCONFIG_DEVICE_INFO_HEADER), ("value", wintypes.UINT)]


user32.GetDisplayConfigBufferSizes.argtypes = [
    wintypes.UINT, ctypes.POINTER(wintypes.UINT), ctypes.POINTER(wintypes.UINT)
]
user32.GetDisplayConfigBufferSizes.restype = wintypes.LONG
user32.QueryDisplayConfig.argtypes = [
    wintypes.UINT, ctypes.POINTER(wintypes.UINT), ctypes.POINTER(DISPLAYCONFIG_PATH_INFO),
    ctypes.POINTER(wintypes.UINT), ctypes.c_void_p, ctypes.c_void_p,
]
user32.QueryDisplayConfig.restype = wintypes.LONG
user32.DisplayConfigGetDeviceInfo.argtypes = [ctypes.POINTER(DISPLAYCONFIG_DEVICE_INFO_HEADER)]
user32.DisplayConfigGetDeviceInfo.restype = wintypes.LONG
user32.DisplayConfigSetDeviceInfo.argtypes = [ctypes.POINTER(DISPLAYCONFIG_DEVICE_INFO_HEADER)]
user32.DisplayConfigSetDeviceInfo.restype = wintypes.LONG


def _active_target_for_display(display_name):
    for _attempt in range(3):
        path_count, mode_count = wintypes.UINT(), wintypes.UINT()
        result = user32.GetDisplayConfigBufferSizes(
            QDC_ONLY_ACTIVE_PATHS, ctypes.byref(path_count), ctypes.byref(mode_count)
        )
        if result != 0:
            raise OSError(result, "Could not query active Windows display paths")
        paths = (DISPLAYCONFIG_PATH_INFO * path_count.value)()
        modes = (ctypes.c_byte * max(1, mode_count.value * 64))()
        result = user32.QueryDisplayConfig(
            QDC_ONLY_ACTIVE_PATHS, ctypes.byref(path_count), paths,
            ctypes.byref(mode_count), modes, None,
        )
        if result == ERROR_INSUFFICIENT_BUFFER:
            continue
        if result != 0:
            raise OSError(result, "Could not read active Windows display paths")
        for path in paths[:path_count.value]:
            source = DISPLAYCONFIG_SOURCE_DEVICE_NAME()
            source.header = DISPLAYCONFIG_DEVICE_INFO_HEADER(
                1, ctypes.sizeof(source), path.sourceInfo.adapterId, path.sourceInfo.id
            )
            result = user32.DisplayConfigGetDeviceInfo(ctypes.byref(source.header))
            if result == 0 and source.viewGdiDeviceName.casefold() == display_name.casefold():
                return path.targetInfo.adapterId, path.targetInfo.id
        raise OSError(f"Windows display target for {display_name} was not found")
    raise OSError("Display topology changed repeatedly during query")


def _device_info_packet(packet_type, packet_class, adapter_id, target_id):
    packet = packet_class()
    packet.header = DISPLAYCONFIG_DEVICE_INFO_HEADER(
        packet_type, ctypes.sizeof(packet), adapter_id, target_id
    )
    return packet


def get_hdr_state(display_name):
    adapter_id, target_id = _active_target_for_display(display_name)
    build = getattr(__import__("sys").getwindowsversion(), "build", 0)
    if build >= 26100:
        modern = _device_info_packet(15, DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2, adapter_id, target_id)
        result = user32.DisplayConfigGetDeviceInfo(ctypes.byref(modern.header))
        if result == 0:
            return {
                "supported": bool(modern.value & (1 << 4)),
                "enabled": bool(modern.value & (1 << 5)),
                "api": "hdr",
            }
    legacy = _device_info_packet(9, DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO, adapter_id, target_id)
    result = user32.DisplayConfigGetDeviceInfo(ctypes.byref(legacy.header))
    if result != 0:
        raise OSError(result, "Could not read HDR/Advanced Color state")
    return {
        "supported": bool(legacy.color.bits.advancedColorSupported),
        "enabled": bool(legacy.color.bits.advancedColorEnabled),
        "api": "advanced_color",
    }


def set_hdr_state(display_name, enabled):
    adapter_id, target_id = _active_target_for_display(display_name)
    build = getattr(__import__("sys").getwindowsversion(), "build", 0)
    if build >= 26100:
        packet = _device_info_packet(16, DISPLAYCONFIG_SET_COLOR_STATE, adapter_id, target_id)
        packet.value = int(bool(enabled))
        result = user32.DisplayConfigSetDeviceInfo(ctypes.byref(packet.header))
        if result == 0:
            return "hdr"
        # Some display drivers expose only the legacy Advanced Color switch.
    packet = _device_info_packet(10, DISPLAYCONFIG_SET_COLOR_STATE, adapter_id, target_id)
    packet.value = int(bool(enabled))
    result = user32.DisplayConfigSetDeviceInfo(ctypes.byref(packet.header))
    if result != 0:
        raise OSError(result, "Windows could not change HDR/Advanced Color state")
    return "advanced_color"
