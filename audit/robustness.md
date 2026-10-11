# Robustness audit (Agent R)

Repo: `degconsultinggroup/desktop-vitals` at `8e5d82c` on branch `harden`.
Read-only review of `src/DesktopVitals.cs`, `build.ps1`, `install.ps1`, `src/app.manifest`, README.
Scope: throw, hang, or wrong data on a PC unlike the author's. No new features. Fixes must follow harden hard rules.

Severity: **C** crash or stuck main UX; **H** blank or wrong core readings on common machines; **M** hang worker / delayed card / secondary wrong data; **L** cosmetic or rare edge.

## Highest severity (summary)

1. **C** `CoreLayout` can throw on startup if a processor core reports an empty affinity mask (`Value[0]`).
2. **H** English-only GPU performance counter names: no NVIDIA and non-English Windows leave Graphics / Video memory as "not available" (or blank until fallback), with no clearer message.
3. **H** First-run review waits on `TaskList.Ready`. If Task Scheduler COM `Connect()` hangs, review never appears and the task worker stays busy forever.
4. **H** `build.ps1` hard-requires `Framework64` and `/platform:x64`. Native ARM64 without usable x64 build tools fails to build; under x64 emulation, PEB walks misread native ARM64 processes.
5. **H** Memory row divides by `ms.total` with no check. Failed `GlobalMemoryStatusEx` can yield Infinity / nonsense percents.

## Findings

### Processor layout and per-thread bars

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R1 | `src/DesktopVitals.cs:84` | C | `CoreLayout` ends with `ThenBy(c => c.Value[0])`. A core entry with an empty thread list throws `IndexOutOfRangeException` during `Build()` and the window never opens (odd firmware, multi-group edge, or bad buffer parse). | Skip cores with `Value.Length == 0` before sort. If the list is empty after that, build a flat `0 .. ProcessorCount-1` layout so the strip still matches `ThreadTimes`. |
| R2 | `src/DesktopVitals.cs:62-85`, `1413-1415`, `1686-1694` | H | `GetLogicalProcessorInformationEx` fails or returns nothing (old host, restricted VM): strip caption shows "0 threads" while `Environment.ProcessorCount` is used for timing arrays. Bars never update. Single-core and no E-core machines are fine when the API works; empty layout is the bug. | Same fallback as R1. Caption should use the fallback thread count, not "0 threads". |
| R3 | `src/DesktopVitals.cs:76-78` | M | Affinity mask is one 64-bit group only. Machines with more than 64 logical processors or multiple processor groups drop threads. Bars incomplete; busiest-thread text wrong. | Read `GroupCount` and each `GROUP_AFFINITY` (group + mask). Map to a stable index space, or cap display and say "showing first group" without adding features beyond honest labeling. Prefer fewer lines: if multi-group, fall back to flat `ProcessorCount` bars and drop hybrid labels. |
| R4 | `src/DesktopVitals.cs:939-943` | L | No efficiency cores: hybrid is false; caption is "N threads" only. Correct. Single core: one bar. Correct when R1/R2 fixed. | None beyond R1/R2. |

### GPU: NVIDIA, AMD, Intel, none, non-English

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R5 | `src/DesktopVitals.cs:670,696`, `2265-2266` | H | Counter category/counter names are English (`GPU Engine`, `Utilization Percentage`, `GPU Adapter Memory`, `Dedicated Usage`, `GPU Process Memory`, `Local Usage`). Non-English Windows: `ReadCategory` throws, `Have = false`, UI shows "not available". Same for VRAM-by-app list (stays empty). Matches known problem 5. | Prefer index lookup via `Perflib\009` (English) names to local names; on total failure set a sticky message "GPU readings not available on this system" on both Graphics and Video memory rows (not a blank detail). Do not add new deps. |
| R6 | `src/DesktopVitals.cs:128-171`, `1705-1723` | M | No `nvml.dll` (AMD, Intel, basic display): Nvml returns false; path falls through to GpuCounters. OK if counters exist. No GPU counters at all: "not available". Acceptable once R5 message is clear. | Keep fallback chain. Align copy with R5. |
| R7 | `src/DesktopVitals.cs:164-168` | M | Nvml init succeeds but temp/power/util calls fail (some laptops, headless, driver subset). Method still returns true and UI shows `0°C · 0 W` and 0% as if real. | Return true only if util (and optionally memory) succeeded. Omit temp/power segments when those calls fail. Never invent zeros for missing sensors. |
| R8 | `src/DesktopVitals.cs:155` | L | Only adapter index 0. Multi-GPU PCs show the wrong card vs the one driving the desktop. | Out of scope as a feature. Document as known limit in README later (Phase 4). No code feature add in harden unless PLAN says so. |
| R9 | `src/DesktopVitals.cs:661-705` | M | First GpuCounters sample needs a previous timestamp; util stays 0 for one interval while `Have` may already be true. Brief wrong 0% on AMD/Intel. | Do not set `Have` for util until a delta exists, or leave value blank until the second sample. |

