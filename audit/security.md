# Desktop Vitals security audit (Phase 1 Agent S)

Repo tip audited: `8e5d82c` on `main` (branch `harden` created from it).
Files read in full: `src/DesktopVitals.cs`, `build.ps1`, `install.ps1`, `src/app.manifest`.
Read only. No app code changes in this phase.

## Verdict

The app is mostly "read and show". Three places break hard rules today: `cmd.exe` for the editor, `schtasks /run` for the temperature sensor, and unbounded HTTP from `watch.txt`. Manifest stays `asInvoker`. Build and install scripts only kill DesktopVitals, compile, and write two shortcuts.

## Known problems 1 to 9

| # | Status | Notes |
|---|---|---|
| 1 | Confirmed | `RunEditor` at 2199-2204 builds `cmd.exe /c` + `Editor[0]` + path args from process cwd, script path, task script, or bot `code`/`log`. `FindEditor` at 2151-2156 looks for `cursor.cmd` / `code.cmd` on PATH, not the real exe. Folder or file with `"`, `&`, newline, or `%` can run extra commands on click. |
| 2 | Confirmed | `WakeSensor` at 2064-2077 runs `schtasks.exe /run /tn "<task>"` where `task` is `@temperature-task` from settings. Starts another program. Breaks "read and show only". Task name with `"` can also break the argument string. |
| 3 | Confirmed | `ReadHttp` at 767-785. URL is `check:` from `watch.txt`. Any scheme starting with `http` (includes `https://`). Default `AllowAutoRedirect` is true. Body is `ReadToEnd` with no size cap. Timeout 3s only. Worst case: SSRF to LAN or internet, redirect chain, large body hangs or blows memory. |
| 4 | Confirmed | `Program.Main` at 2666 and 2678 sets `DispatcherUnhandledException` to `e.Handled = true` with no log. No `AppDomain.CurrentDomain.UnhandledException`. Background thread faults stay silent. |
| 5 | Confirmed (scope) | `GpuCounters` uses English counter category and counter names at 670, 696, and 2265 (`"GPU Engine"`, `"Utilization Percentage"`, `"GPU Adapter Memory"`, `"Dedicated Usage"`, `"GPU Process Memory"`, `"Local Usage"`). Catch sets `Have = false` or empty VRAM. Security impact is low (fail closed). Robustness owns the non-English fix. |
| 6 | Confirmed, refined | `PROCESS_VM_READ` is used in `Procs.CurrentDirectory` (261-282), not in `Procs.CommandLine`. Command lines use `NtQueryInformationProcess` class 60 with `PROCESS_QUERY_LIMITED_INFORMATION` only (233-256). Cwd reads PEB via `ReadProcessMemory`, already capped at 4096 bytes. Handles close in `finally`. Keep read-only access; add the why comment; keep the bound; skip on failure (already mostly done). |
| 7 | Confirmed | `--snapshot <dir>` at 2661-2669; `WriteSnapshot` at 2563-2597. `Directory.CreateDirectory(snapshotDir)` creates the whole tree. Overwrites `vitals.txt` and `vitals.png` only by name, but parent creation is unbounded. Any writable path the user (or a caller) passes is created. Snapshot text also includes `Environment.MachineName` (2569). |
| 8 | Confirmed | `WatchRule.Settings`, `Many`, and `Load` at 376-439 all `File.ReadAllLines` with no size, line, or length caps. Same for `SaveReview` read at 2490. Hostile or huge `watch.txt` can stall parse, feed bad `check`/`log`/`code`/`temperature-*` into other sinks. |
| 9 | Confirmed, partly | Process handles in `CommandLine`, `CurrentDirectory`, `ImagePath` use `try/finally` + `CloseHandle`. `AllocHGlobal` freed in those paths. Gaps: temperature JSON `ReadToEnd` (2047), HTTP body `ReadToEnd` (775), crash log full scan (808-831), `watch.txt` full reads, snapshot `CreateDirectory` without parent check. `Process.Start` results are disposed with `using` where started. |

## install.ps1 and build.ps1

### install.ps1 (19 lines)

Does exactly:

1. Calls `build.ps1` (which may stop a running DesktopVitals; see below).
2. Creates Start menu shortcut `%Programs%\Desktop Vitals.lnk` with hotkey `CTRL+ALT+P`.
3. Creates desktop shortcut `%Desktop%\Desktop Vitals.lnk` with no hotkey.

Shortcut fields: `TargetPath` = `bin\DesktopVitals.exe` under the clone, `WorkingDirectory` = `bin`, icon from the exe, fixed description string. No Run key, no scheduled task, no network, no other files, does not launch the app. Safe to re-run. No unexpected behavior found.

### build.ps1 (17 lines)

