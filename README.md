# Desktop Vitals

Task Manager is not enough in the age of AI. Desktop Vitals is a small glass window for Windows that shows what your PC is doing: scripts, bots, scheduled tasks, local AI, and system load.

It does not kill any tasks. It only reads and shows. It never starts, stops, or kills other programs.

When you want to open something, it links out to your IDE or File Explorer.

It needs no setup. Open it and it finds what is running on your machine.

## What it can and can't do

- It only reads what is already on the machine and shows it.
- It never starts or stops programs, tasks, or services.
- It uses no network except optional http health checks to `127.0.0.1`, `::1`, or `localhost`.
- It never needs admin rights.
- Unexpected errors append to `%APPDATA%\DesktopVitals\errors.log`.

## What it shows

- **System:** processor load, with a bar for every thread, grouped into performance and efficiency cores when the CPU reports them; memory; graphics load and video memory; free space on the system drive. NVIDIA cards also show temperature and power.
- **Scripts:** every script an interpreter is running from your own files, one line per script, with how long it has been up, its CPU and its memory. This covers Python (including `python -m`), Node, Bun, Deno, PowerShell, batch files, Windows Script, Ruby, PHP, Perl and Java. Scripts that belong to installed apps or to Windows are left out.
- **Scheduled tasks:** the tasks you or your tools created, not Windows' own or app updaters. It shows which are running now, which failed their last run, when each last ran, and when each runs next.
- **AI:** everything AI on the machine in one card, each line tagged with what it is:
  - **Agent:** command-line agents such as Claude Code, Codex, Gemini CLI, OpenClaw and Aider. For agents with one process per session it shows how many sessions are open; hover to see the folder each one works in.
  - **App:** AI desktop apps such as Claude, ChatGPT, Cursor, Windsurf and Grok.
  - **Model:** local model servers (llama.cpp `llama-server`, Ollama, LM Studio, koboldcpp, vLLM), with the model name and port.
  - **Bot** and **Watching:** your own bots and watched apps that are AI (see [Settings](#settings)).
- **Bots** and **Watching:** optional cards you set up yourself. See [Settings](#settings).
- **Top apps:** the busiest apps, grouped, so all Chrome processes count as one line. Click **CPU** or **Memory** to sort.

Hover over a line to see what a click will do:

- **Open in Cursor** or **Open in VS Code** opens a script or folder in your editor, whichever one is installed.
- **Show in File Explorer** opens the program's folder with the file selected.

## Install

You need Windows 10 or 11.

### Download the exe

1. Open the latest [GitHub Release](https://github.com/degconsultinggroup/desktop-vitals/releases).
2. Download `DesktopVitals.exe` (and `SHA256SUMS.txt` if you want to check the hash).
3. Run the exe. No installer and no admin prompt.

The release exe is not code-signed yet (TODO). Windows SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway**.

### Build from source

```powershell
git clone https://github.com/degconsultinggroup/desktop-vitals.git
cd desktop-vitals
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

`install.ps1` builds `bin\DesktopVitals.exe` and adds two shortcuts: one on the desktop, and one in the Start menu that opens with **Ctrl+Alt+P**. `build.ps1` builds without adding shortcuts. The build uses the C# compiler that ships with Windows, so there is nothing else to install.

## First run

The first time it opens, Desktop Vitals shows **Here's what I found**: everything it detected and everything in your settings, grouped by kind. Untick anything that shouldn't be shown, and click a tag if something is the wrong kind (a script that is really a bot, say). **Looks good** saves your answers to your settings file. The gear button at the top brings this screen back any time.

## Use

- **Ctrl+Alt+P** opens the window. Press it again while the window is in front to tuck it away.
- The **Live** switch turns readings on and off. When it is off, or the window is minimized, the app does no work.
- **Esc** or **×** closes it. Nothing starts at logon.

While live, it uses about 0.1% of the machine. One system call per refresh covers every process.

## Settings

Everything works without settings. To add your own Watching or Bots lines, or an optional temperature file, copy `watch.example.txt` to `%APPDATA%\DesktopVitals\watch.txt`, edit it, and reopen the app. Your settings stay outside the app folder, so updates never touch them.

- **Watching:** `Label = match` keeps an eye on an app or a command. A match ending in `.exe` is a process name. Anything else is text found in the command line.
- **Bots:** add `| check:` to a line to track whether a bot is still doing work, not just running. The check is a file the bot rewrites, a folder it writes into, or a local http health address (`127.0.0.1`, `::1`, or `localhost` only). A bot turns amber when its check goes quiet for 5 minutes. Add `| code:` to open its folder in your editor.
- **AI:** lines that name a known AI program (Claude, Cursor, Ollama, OpenClaw ...) go in the AI card instead of Bots or Watching. Add `| ai` to put any other line there, and `| kind: Agent` (or `App`, `Model`, `Bot`, `Watching`) to choose its tag.
- **Hidden and re-tagged items:** the review screen writes `@hide = <name>` for things you untick and `@kind = <name> | Agent` for things you re-tag. Delete those lines to undo them.
- **Processor temperature:** Windows doesn't give CPU temperature to normal apps. If a sensor tool already writes a JSON file, point `@temperature-file` at it. Desktop Vitals only reads that file; it never starts a sensor task.

`watch.example.txt` explains each one with examples.

## Snapshot for scripts and AI agents

```powershell
DesktopVitals.exe --snapshot C:\some\folder
```

Add `--review` to picture the review screen instead. This takes a reading off screen, writes `vitals.txt` (a plain-text report) and `vitals.png` (a picture of the full window) to that folder, and exits. It takes about 5 seconds and doesn't disturb an open window. A chat bot or an AI agent can run it to answer "how's my PC doing?" with text or a screenshot.

In `vitals.txt`, bots are marked `[ok]`, `[QUIET]` (running but no sign of work for 5 minutes) or `[DOWN]` (not running).

## Build notes

The whole app is one file, `src\DesktopVitals.cs`, a WPF window written for the C# 5 compiler in `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`. `build.ps1` shows the exact compiler call. Setting `DESKTOPVITALS_NO_NVML=1` before starting it shows the graphics readings that AMD and Intel owners get.

## Tested on

- PASS: GitHub Actions `windows-latest` (x64) smoke
- PASS: GitHub Actions `windows-11-arm` (x64 exe under emulation); cores flat on that image
- Not tested yet (no lab machine): Windows Sandbox / fresh non-admin no-NVIDIA; German UI; Defender / VirusTotal; Windows 10 22H2

## License

MIT. See [LICENSE](LICENSE).