### Memory, disk, missing drive

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R10 | `src/DesktopVitals.cs:1699-1702` | H | `GlobalMemoryStatusEx` ignored return value. If it fails, `ms.total` is 0: division yields Infinity; percent text and bar wrong. | If call fails or `total == 0`, set "not available" and frac 0 (Bar already guards NaN). |
| R11 | `src/DesktopVitals.cs:1708` | M | Nvml memory total 0 with used set: `vu / vt` is Infinity before Bar clamp; percent string wrong. | Same guard as existing GpuCounters branch (`t > 0`). |
| R12 | `src/DesktopVitals.cs:1726-1736` | L | System drive missing or not ready: catch sets detail "not available". OK. Bot check paths on a missing drive return null last-sign (BotView "no sign"). OK. | None required. |

### Task Scheduler disabled or stuck

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R13 | `src/DesktopVitals.cs:597-615`, `1852` | H | `needReview` waits for `TaskList.Ready`. Background `Schedule.Service` `Connect()` can hang if the service is stuck. `busy` stays true; `read` stays default; review never opens. Tasks card stays empty with no explanation. | Wrap Connect/Walk in a short timeout (cooperative: abort flag after N seconds and set `read` so Ready becomes true). On failure, keep Items empty and optionally one subtitle or tasks detail "Task Scheduler not available". First-run review must not depend on a successful Connect; gate on tickCount only, or on Ready-or-failed. |
| R14 | `src/DesktopVitals.cs:606-610` | M | Service disabled: Connect usually throws; Items empty; Ready true; tasks hidden. Quiet and OK, but user cannot tell schedule read failed vs no tasks. | After failed Connect, set a sticky "not available" once on the Task card or leave collapsed and mention in review Missed path only if needed. Prefer one short empty-state string when Ready and Items empty after an error flag. |
| R15 | `src/DesktopVitals.cs:2064-2076` | H | `WakeSensor` runs `schtasks /run` when temperature file is stale. Breaks read-only rule; fails for standard users / disabled scheduler; can confuse "temperature starting" state. Known problem 2. | Delete `WakeSensor`, `temperature-task` setting, and docs. Stale file: show "temperature sensor off" or "temperature stale" without starting anything. |

### APPDATA empty, settings, unbounded reads

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R16 | `src/DesktopVitals.cs:365-372`, `2533-2534` | M | Empty or missing `%APPDATA%\DesktopVitals` is fine: Load returns empty; SaveReview creates the directory. If ApplicationData points at an unavailable network location, CreateDirectory/Write throw; SaveReview swallows errors and the review looks saved but is not. | On save failure, keep the review open and show a one-line error in the review footer (no new card feature: reuse existing label). Still no admin. |
| R17 | `src/DesktopVitals.cs:381,397,413`, `2490` | M | `File.ReadAllLines` on huge `watch.txt` can hitch or OOM on first load / review save. Known problem 8. | Cap 64 KB, 200 lines, 500 chars/line; skip rest; show "some settings lines were skipped". |
| R18 | `src/DesktopVitals.cs:808-832` | M | `CountCrashes` reads an entire log with no size/line cap. Multi-GB supervisor log blocks a ThreadPool worker; bot status stays Busy; Last work column goes stale. | Cap bytes (e.g. last 1 MB via seek) and line count. Skip on failure. |
| R19 | `src/DesktopVitals.cs:788-804` | M | `NewestWrite` walks folders with `GetFiles` / `GetDirectories` unbounded. Huge data dirs hang the bot check worker. | Cap files per level (e.g. 200) and keep depth 4. On error return null. |
| R20 | `src/DesktopVitals.cs:2046-2047` | L | Temperature JSON `ReadToEnd` unbounded. | Cap 64 KB. |

