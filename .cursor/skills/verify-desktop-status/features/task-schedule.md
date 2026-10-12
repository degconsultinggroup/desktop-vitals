# Task schedule

Registering the board creates two triggers: one at logon, and one clock that starts a minute later and repeats every 2 minutes for 3650 days. Re-registering backs up the previous task XML, then writes the same definition again.

## Sub-features

- `task-logon` is a logon trigger with no repetition.
- `task-clock` repeats every 2 minutes (`PT2M`) for 3650 days (`P3650D`) with `StopAtDurationEnd` false.
- `task-settings` are hidden, limited, interactive, `IgnoreNew`, and `StartWhenAvailable`.
- `task-backup` copies the previous XML before the new one is written.
- `task-idempotent` emits the same XML when the inputs do not change.

## How to get to it (user POV)

- On Windows, run `powershell -ExecutionPolicy Bypass -File register-desktop-status.ps1`.
- For a proof without registering, run the same script with `-EmitXml`.

## Driving it with verify.py

Preconditions:

- `pwsh` is on `PATH`, or `powershell.exe` on Windows when `pwsh` is absent.
- You have a scratch folder for the XML and the backup.

- **Emit the task.** Run `pwsh -NoProfile -ExecutionPolicy Bypass -File register-desktop-status.ps1 -EmitXml task.xml -UserId 'FIXTURE\boarduser' -RegisteredAt 2026-10-09T18:00:00`. If `pwsh` is missing on Windows, use `powershell.exe` with the same arguments. Exit code is 0.
- **Read the triggers.** `task.xml` has one `LogonTrigger` and one `TimeTrigger`. The logon trigger has no `Repetition`. The time trigger starts at `2026-10-09T18:01:00`, interval `PT2M`, duration `P3650D`, `StopAtDurationEnd` false.
- **Read the settings.** `MultipleInstancesPolicy` is `IgnoreNew`, `StartWhenAvailable` is true, `Hidden` is true, `RunLevel` is `LeastPrivilege`, `LogonType` is `InteractiveToken`, and the user id is `FIXTURE\boarduser`.
- **Back up, then emit again.** Run the same command with `-ExistingXml` pointing at a previous XML and `-BackupDir` pointing at an empty folder. The new `task.xml` matches the first emit. The backup folder contains the previous XML.
- **Proof.** Keep both XML files. `python3 verify.py` asserts this shape for you.

## Gotchas

- A logon trigger whose repetition has no duration and `StopAtDurationEnd` true is the old bug. Do not put the repeat on the logon trigger alone.
- `-EmitXml` does not call `Register-ScheduledTask`. On the tower, omit `-EmitXml` to register.
- The user id is whoever runs the script, unless `-UserId` is set. It must not be a hardcoded home path.
