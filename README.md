# Desktop Vitals

Task Manager is not enough in the age of AI. Desktop Vitals is a small glass window for Windows that shows what you started on this PC.

It does not kill any tasks. It only reads and shows. It never starts, stops, or kills other programs.

When you want to open something, it links out to your IDE or File Explorer.

It needs no setup. Open it and it finds what is running on your machine.

<img src="docs/screenshot.png" alt="Desktop Vitals on a fresh Windows machine" width="400">

## What it can and can't do

- It only reads what is already on the machine and shows it.
- It never starts or stops programs, tasks, or services.
- It does not use the network.
- It never needs admin rights.
- Unexpected errors append to `%APPDATA%\DesktopVitals\errors.log`.

## What it shows

One window. The top of the pane is the v0.1.0 system bars: processor with every thread, grouped by core type, then memory, graphics, video memory, and disk. The headline says "All good", or it counts what needs you.

Scripts, scheduled tasks, and models on disk are listed under the folder they belong to. Each line has a state dot, a name, and a short status. Hover a line for the facts behind it. Click a name to open its folder.

## Install

You need Windows 10 or 11.

### Download the exe

1. Open the latest [GitHub Release](https://github.com/degconsultinggroup/desktop-vitals/releases).
2. Download `DesktopVitals.exe` (and `SHA256SUMS.txt` if you want to check the hash).
3. Run the exe. No installer and no admin prompt.

The exe is not code-signed yet. Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway**.

### Build from source

```powershell
git clone https://github.com/degconsultinggroup/desktop-vitals.git
cd desktop-vitals
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

`install.ps1` builds `bin\DesktopVitals.exe` and adds two shortcuts: one on the desktop, and one in the Start menu that opens with **Ctrl+Alt+P**. `build.ps1` builds without adding shortcuts. The build uses the C# compiler that ships with Windows, so there is nothing else to install.

## First run

The window opens on what is already running. If an old `watch.txt` is still there, hides and pins are copied into `%APPDATA%\DesktopVitals\prefs.txt` once.

## Use

- **Ctrl+Alt+P** opens the window. Press it again while the window is in front to tuck it away.
- The **Live** switch turns readings on and off. When it is off, or the window is minimized, the app does no work.
- **Esc** or **×** closes it. Nothing starts at logon.
- Click the PC name to rename it.

While live, it uses about 1-2% of a typical 4-core machine in CI soak (lower on quieter desktops). One system call per refresh covers every process.

## Settings

Zero config. The PC name, renames, hides, and pins live in `%APPDATA%\DesktopVitals\prefs.txt`.

An agent can set the same things from the command line: `--name`, `--rename`, `--hide`, `--pin`, and `--unpin`. `--json` prints the board the window draws.

## Snapshot for scripts and AI agents

```powershell
DesktopVitals.exe --snapshot C:\some\folder
```

Add `--width 1200` to picture a wider window. This takes a reading off screen, writes `vitals.txt` and `vitals.png` to that folder, and exits. It waits until the task and model scans finish, then writes. It doesn't disturb an open window.

`--fixture board.json` draws that board instead of the machine. With `--snapshot`, it writes `vitals.txt` and `vitals.png` for that board.

## Build notes

The app is four files under `src`: `App.cs` (entry, command line, settings), `Engine.cs` (readings), `Window.cs` (the glass window), and `Native.cs` (Windows calls). It is a WPF window written for the C# 5 compiler in `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`. `build.ps1` shows the exact compiler call. Setting `DESKTOPVITALS_NO_NVML=1` before starting it shows the graphics readings that AMD and Intel owners get.

## Tested on

- PASS: GitHub Actions `windows-latest` (x64) smoke
- PASS: GitHub Actions `windows-11-arm` (x64 exe under emulation); cores flat on that image
- PASS: Windows 11 desktop, i7-14700F, RTX 4070 SUPER, with Claude Code, Codex, Cursor, bots and local models running
- Not tested yet (no lab machine): Windows Sandbox / fresh non-admin no-NVIDIA; German UI; Defender / VirusTotal; Windows 10 22H2

## License

MIT. See [LICENSE](LICENSE).

Made by [Frontier Deployments](https://frontierdeployments.com).
