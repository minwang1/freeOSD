# -*- mode: python ; coding: utf-8 -*-

from PyInstaller.building.datastruct import TOC

excluded_qt_modules = [
    'PySide6.Qt3D*',
    'PySide6.QtCharts',
    'PySide6.QtDataVisualization',
    'PySide6.QtGraphs',
    'PySide6.QtMultimedia*',
    'PySide6.QtPdf*',
    'PySide6.QtPositioning',
    'PySide6.QtQml*',
    'PySide6.QtQuick*',
    'PySide6.QtRemoteObjects',
    'PySide6.QtSensors',
    'PySide6.QtSerialPort',
    'PySide6.QtSpatialAudio',
    'PySide6.QtSvg*',
    'PySide6.QtVirtualKeyboard',
    'PySide6.QtWebChannel',
    'PySide6.QtWebEngine*',
    'PySide6.QtWebSockets',
    'PySide6.QtWebView',
]


a = Analysis(
    ['src/main.py'],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excluded_qt_modules,
    noarchive=False,
    optimize=0,
)

# The app uses Qt Widgets only. Keep the Windows platform plugin and discard
# optional plugins (PDF/image codecs, SVG, alternate platforms, TLS, etc.).
unused_qt_libraries = {
    'Qt6Pdf.dll',
    'Qt6Qml.dll',
    'Qt6QmlMeta.dll',
    'Qt6QmlModels.dll',
    'Qt6QmlWorkerScript.dll',
    'Qt6Quick.dll',
    'Qt6Svg.dll',
    'Qt6VirtualKeyboard.dll',
}
a.binaries = TOC(
    entry for entry in a.binaries
    if not (
        entry[0].replace('\\', '/').startswith('PySide6/plugins/')
        and entry[0].replace('\\', '/') != 'PySide6/plugins/platforms/qwindows.dll'
    )
    and entry[0].replace('\\', '/') != 'PySide6/opengl32sw.dll'
    and entry[0].replace('\\', '/').rsplit('/', 1)[-1] not in unused_qt_libraries
)
a.datas = TOC(
    entry for entry in a.datas
    if '/translations/' not in entry[0].replace('\\', '/').lower()
    and not entry[0].lower().endswith('.qm')
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='MonitorControl',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='MonitorControl',
)
