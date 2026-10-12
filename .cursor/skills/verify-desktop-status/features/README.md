# Desktop status verification map

This directory is the maintained source for verifying the desktop status board. Read the index, then use the matching feature file as the recipe.

## Baseline preconditions

- Repo root is the current directory.
- `python3 verify.py --doctor` prints `DOCTOR OK`.
- Pillow and psutil are installed from `requirements.txt`.
- `pwsh` is on `PATH` for the task-registration feature, or `powershell.exe` on Windows when `pwsh` is absent.
- Set `DEG_RUNTIME_DIR` to a fresh folder for each board run so two runs do not share state.
- Drive fixture mode only. Do not pass a live run that sets the wallpaper unless you are on the tower and you mean to change it.

## Driving conventions

- Start every recipe from the baseline unless its preconditions say otherwise.
- Treat every command as literal.
- Board proof is the `status.html` text, the wallpaper size, and `commands.json`.
- Task proof is the XML emitted by `register-desktop-status.ps1`.
- Do not remove proof artifacts during cleanup.

## Proof and skip reporting

- Capture the command, the exit code, and the files the command wrote.
- A heading without the row text is not enough.
- An empty `commands.json` is how you show the run did not call the GPU or the task scheduler.
- Record the feature id and the fixture or script you used.
- If a path cannot be run, report the command and the missing precondition. Do not mark it verified through a different path.

## Feature entry contract

Each feature file starts with an H1 and one paragraph, then exactly four H2 sections in this order: `Sub-features`, `How to get to it (user POV)`, `Driving it with verify.py`, and `Gotchas`.

## Features

- [Healthy board](./healthy-board.md) covers a green wallpaper and detail page from `fixtures/healthy`.
- [Attention board](./attention-board.md) covers red and amber lines when readings cross a threshold.
- [Missing readings](./missing-readings.md) covers grey "not available" lines when a source has nothing to say.
- [Task schedule](./task-schedule.md) covers logon plus a 2-minute clock, and backing up the previous task.
- [Runtime paths](./runtime-paths.md) covers script-relative paths and the gitignored runtime folder.
- [Desktop overlay](./overlay.md) covers the frosted panel, the ON/OFF pill, and skipping a redraw when nothing changed.
- [Disk events](./disk-events.md) covers counting only disk-provider errors and warnings, so Kernel-Boot 153 stays out.