### HTTP health checks (hang / wrong host)

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R21 | `src/DesktopVitals.cs:767-784` | H | Any URL; follows redirects; unbounded body. Slow or huge response blocks the bot worker up to timeout; non-loopback is a policy break (known problem 3). | Loopback `http` only; `AllowAutoRedirect = false`; max 64 KB; else invalid with "check must be a local address". |

### ARM64, build, non-admin

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R22 | `build.ps1:4-15` | H | Requires `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` and `/platform:x64`. Pure ARM64 environments without that path fail the build. | Keep x64 as primary (hard rule: one in-box csc). Document ARM: build/run under Windows x64 emulation. Optional tiny fallback to `Framework` only if PLAN allows; do not add SDKs. |
| R23 | `src/DesktopVitals.cs:208-229`, `259-282` | H | Exe is x64. `SYSTEM_PROCESS_INFORMATION` offsets are x64 (OK under emulation). `CurrentDirectory` PEB offsets are x64; native ARM64 target processes fail reads. Relative script paths needing cwd are missed on WoA. | Keep skip-on-failure. Comment that cwd is best-effort for same-arch processes. Do not add architecture detection features beyond a short comment. |
| R24 | `src/DesktopVitals.cs:233-255`, `manifest asInvoker` | M | Standard user: elevated processes often deny query; Cmd empty; some scripts/AI lines missing. Not a crash. | Keep current skip. No admin elevation. |
| R25 | `src/app.manifest:14-15` | L | `asInvoker` and PerMonitorV2 DPI: correct for non-admin and scaling. | None. |

### Display: 4K, 100% scale, light and dark

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R26 | `src/DesktopVitals.cs:1329-1331`, manifest DPI | L | Width 400 DIP; MaxHeight from WorkArea. At 4K 100% and at 100% 1080p, WPF + PerMonitorV2 scale correctly. Tall content scrolls. | None for layout. |
| R27 | `src/DesktopVitals.cs:2584-2590` | M | Snapshot `RenderTargetBitmap` at 2x ActualWidth/Height. Very tall window on large desktop can OOM or throw; catch writes "Snapshot failed". | Cap bitmap dimensions (e.g. max 4000 px) or scale factor down to fit. Still only vitals.png. |
| R28 | `src/DesktopVitals.cs:1395`, `1574-1598` | M | Theme is always dark. `DwmSetWindowAttribute(20)` forces immersive dark. Light mode Windows still get a dark glass window. Not a crash; acrylic off uses opaque dark fill. Low contrast only if DWM glass fails oddly. | Keep forced dark product look, or read `AppsUseLightTheme` only to adjust chrome if PLAN asks. Do not invent a full light theme (that is a feature). Ensure opaque fallback stays readable (already dark fill). |
| R29 | `src/DesktopVitals.cs:1513`, `2440` | L | Segoe Fluent Icons missing on older Win10: glyph missing, checkbox empty. | Keep MDL2 fallback already in FontFamily string. |

### Unhandled errors and wrong strings

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R30 | `src/DesktopVitals.cs:2666`, `2678` | H | Dispatcher errors swallowed; no background handler; no log. Stranger PCs show blank rows with no breadcrumb. Known problem 4. | Log to `%APPDATA%\DesktopVitals\errors.log` (timestamp, version, type, message, stack); cap 256 KB; keep running on dispatcher errors. |
| R31 | `src/DesktopVitals.cs:2051` | M | Source bytes are mojibake (`â€¦` instead of ellipsis). UI shows "temperature startingâ€¦". | Replace with ASCII `...` or `\u2026` only. No en/em dash characters. |
| R32 | `src/DesktopVitals.cs:1824` | L | Never-ran task result uses `\u2013` (en dash). Hard rules forbid en dashes in code/docs. | Use ASCII `-` or `n/a`. |

