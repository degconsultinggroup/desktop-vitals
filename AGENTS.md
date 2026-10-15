# Desktop Vitals

Desktop Vitals is a small Windows window that shows what this PC is doing: agents, the things you started, models, and system load.
It only reads.

## Install

Paste this link into your agent, or run:

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
& "$env:LOCALAPPDATA\Programs\DesktopVitals\DesktopVitals.exe" --json "$env:TEMP\desktop-vitals.json"
```

`--version` prints `0.2.0`. `--json` prints the board as UTF-8 JSON and exits.

The board has `name`, `strip`, `headline`, and `groups`. `strip` is processor, memory, graphics, video memory, disk, and `threads`. `headline` has `text` and `needs_you`. Each group has `title`, `kind` (`agents`, `project`, `models`, `other`, or `ended`), and `things`.

A thing has `id`, `name`, `kind`, `state`, `project`, `branch`, `folder`, `ports`, `cpu`, `ram_mb`, `vram_mb`, `count`, `since`, `detail`, `why`, and `pinned`.

If `--json` prints nothing, read the file path you passed.

## Launch

The one-liner starts the app. To open it again, run the exe or press Ctrl+Alt+P.
Click the PC name to rename it, or run `DesktopVitals.exe --name "Studio PC"`.

## Preferences

`--name`, `--rename <id> <name>`, `--hide <id>`, `--pin <id>`, and `--unpin <id>` write `%APPDATA%\DesktopVitals\prefs.txt`.

## Tell the human

Tell them Desktop Vitals is installed and open. Summarize `strip`, `headline`, and the `groups` from `--json`.

## Uninstall

```powershell
$env:DV_UNINSTALL='1'; irm https://raw.githubusercontent.com/degconsultinggroup/desktop-vitals/main/install.ps1 | iex
```

Or, from a clone of this repo:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

This removes `%LOCALAPPDATA%\Programs\DesktopVitals` and the Start menu shortcut.
