# Desktop Vitals

A small window for Windows that shows what's running on your PC: scripts, bots, scheduled tasks, AI agents, local models, and CPU/GPU/memory.

It only looks. It never starts or stops anything and doesn't need admin.

<img src="docs/screenshot.png" alt="Desktop Vitals" width="400">

## Install

```powershell
irm https://raw.githubusercontent.com/degconsultinggroup/desktop-vitals/main/install.ps1 | iex
```

Windows 10 or 11. Or grab `DesktopVitals.exe` from [Releases](https://github.com/degconsultinggroup/desktop-vitals/releases). It isn't signed yet, so SmartScreen may warn you: click **More info**, then **Run anyway**.

To build it yourself, run `build.ps1`.

## What it shows

- CPU, memory, GPU, disk
- Scripts you're running
- Apps that start with Windows
- Scheduled tasks
- AI agents, apps, and models
- Your own bots and pinned apps
- Top apps by CPU and memory

Ctrl+Alt+P opens it. To add your own bots or apps, copy `watch.example.txt` to `%APPDATA%\DesktopVitals\watch.txt` and edit it.

## License

MIT. Made by [Frontier Deployments](https://frontierdeployments.com).