1. Ensures `bin\`.
2. `Get-Process DesktopVitals | Stop-Process -Force` (kills running app so the exe can be overwritten).
3. Invokes in-box `csc.exe` with WPF refs, icon, and `src\app.manifest`.

No other side effects. Kill is expected for rebuild; install inherits it.

### app.manifest

`requestedExecutionLevel` is `asInvoker`, `uiAccess` false. DPI aware. Matches hard rule 4.

## Inventory: every security surface

Format: location, what controls input, worst case, fix.

### Process launches

1. **src/DesktopVitals.cs:1501** `Process.Start(taskmgr.exe)` via shell. Input: caption button click only. Worst case: UAC prompt if elevated Task Manager policy applies; no user path. Fix: optional `UseShellExecute = false` with full path under `%WINDIR%`; low priority.

2. **src/DesktopVitals.cs:2071-2074** `schtasks.exe /run` with `@temperature-task`. Input: `watch.txt` setting. Worst case: starts arbitrary scheduled task; quote break in task name. Fix: delete `WakeSensor`, setting, and docs (problem 2).

3. **src/DesktopVitals.cs:2191** `explorer.exe` with folder or file from OpenCode fallback. Input: script path, cwd, task script, bot paths (other processes / settings). Worst case: odd path with `"` confuses ShellExecute args. Fix: validate path exists; reject `"`, newline, `%`; prefer `UseShellExecute = false` or Explorer-safe API.

4. **src/DesktopVitals.cs:2203-2204** `cmd.exe /c` + editor + args. Input: same as OpenCode. Worst case: command injection; runs attacker-chosen commands as the user. Fix: resolve `Cursor.exe` / `Code.exe`; `UseShellExecute = false`; one validated path arg; never `cmd.exe` (problem 1). Also fix FindEditor at 2155 to look for `.exe` not only `.cmd`.

5. **src/DesktopVitals.cs:2214** `taskschd.msc` via shell. Input: click on task row without script. Worst case: opens MMC snap-in only. Fix: none required for security.

6. **src/DesktopVitals.cs:2241** `explorer.exe /select,"<image path>"`. Input: `QueryFullProcessImageName` for a listed pid. Worst case: crafted image path with `"`. Fix: same path validation as (3).

7. **src/DesktopVitals.cs:2556** `notepad.exe` on `watch.txt` path. Input: config path under APPDATA or app folder. Worst case: opens Notepad on settings file. Fix: keep; path is app-controlled.

8. **build.ps1:8** `Stop-Process -Force` on DesktopVitals. Input: local process name. Worst case: kills the app being rebuilt. Fix: keep; document in README if needed.

### Handles and other-process memory

9. **src/DesktopVitals.cs:190-231** `NtQuerySystemInformation(5)` process list into reusable `AllocHGlobal` buffer. Input: kernel. Worst case: large process table grows buffer (capped by retries). Fix: keep; already retries with grow.

10. **src/DesktopVitals.cs:233-256** `OpenProcess(QUERY_LIMITED)` + `NtQueryInformationProcess(60)` for command line. Input: pid from snapshot. Worst case: reads peer command line (display / match only). Buffer grow capped under 1 MB. Fix: keep; treat string as hostile forever (never execute).

11. **src/DesktopVitals.cs:261-282** `OpenProcess(QUERY_LIMITED | VM_READ)` + PEB walk for cwd. Input: pid. Worst case: reads up to 4096 bytes of peer memory as Unicode path; used for labels and editor open. Fix: keep minimum mask; comment why read-only; keep 32 KB max if struct allows; never pass to a shell (problem 6).

12. **src/DesktopVitals.cs:285-289** `ReadProcessMemory` 8-byte pointer helper. Same as (11).

13. **src/DesktopVitals.cs:292-303** `OpenProcess` + `QueryFullProcessImageName`. Input: pid. Worst case: path used for icon, Explorer, labels. Fix: validate before any launch.

14. **src/DesktopVitals.cs:62-84, 88-104** `AllocHGlobal` for core layout and thread times. Input: kernel sizes. Worst case: alloc fail throws (caught higher). Fix: already `finally` free.

15. **src/DesktopVitals.cs:107-120** `EnumWindows` / DWM for Alt+Tab-like windows; `SetForegroundWindow` / `ShowWindow` on click (2233-2236). Input: pids on the row. Worst case: focuses another window of those pids. Fix: keep (show only, no terminate).

### Network

16. **src/DesktopVitals.cs:767-785** `HttpWebRequest` for bot `check:` URL. Input: `watch.txt`. Worst case: any http(s) host, redirects, unbounded body. Fix: only `http://` to `127.0.0.1`, `::1`, or `localhost`; `AllowAutoRedirect = false`; max 64 KB; else show "check must be a local address" (problem 3).

No other sockets, DNS helpers, or WebClient usage found.

### File reads

17. **src/DesktopVitals.cs:365-372, 376-439, 2490** `watch.txt` via `ReadAllLines` / exists checks. Input: user or drop-in file under APPDATA or next to the exe. Worst case: huge file, hostile settings for check/log/code/temperature. Fix: cap 64 KB, 200 lines, 500 chars/line; skip and message (problem 8).

18. **src/DesktopVitals.cs:2044-2047** `@temperature-file` JSON `ReadToEnd`. Input: settings path. Worst case: huge file into string every UI path that calls `SensorDetail`. Fix: cap read (e.g. 64 KB); delete task wake only, keep optional file read if still wanted.

