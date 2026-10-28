# Repository Guidelines

## Project Structure & Module Organization

This is a small Windows desktop application. The UI and DDC/CI implementation are in `src/main.py`; Windows refresh-rate and HDR APIs are isolated in `src/display_settings.py`; testable VCP test logic is in `src/vcp_test.py`, with unit tests in `tests/`. `requirements.txt` lists runtime dependencies, `requirements-build.txt` lists packaging dependencies, and `build_windows.ps1` creates the Windows bundle. `README.md` documents setup, behavior, and limitations. There is no separate asset directory. Keep application code in `src/` and tests in `tests/`; update the README when commands, supported controls, or platform requirements change.

## Build, Test, and Development Commands

Use native Windows Python 3.12 or newer; WSL and non-Windows hosts cannot load the Windows monitor APIs. From PowerShell at the repository root:

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python .\src\main.py
```

The final command launches the app. Run `python -m unittest discover -s tests -v` to run the hardware-independent unit tests. Run the application on Windows with a DDC/CI-capable display to check monitor discovery and controls.

Build the onedir Windows package from PowerShell with `python -m pip install -r requirements-build.txt` and `.\build_windows.ps1`.

## Coding Style & Naming Conventions

Follow the existing Python style: four-space indentation, `snake_case` functions and variables, `PascalCase` classes, and uppercase constants. Keep Windows API declarations and monitor-specific mappings explicit, and handle unsupported VCP features gracefully. Avoid adding dependencies unless required; record them in `requirements.txt`.

## Testing Guidelines

Tests use Python's built-in `unittest` framework and descriptive `test_*.py` filenames. Keep tests for hardware-independent logic runnable without a monitor. Hardware-dependent behavior should also be checked against a physical monitor because advertised capabilities may not work in practice.

## Commit & Pull Request Guidelines

Git history is unavailable in this checkout, so no repository-specific commit format can be confirmed. Use short, imperative commit subjects (for example, `Handle missing VCP reads`). Pull requests should explain user-visible behavior and Windows/monitor validation, link related issues when applicable, and include screenshots for UI changes. Call out hardware requirements or limitations.

## Platform and Configuration Notes

The app calls Windows `user32` and `dxva2` APIs directly. Keep platform-sensitive code Windows-specific, avoid committing user settings or credentials, and remember that monitor writes take effect immediately. Refresh-rate changes must keep a timed rollback path. The current app stores window state through Qt `QSettings` and startup configuration in the current user's Windows Run registry key.
