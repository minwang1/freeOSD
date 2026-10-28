$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    throw "Build this Windows application on native Windows."
}

python -m pip install -r requirements-build.txt
if ($LASTEXITCODE -ne 0) {
    throw "Could not install build requirements."
}

python -m PyInstaller --clean --noconfirm MonitorControl.spec
if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller failed to build MonitorControl."
}

Write-Host "Build complete: dist\MonitorControl\MonitorControl.exe"
