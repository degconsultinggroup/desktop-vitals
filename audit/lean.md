# Lean audit: Desktop Vitals

Repo file at commit `8e5d82c` (latest `main` when audited). One source file: `src/DesktopVitals.cs` (2685 lines). Scripts and docs checked for surface area only. No app code changed in this phase.

Goal: code that does not serve "read and show". Propose deletions. No new features. Prefer fewer lines. Respect hard rules (read-only app, no new deps, C# 5, loopback only).

## Verdict

Clear fat sits in three places: a settings path that starts a scheduled task, a second copy of argument splitting, and bot crash-log machinery. Smaller dead weight is an unused review parameter and a Task Manager launch button. Keep auto-detect cards (system, scripts, tasks, AI, top apps). Keep optional Watching and Bots checks that only read. Keep the first-run review unless PLAN wants a bigger cut later.

**Total proposed removable (recommend + larger, CS only): about 165 lines.**  
**Docs/example cleanup tied to those cuts: about 15 lines.**  
**Optional further cuts (not counted in the 165): about 50 to 220 lines.**

## Keep (serves read and show)

| Area | Why keep |
|---|---|
| System card (CPU, threads, memory, GPU, VRAM, disk) | Core readings |
| Scripts, scheduled tasks, top apps | Core auto-detect |
| AI card + `Identify` / `ModelLabel` | Documented product surface |
| Watching / Bots with `check:` file, folder, or http | Optional read of health |
| `@temperature-file` read in `SensorDetail` | Read-only sensor display |
| First-run / gear review (`ShowReview`, `@hide`, `@kind`, `@reviewed`) | Settings UX already shipped |
| Snapshot `--snapshot` | Read, write own report files |
| Glass / Live switch / window placement | Window chrome for the viewer |

## Deletion proposals

Line ranges refer to `src/DesktopVitals.cs` unless noted. Savings are estimates after a careful delete (call sites and fields go too).

### A. Must delete (breaks hard rule "read and show only")

#### A1. `WakeSensor` and `@temperature-task`

| | |
|---|---|
| Lines | `2062-2077` (`lastWake`, `WakeSensor`); call at `2051`; comments `375`, `2036-2037` |
| Also | `watch.example.txt` `37`, `41`; README Settings blurb if it implies waking a task |
| Savings | **~18 lines CS**, **~5 lines docs** |
| Why | Runs `schtasks.exe /run` from a settings string. Starts another program. Harden known problem #2. |
| Keep | `SensorDetail` and `@temperature-file` (read JSON only). On stale `ts`, show a stale message. Do not start anything. |

### B. Dead or duplicate (safe small wins)

#### B1. Duplicate `SplitArgs`

| | |
|---|---|
| Lines | `2116-2129` (`VitalsWindow.SplitArgs`); callers `2087` |
| Savings | **~14 lines** |
| Why | Same loop as `Scripts.SplitArgs` at `486-499`. Call `Scripts.SplitArgs` from `ModelLabel` instead. |

#### B2. Unused `origOn` in review save

| | |
|---|---|
| Lines | `2381`, `2421`, `2485` parameter |
| Savings | **~4 lines** |
| Why | Built and passed into `SaveReview`, never read. Dead. |

#### B3. Merge `Short` and `Ago`

| | |
|---|---|
| Lines | `1858-1872` |
| Savings | **~7 lines** |
| Why | Same thresholds. One helper with a flag or format string is enough. Display text can stay the same. |

#### B4. `ConfigFile` path fallbacks beyond APPDATA

| | |
|---|---|
| Lines | `364-373` |
| Savings | **~5 lines** |
| Why | Docs say settings live in `%APPDATA%\DesktopVitals\watch.txt`. Extra probes next to the exe and one folder up are unused for the normal install. Return the APPDATA path always. OpenSettingsFile already creates it. |

### C. Settings and helpers nobody needs (larger, still lean)

#### C1. Bot crash log: `| log:`, `| crash:`, `CountCrashes`

| | |
|---|---|
| Lines | parse `427-429`; fields `352`, `362`, `744-746`; `CountCrashes` `807-833`; call `760`; BotView crash text `1881-1893`; `OpenBot` crash jump `2245-2252`; example docs in `watch.example.txt` `16-17`, `20` |
| Savings | **~55 lines CS**, **~6 lines docs** |
| Why | Not needed to show running or quiet. File/folder/http `check:` already covers "still working". Crash counting reads whole logs and adds editor jump-to-line. Niche. Dropping it shrinks Bots to match "read and show". |
| Keep | `check:` and `| code:` open folder (folder only, no crash line). |

#### C2. HTTP check JSON schema extras

| | |
|---|---|
| Lines | `767-784` body parse for `uptimeMs`, `failing`, `ready` |
| Savings | **~12 lines** (keep short "got a response" path) |
| Why | Overfitted to one health JSON shape. A loopback GET that sets `LastSign` is enough for quiet/working. Drop `Problem` / `Uptime` from http unless a later PLAN item needs them. |

### D. Over-built UI and actions (not read and show)

#### D1. Task Manager caption button

| | |
|---|---|
| Lines | `1499-1503` |
| Savings | **~5 lines** |
| Why | Starts `taskmgr.exe`. Not a reading. Users already have Task Manager. Gear, Live, minimize, close stay. |

#### D2. Click actions that change other windows

| | |
|---|---|
| Lines | focus path in `GoTo` `2231-2237` (`SetForegroundWindow` / `ShowWindow`); Task Scheduler launch `2214` |
| Savings | **~12 lines** if removed; more if `AppWindows` / related PInvoke only served clicks |
| Why | Hard rule: do not change another program. Bringing a window forward and opening `taskschd.msc` are control, not display. |
| Keep for now (security rewrite, not lean delete) | Open folder in editor / Explorer for scripts and bots, once `RunEditor` is fixed to no `cmd.exe`. |
| Note | If PLAN drops focus switching, also trim `Hint` strings and unused `Native.AppWindows` if review "missed apps" no longer needs it. Review still uses `AppWindows` at `2366`. |

#### D3. Live switch DropShadowEffect

| | |
|---|---|
| Lines | `1488` |
| Savings | **~1 line** |
| Why | Pure polish. No reading value. |

### E. Optional (do not take unless PLAN wants a bigger cut)

#### E1. "Did I miss anything?" review section

| | |
|---|---|
| Lines | `2354-2372`, UI at `2405-2409` |
| Savings | **~35 lines** |
| Why | Extra discovery UI on top of auto-detect and hand-written `watch.txt`. Users can still add Watching lines by hand. |

#### E2. Whole first-run review screen

| | |
|---|---|
| Lines | `2321-2544` plus gear button `1498`, `ReviewItem`, related fields |
| Savings | **~220 lines** |
| Why | Large. Works today. Deleting it forces all hide/kind edits through the text file. Only cut if Danny wants a much smaller v1. |

#### E3. Bar / thread strip animations

| | |
|---|---|
| Lines | animation bits in `984-995`, `1020-1030`, `1606-1608` |
| Savings | **~10 lines** if set scale directly |
| Why | Motion only. Readings stay if `ScaleX` / `ScaleY` are set without `DoubleAnimation`. Low priority. |

#### E4. Per-app VRAM list under Video memory

| | |
|---|---|
| Lines | `2254-2319`, `vramList` wiring in `Build` / `Tick` |
| Savings | **~70 lines** |
| Why | Nice detail, not required once `vramRow` shows total. Keep unless line budget is tight after security fixes. |

## Do not delete for lean

- `ReadHttp` itself: keep, but bound and loopback-only (security/robustness). Lean only trims JSON extras (C2).
- `Nvml`, `GpuCounters`, `DESKTOPVITALS_NO_NVML`: needed for show / test.
- `PROCESS_VM_READ` command lines: needed for scripts and AI labels (harden #6 keep).
- Snapshot and `--review`: small, useful for tests.
- `install.ps1` / `build.ps1`: already small. No lean cut.

## Suggested PLAN order (lean items only)

Do security fixes for launch and http before or with A1. Then:

1. A1 WakeSensor / temperature-task (required)
2. B1 SplitArgs dedupe
3. B2 dead `origOn`
4. B3 Short/Ago merge
5. B4 ConfigFile APPDATA only
6. D1 Task Manager button
7. C1 crash log feature (if PLAN accepts product trim)
8. C2 http JSON extras
9. D2 click-to-focus / taskschd (if PLAN aligns with hard rule)
10. D3 DropShadow
11. E* only if still over line budget

## Line count summary

| Bucket | Est. lines out of `DesktopVitals.cs` |
|---|---|
| A must | 18 |
| B dead/dup | 30 |
| C settings/helpers | 67 |
| D UI/actions (D1+D2+D3) | 18 |
| **Recommend + larger total** | **~165** |
| E optional | 35 to 220 more |
| Docs/example with A1+C1 | ~15 |

After Phase 2, net `src/DesktopVitals.cs` should go down. Security fixes that add bounds and logging should be paid for by these deletes.

## Files touched by this audit only

- Store: `/cursor/stores/bc-0141778a-888d-4fad-8c0f-e1621ccaf5aa/internal/audit/lean.md`
- Repo (branch `harden`): `audit/lean.md`
