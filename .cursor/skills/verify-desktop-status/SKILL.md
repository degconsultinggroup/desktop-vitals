---
name: verify-desktop-status
description: Drive the DEG desktop status board with fixtures and prove the wallpaper, the detail page, and the scheduled-task registration. Use when changing desktop_status.py, the register script, the launcher, or the vitals the board shows.
---

# Verify desktop status

The board is a short-lived Windows command that draws a wallpaper and writes a detail page. Verification runs on the fixture path: `desktop_status.py --fixture <folder> --no-set`. That path uses the same collectors and renderer as a live run. It does not read the GPU, the event log, or the network. A live wallpaper change is a separate run on the tower and must not be started from here.

Two fixture runs can sit side by side when each has its own `DEG_RUNTIME_DIR`. Do not start two live runs. They would both set the one desktop wallpaper.

## Launch

Install once from the repo root:

```bash
python3 -m pip install -r requirements.txt
```

There is no server to keep up. Each drive is one process. Start the healthy board into the proof folder:

```bash
mkdir -p .cursor/skills/verify-desktop-status/artifacts/healthy-board
DEG_RUNTIME_DIR="$PWD/.cursor/skills/verify-desktop-status/artifacts/healthy-board" \
  python3 desktop_status.py --fixture fixtures/healthy --no-set
```

Ready when the process exits 0, stdout contains `Board: All good` and `Wallpaper not changed (fixture)`, and `status.html` exists in that folder.

## Doctor

```bash
python3 verify.py --doctor
```

Ready when stdout ends with `DOCTOR OK`. Doctor does not write `status.html` next to the script. If Pillow or psutil is missing, install from `requirements.txt` and run doctor again. Do not drive a run that failed doctor.

## Drive

`verify.py` is the harness. It shells out to `desktop_status.py --fixture` and to `register-desktop-status.ps1 -EmitXml`. Phrases below are exact substrings of `runtime/status.html` (or of the proof copy).

Drive every feature in `features/` before calling a change verified. One feature is enough only for a first smoke of this skill.

```bash
python3 verify.py
```

A single feature, from a clean proof folder:

```bash
rm -rf .cursor/skills/verify-desktop-status/artifacts/healthy-board
mkdir -p .cursor/skills/verify-desktop-status/artifacts/healthy-board
DEG_RUNTIME_DIR="$PWD/.cursor/skills/verify-desktop-status/artifacts/healthy-board" \
  python3 desktop_status.py --fixture fixtures/healthy --no-set
```

Open `status.html` and confirm the heading `All good`, the phrase `No unexpected restarts in 30 days`, and `commands.json` equal to `[]`.

## Evidence

Write proof under `.cursor/skills/verify-desktop-status/artifacts/`. `python3 verify.py` already fills `healthy-board/`, `attention-board/`, and `missing-board/` plus matching `*-stdout.txt` files.

Each proof needs the command, the exit code, and the resulting files, not only the final heading:

- `status.html` shows the heading and the row text after the run.
- `render/wallpaper-a.png` or `wallpaper-b.png` is 3440 by 1440.
- `commands.json` lists every external command the fixture host was asked to run. A healthy, attention, or missing run must be `[]`. A stale-sensor check is the one case that records `schtasks /run` for `\DEG\DEG-CpuSensorPawnIO`.
- The task check writes XML from `register-desktop-status.ps1` and reads it back. That is the registration the tower will install.

Fixture mode is the production boundary for hardware. Confirm it skipped the live machine by the empty command list and by `Wallpaper not changed (fixture)` in stdout. Do not treat a green heading alone as proof.

## Cleanup

```bash
python3 verify.py --cleanup
```

That deletes `<repo>/runtime` when a scratch render created it. It does not delete `.cursor/skills/verify-desktop-status/artifacts/`. Do not delete a `commands.json` or `status.html` you still need to show.

## Helpers

- `python3 verify.py` runs the full fixture suite and refreshes the proof folders.
- `python3 verify.py --doctor` is the read-only health check.
- `python3 verify.py --cleanup` removes repo `runtime/` only.
- `python3 desktop_status.py --fixture fixtures/<name> --no-set` is one board. `<name>` is `healthy`, `attention`, or `missing`.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File register-desktop-status.ps1 -EmitXml <out.xml> -UserId <DOMAIN\user> -RegisteredAt yyyy-MM-ddTHH:mm:ss` writes the task XML without registering it. On Windows, if `pwsh` is missing, the same arguments work with `powershell.exe`.

Keep this map honest with `/maintain-verification-skill` when the board's lines or the register script change.
