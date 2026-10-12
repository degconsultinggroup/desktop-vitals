# desktop-status

One frosted panel stays on the desktop and says, in plain English, what this PC is doing. Read-only. It does not change other tasks or processes.

ON shows the readings and checks about every 30 seconds. OFF leaves the pill and stops collecting. An unchanged board does no redraw. The detail page is still there when you want the long version.

## What the board shows

The panel sits on the right of the desktop, over the wallpaper. Each line has a dot:

- Green: fine
- Amber: worth a glance
- Red: needs attention
- Grey: not available, just information, or switched off

A missing reading is grey, with a short reason, and the rest of the board still draws.

Lines:

- Processor temperature, power, and voltage from the PawnIO sensor file
- Graphics temperature, memory, power, clocks, and voltage when `nvidia-smi` returns them
- Case and CPU fans, from that same sensor file or from the Windows fan list
- Memory use, plus memory speed and voltage when Windows reports them
- Free space on C:
- Disk health: Windows physical-disk status, temperature, wear, read and write errors, hours powered on
- The last 30 days: unexpected restarts, hardware errors, disk errors, application crashes
- Chip protection (BIOS and microcode)
- Whether Windows is waiting on a restart
- How long the PC has been on
- DEG scheduled tasks, the Stage 0 scorecard, and the practice-trader paper result
- Whether the AI bench, Claude, Cursor, and OpenClaw are running
- Which apps are using the most processor and memory

The detail page is `runtime\status.html`. Pictures land in `runtime\render\`. State is `runtime\state.json`. Those files stay out of git.

## Register

From this folder:

```
powershell -ExecutionPolicy Bypass -File register-desktop-status.ps1
```

If the task already belongs to an admin, run that again from an elevated PowerShell. The script says so when Windows returns access denied.

That registers `\DEG\DEG Desktop Status` for the user who runs it:

- At logon
- And on a clock that starts one minute later and repeats every 2 minutes for 3650 days
- Hidden, as that interactive user, RunLevel Limited
- It starts the overlay and leaves it running. A second start is ignored while that process is up
- A missed slot runs when the PC is next available

Running the script again is safe. If the task already exists, its XML is copied to `runtime\task-backups\` first.

The launcher is `run-desktop-status.vbs`. It uses the `.venv` next to the script and starts the overlay. ON and OFF are the pill on the panel. The choice is remembered in `runtime\monitor.txt`.

Turn the scheduled task off and put the old wallpaper back: `restore-wallpaper.ps1`. Remove the task only: `unregister-desktop-status.ps1`.

Draw the old wallpaper once without setting it:

```
.venv\Scripts\python.exe desktop_status.py --no-set
```

Start the overlay yourself:

```
.venv\Scripts\pythonw.exe desktop_status.py --overlay
```

Python packages are `Pillow` and `psutil` (`requirements.txt`). The venv on the tower already has them.

## Verify

On a machine that is not the tower:

```
python -m pip install -r requirements.txt
python verify.py
```

The suite reads `fixtures\` only. It does not query a GPU, read real disks, or use the network. `python verify.py --doctor` checks that the pieces are present. `python verify.py --cleanup` removes a scratch `runtime\` folder and leaves proof files under `.cursor\skills\verify-desktop-status\artifacts\`.

## Thresholds

Green, amber, and red lines live in `THRESHOLDS` at the top of `desktop_status.py`. Each number has a comment that says why it is the line. Change the number, then run `python verify.py`.

Grey "not available" is not a threshold. It means that source had nothing to report.

## On the tower

The fixture suite proves the wording, the colors, and the task XML. These still need one real render after you pull the branch, because this repo cannot see the tower's hardware:

- Fan names and speeds inside the PawnIO `last.json` (or a Windows fan list that actually has speeds)
- `Get-PhysicalDisk` and `Get-StorageReliabilityCounter` (health, wear, errors, hours, temperature)
- `nvidia-smi` voltage. Clocks and power are requested too; voltage is grey if this driver does not return `voltage.graphics`
- Memory voltage from `Win32_PhysicalMemory` (configured speed is the part Windows usually has)
- Live 30-day counts from the Windows event log