19. **src/DesktopVitals.cs:788-804** `NewestWrite` on `check:` path. Input: settings. Worst case: walks up to 4 directory levels, all files in each, on a huge tree. Fix: bound file count and skip reparse points; or only allow a single file path.

20. **src/DesktopVitals.cs:808-831** crash log full line scan. Input: `log:` from settings. Worst case: multi-GB log read each 10s refresh. Fix: cap bytes or lines (e.g. last 1 MB / 10k lines).

21. **src/DesktopVitals.cs:2178** `File.Exists` on regex-extracted script path from command line. Display / open path only. Fix: same validation before editor/Explorer.

22. **src/DesktopVitals.cs:529** `File.Exists` on resolved script path. Same.

23. **src/DesktopVitals.cs:2155** `File.Exists` on `PATH\cursor.cmd` / `code.cmd`. Fix: look for real `.exe`.

24. **src/DesktopVitals.cs:2615-2617** `%LOCALAPPDATA%\DesktopVitals\window.txt` placement read. App-owned. Fix: keep; already small parse with try/catch.

25. **src/DesktopVitals.cs:325** `FileVersionInfo.GetVersionInfo` on image path. Display title only. Fix: none.

26. **src/DesktopVitals.cs:332** `Icon.ExtractAssociatedIcon` on image path. Display only. Fix: none.

27. **src/DesktopVitals.cs:1730** `DriveInfo` on system drive root. Fixed root. Fix: none.

28. **Registry reads** at 713 (GPU class, HKLM) and 1578 (theme personalize, HKCU). Read only. Fix: none.

### File writes

29. **src/DesktopVitals.cs:2533-2534** create APPDATA folder; `WriteAllLines` to `watch.txt` after review. User confirmed UI. Worst case: overwrites settings (intended). Fix: keep under `%APPDATA%\DesktopVitals` only.

30. **src/DesktopVitals.cs:2553-2554** create empty settings header if missing before Notepad. Fix: keep.

31. **src/DesktopVitals.cs:2567-2596** snapshot dir create; write `vitals.txt` / `vitals.png`. Input: `--snapshot` arg or default under LocalAppData. Worst case: creates arbitrary directory tree; overwrites those two names. Fix: create `<dir>` only if parent exists; never write outside `<dir>`; only those two names (problem 7).

32. **src/DesktopVitals.cs:2632-2635** write `window.txt` under LocalAppData. App-owned. Fix: keep.

33. **build.ps1:7,14-15** create `bin\`, write `DesktopVitals.exe`. Fix: keep.

34. **install.ps1:9-16** write `.lnk` files to Programs and Desktop. Fix: keep; already scoped.

### COM / counters (read)

35. **src/DesktopVitals.cs:606-647** Task Scheduler `Schedule.Service` walk (skip `\Microsoft`). Read task names, actions, state. Paths can later open in editor. Fix: treat action paths as hostile; never run them (today only OpenCode on script path or MMC).

36. **src/DesktopVitals.cs:670-696, 2265** Performance counters for GPU. English names (problem 5). Fix: robustness; not a privilege issue.

37. **src/DesktopVitals.cs:145-168** NVML via `nvml.dll`. Driver library; no network. Fix: keep optional.

## New findings (beyond 1-9)

N1. **Explorer args** (2191, 2241): paths from other processes are quoted by hand into `explorer.exe` command lines. Same validation rules as the editor fix. Severity: medium if paired with odd paths; lower than cmd.exe.

N2. **schtasks quote break** (2071): even before deleting WakeSensor, `"` in `@temperature-task` breaks `/tn "..."`. Deletion removes this.

N3. **Unbounded temperature JSON and crash logs** (2047, 808-831): same class as problem 9. Cap reads.

N4. **Snapshot embeds machine name** (2569): not code exec; privacy for shared snapshots. Prefer hostname omit or optional later; out of scope unless PLAN asks.

N5. **HTTP match is prefix `http`** (758): accepts `https://` and `httpanything`. Loopback fix must parse URI host and scheme strictly.

N6. **FindEditor `.cmd` only** (2155): forces cmd shim path even after RunEditor is fixed, unless lookup moves to `.exe`.

## What is already fine

- Manifest `asInvoker`.
- No admin requirement in code paths audited.
- Single-instance show signal is a local EventWaitHandle; no network.
- Clicking rows focuses windows or opens files; does not kill processes (except build script stopping self).
- install.ps1 does not add logon persistence.
- Process and memory APIs are used for display; command lines are not executed except through the broken editor/sensor paths above.

## Suggested fix order (for PLAN)

1. Problem 1 + N6 + N1 (editor and path launch hardening).
2. Problem 2 + N2 (delete WakeSensor / temperature-task).
3. Problem 3 + N5 (loopback HTTP only).
4. Problem 8 then 9/N3 (bound watch.txt and all reads).
5. Problem 7 (snapshot parent check).
6. Problem 4 (error log).
7. Problem 6 comment and any remaining handle polish.
8. Problem 5 stays with robustness unless audit-only note is enough.

## install.ps1 surprise check (summary)

Expected: kill via build, two shortcuts, hotkey on Start menu shortcut. Observed: that and nothing else. Pass.