### Culture and PATH

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R33 | `src/DesktopVitals.cs:906-923` | L | `ToString("0.0")` uses current culture; non-English may show comma decimals. Parsing of sensor JSON uses InvariantCulture (good). | Use `CultureInfo.InvariantCulture` in Theme numeric formatters for stable UI. |
| R34 | `src/DesktopVitals.cs:2151-2156` | M | Editor discovery looks for `cursor.cmd` / `code.cmd` on PATH. Store installs often only have `.exe`. Open in editor hint absent; falls back to Explorer. | Also probe `.exe` next to the same PATH entry (still launch exe directly in the security fix for RunEditor; no cmd.exe). |

### Install / build scripts (stranger PC)

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R35 | `build.ps1:8` | M | `Stop-Process -Force` on DesktopVitals while building: fine for same user; surprises if user did not expect the window to close. | Keep for unlock of exe; README already implies rebuild. No change required for robustness of the running app. |
| R36 | `install.ps1:18-19` | M | Start menu / desktop shortcut creation fails if folders redirected and unwritable (`$ErrorActionPreference = 'Stop'`). Whole install aborts after a successful build. | Create shortcuts independently in try/catch; still exit non-zero only if build failed. |

### Snapshot folder

| ID | Where | Severity | Failure scenario | Fix |
|---|---|---|---|---|
| R37 | `src/DesktopVitals.cs:2563-2597` | M | `Directory.CreateDirectory(snapshotDir)` creates arbitrary deep paths; parent missing drives throw. Known problem 7. | Create `<dir>` only if parent exists; write only `vitals.txt` and `vitals.png` under that dir. |

## Scenario checklist

| Scenario | Result today | Primary IDs |
|---|---|---|
| No NVIDIA | Falls back to GpuCounters or "not available" | R5, R6, R7 |
| AMD or Intel GPU | Counters if English locale; else "not available" | R5, R9 |
| No GPU counters | "not available" (message should be clearer) | R5, R6 |
| No efficiency cores | OK if CoreLayout works | R2, R4 |
| Single core | OK if CoreLayout works | R2, R4 |
| ARM64 under x64 emulation | Build may work; cwd/script detection weak for native processes | R22, R23 |
| Non-English Windows | GPU counters fail; some number formatting localized | R5, R33 |
| Standard non-admin | Runs; elevated process details missing; WakeSensor useless | R15, R24 |
| 4K or 100% scale | Layout OK; huge snapshot bitmap risk | R26, R27 |
| Light and dark mode | Always dark chrome | R28 |
| Empty `%APPDATA%` | Creates on save; network APPDATA can fail silently | R16 |
| Task Scheduler disabled | Empty tasks; Ready becomes true after throw | R13, R14 |
| Task Scheduler hung | Review blocked forever | R13 |
| Missing drive | Disk row "not available"; checks null | R12 |

## Confirm known harden problems (robustness angle)

| Known # | Robustness note |
|---|---|
| 2 WakeSensor | R15: also fails when scheduler disabled / non-admin |
| 3 ReadHttp | R21: hang and wrong-host risk |
| 4 error logging | R30 |
| 5 GpuCounters English | R5 |
| 7 snapshot dir | R37 |
| 8 watch.txt caps | R17 |
| 9 bounded reads | R18, R19, R20 |

## Suggested PLAN order (for coordinator)

Security items first (cmd.exe, WakeSensor, ReadHttp, handle bounds), then:

1. R1 + R2 CoreLayout fallback (crash / empty strip)
2. R13 review must not wait on hung Task Scheduler
3. R10 memory total guard
4. R5 GPU counter locale + clear unavailable copy
5. R30 error log
6. R15 delete WakeSensor (if not already from security)
7. R21 ReadHttp bounds (if not already)
8. R17-R20 read caps
9. R7 Nvml honest sensors
10. R31/R32 string cleanup
11. R22/R23 document ARM limits (docs phase) and PEB comment
12. R27 snapshot bitmap cap
13. R36 install shortcut try/catch

Done when: stranger-PC matrix above shows no C items; H items closed or listed as known limits in README from Phase 3 evidence only.
