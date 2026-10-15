# Desktop Vitals


Note from Danny - the rest is written by Opus:

Task Manager is just not enough re: what I want to see about my Windows computer. So I built this and use it all the time to show my processor threads (so cool to see), graphics card usage, models downloaded and whether or not they are being served, any harnesses that are active and how many agents they are running, scheduled jobs/crons/whatever. It's just so much more informative than whatever I had before and I didn't need to go looking for something out there, just built what I wanted. I love AI :)

Tell your agent to install it and configure it for your PC, it'll likely find bugs, but it'll fix em!
Danny @ Frontier Deployments / DEG Consulting Group

---

Desktop Vitals just reads what's running and shows you. It never starts or stops anything and doesn't need admin.

Example of how it looks:

<img src="docs/screenshot.png" alt="Desktop Vitals" width="400">

## Install

```powershell
irm https://raw.githubusercontent.com/degconsultinggroup/desktop-vitals/main/install.ps1 | iex
```

Or paste this link into your agent: https://github.com/degconsultinggroup/desktop-vitals

Works on Windows 10 or 11. You can also grab `DesktopVitals.exe` from [Releases](https://github.com/degconsultinggroup/desktop-vitals/releases). It isn't signed yet, so Windows may warn you - click **More info**, then **Run anyway**.

To build it yourself, run `build.ps1`.

## What it shows

Processor with every thread, memory, graphics, video memory, and disk. Then agents, your things by project, models, other, and what ended in the last hour.

## How it decides

It is yours when it runs as you, in your session. A scheduled task you created counts.

The project is the nearest folder with git or a package file.

A listening port is a server. A weight file is a model. Nothing for five minutes is quiet. A failed exit or task shows failed.

An agent shows its repo, the command it is running, and the last file it touched.

## Use

The Live switch turns readings on and off. Ctrl+Alt+P opens the window.

Click a name to open its folder. Click a port to open it. Right-click a line to rename, hide, or pin it. Click the PC name to rename this computer.

## Command line

`--json` prints the board. `--snapshot` writes `vitals.txt` and `vitals.png`. `--fixture board.json` draws that board.

`--name`, `--rename`, `--hide`, `--pin`, and `--unpin` save the PC name, renames, hides, and pins in `%APPDATA%\DesktopVitals\prefs.txt`.

## Tested on

- My PC: Windows 11, i7-14700F, RTX 4070 SUPER, with Claude Code, Codex, Cursor, bots and local models running
- GitHub Actions Windows (x64)
- GitHub Actions Windows 11 ARM

Not tested yet on Windows 10 or a PC without an NVIDIA card.

## License

MIT.

Made with love by Danny ([Frontier Deployments](https://frontierdeployments.com)) prompting Opus 5.5, who managed a bunch of Cursor Grok 4.7 and Composer agents.
