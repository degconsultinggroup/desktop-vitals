# Desktop Vitals

Desktop Vitals is a small Windows window that shows what this PC is doing: scripts, bots, scheduled tasks, local AI, and system load.
It only reads. It does not start or stop other programs.

## Install

In PowerShell, run:

```powershell
irm https://raw.githubusercontent.com/degconsultinggroup/desktop-vitals/main/install.ps1 | iex
```

To install without piping the script into `iex`:

1. Download `DesktopVitals.exe` and `SHA256SUMS.txt` from https://github.com/degconsultinggroup/desktop-vitals/releases/latest
2. Run `Get-FileHash -Algorithm SHA256 .\DesktopVitals.exe` and compare it to the `DesktopVitals.exe` line in `SHA256SUMS.txt`. Stop if they differ.
3. Create `%LOCALAPPDATA%\Programs\DesktopVitals` and copy `DesktopVitals.exe` there.
4. Create a Start menu shortcut named `Desktop Vitals` that runs that exe, with hotkey Ctrl+Alt+P.
5. Start the exe.

## Check

```powershell
& "$env:LOCALAPPDATA\Programs\DesktopVitals\DesktopVitals.exe" --version
& "$env:LOCALAPPDATA\Programs\DesktopVitals\DesktopVitals.exe" --json
```

`--version` prints `0.1.1`. `--json` prints UTF-8 JSON and exits. If `--json` prints nothing, run it with a file path and read that file:

```powershell
& "$env:LOCALAPPDATA\Programs\DesktopVitals\DesktopVitals.exe" --json "$env:TEMP\desktop-vitals.json"
```

The JSON has `strip` (processor, memory, graphics, video memory, disk) and `lines` (section, name, kind, state, cpu, ram_mb, vram_mb, folder).

## Launch

The install command starts the app. To open it again, run the exe or press Ctrl+Alt+P.
Click the name to rename it, or run `DesktopVitals.exe --name "Studio PC"`.

## Tell the human

Tell them Desktop Vitals is installed and open. Summarize `strip` and the `lines` from `--json`.

## Uninstall

```powershell
$env:DV_UNINSTALL='1'; irm https://raw.githubusercontent.com/degconsultinggroup/desktop-vitals/main/install.ps1 | iex
```

Or, from a clone of this repo:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

This removes `%LOCALAPPDATA%\Programs\DesktopVitals` and the Start menu shortcut.
