# Desktop Vitals

A glass window for what this PC is doing. It only reads.

<img src="docs/screenshot.png" alt="Desktop Vitals" width="560">

## What it shows

Processor, memory, graphics, video memory, and disk. Then agents, your things by project, models, other, and what ended in the last hour.

## How it decides

It is yours when it runs as you, in your session. A scheduled task you created counts.

The project is the nearest folder with git or a package file.

A listening port is a server. A weight file is a model. Five quiet minutes is quiet. A failed exit or task shows failed.

An agent shows its repo, the command it is running, and the last file it touched.

## Install

Paste this link into your agent:

https://github.com/degconsultinggroup/desktop-vitals

```powershell
irm https://raw.githubusercontent.com/degconsultinggroup/desktop-vitals/v2/simplest/install.ps1 | iex
```

Or download [DesktopVitals.exe](https://github.com/degconsultinggroup/desktop-vitals/releases/latest).

## Use

The Live switch turns readings on and off. Ctrl+Alt+P opens the window.

Click a name to open its folder. Click a port to open it. Right-click a line to rename, hide, or pin it. Click the PC name to rename this computer.

## Command line

`--json` prints the board. `--snapshot` writes `vitals.txt` and `vitals.png`. `--fixture board.json` draws that board.

`--name`, `--rename`, `--hide`, `--pin`, and `--unpin` save the PC name, renames, hides, and pins in `%APPDATA%\DesktopVitals\prefs.txt`.

## License

MIT. See [LICENSE](LICENSE).

Made by [Frontier Deployments](https://frontierdeployments.com).
