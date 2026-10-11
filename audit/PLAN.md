# Harden PLAN

Merged from known problems 1-10 and Phase 1 audits (`audit/security.md`, `audit/robustness.md`, `audit/lean.md`).
Order: security, then robustness, then lean, then packaging (known problem 10).
One commit per item in Phase 2. Check the box and paste the commit hash when done.
No new features. No new dependencies. Net lines in `src/DesktopVitals.cs` should go down or stay flat.

## Security

### 1. Fix editor and Explorer path launches
Refs: `src/DesktopVitals.cs:2151-2156`, `2191`, `2199-2204`, `2241` (known 1; security N1, N6; robustness R34)
Done when: no `cmd.exe`; editor is a resolved `Cursor.exe` or `Code.exe`; launches use `UseShellExecute = false` with one path that exists; paths with `"`, newline, or `%` are rejected; Explorer open/select uses the same path rules.
Commit: [x] eb74c27

### 2. Delete WakeSensor and temperature-task
Refs: `src/DesktopVitals.cs:375`, `2036-2037`, `2051`, `2062-2077`; `watch.example.txt`; README settings that mention waking a task (known 2; security N2; robustness R15; lean A1)
Done when: `WakeSensor`, `@temperature-task`, and docs for starting a task are gone; stale `@temperature-file` only shows a stale or off message; nothing runs `schtasks`.
Commit: [x] b83a92a

### 3. Restrict and bound HTTP checks
Refs: `src/DesktopVitals.cs:758`, `767-785` (known 3; security N5; robustness R21)
Done when: only `http://` to `127.0.0.1`, `::1`, or `localhost` is accepted; `AllowAutoRedirect` is false; body read max is 64 KB; other URLs show `check must be a local address`.
Commit: [x] 4298c1c

### 4. Log unhandled errors
Refs: `src/DesktopVitals.cs:2666`, `2678` (known 4; robustness R30)
Done when: dispatcher and `AppDomain` unhandled errors append to `%APPDATA%\DesktopVitals\errors.log` with timestamp, version, type, message, and stack; log caps at 256 KB by truncating the oldest half; no foreign process command lines or paths in the log; dispatcher errors stay handled so the app keeps running.
Commit: [x] 668c6cb

### 5. Keep PEB cwd reads read-only and bounded
Refs: `src/DesktopVitals.cs:233-256`, `261-282` (known 6; robustness R23)
Done when: cwd still uses the minimum access mask; handles close in `finally`; reads stay bounded (max 32 KB); failures skip; a short comment states why the read is display-only and best-effort across architectures.
Commit: [x] 51dc146

### 6. Harden snapshot directory writes
Refs: `src/DesktopVitals.cs:2563-2597`, `2661-2669` (known 7; robustness R37)
Done when: `<dir>` is created only if its parent already exists; only `vitals.txt` and `vitals.png` are written under that dir; writes do not follow a path outside `<dir>`.
Commit: [x] f634ded

### 7. Cap watch.txt parsing
Refs: `src/DesktopVitals.cs:376-439`, `2490` (known 8; robustness R17)
Done when: load and review reads cap at 64 KB, 200 lines, and 500 chars per line; over-limit content is ignored and the UI shows `some settings lines were skipped`.
Commit: [x] d78e237

### 8. Bound remaining file reads and handle cleanup
Refs: `src/DesktopVitals.cs:788-804`, `808-831`, `2044-2047` and every `Process` / stream / native handle path (known 9; security N3; robustness R18-R20)
Done when: temperature JSON, crash-log scans (if still present), and `NewestWrite` walks are size or count capped; every handle, stream, and `Process` uses `using` or `try/finally`; oversized reads skip safely.
Commit: [x] 2278d6f

## Robustness

### 9. CoreLayout empty-mask fallback
Refs: `src/DesktopVitals.cs:62-85`, `84`, `1413-1415`, `1686-1694` (robustness R1, R2)
Done when: cores with empty thread lists are skipped; if none remain, the strip uses a flat `0 .. ProcessorCount-1` layout; the caption never shows `0 threads` when `ProcessorCount` is greater than 0; startup no longer throws on empty affinity.
Commit: [x] d4ab4b2

### 10. Multi-group processor fallback
Refs: `src/DesktopVitals.cs:76-78` (robustness R3)
Done when: machines with more than one processor group or more than 64 logical processors fall back to flat `ProcessorCount` bars without hybrid labels, or show an honest first-group limit without new product features.
Commit: [x] 160b818

### 11. GPU counters on non-English Windows
Refs: `src/DesktopVitals.cs:670`, `696`, `2265-2266` (known 5; robustness R5, R6)
Done when: counter names resolve via `Perflib\009` index lookup when needed; on total failure Graphics and Video memory show `GPU readings not available on this system` (not a blank detail).
Commit: [x] 109b1d6

### 12. Honest GPU samples
Refs: `src/DesktopVitals.cs:128-171`, `164-168`, `661-705`, `1705-1723` (robustness R7, R9)
Done when: Nvml does not report fake `0` temp, power, or util when those calls failed; GpuCounters util is not marked ready until a real delta exists (no first-interval fake 0%).
Commit: [x] f1654b9

### 13. Guard memory and VRAM totals
Refs: `src/DesktopVitals.cs:1699-1702`, `1708` (robustness R10, R11)
Done when: failed or zero `GlobalMemoryStatusEx` total shows not available with frac 0; Nvml VRAM percent uses the same `t > 0` guard as the counter path.
Commit: [x] d895914

### 14. Task Scheduler hang must not block review
Refs: `src/DesktopVitals.cs:597-615`, `1852` (robustness R13, R14)
Done when: Connect/Walk cannot block review forever (timeout or failed flag sets Ready); first-run review can open without a successful Connect; failed read shows a short not-available empty state rather than hanging Busy.
Commit: [x] 45df370

### 15. Surface review save failures
Refs: `src/DesktopVitals.cs:365-372`, `2533-2534` (robustness R16)
Done when: if create or write of settings fails, the review stays open and shows a one-line error in the existing footer or label area.
Commit: [x] 6ec761d

### 16. Cap snapshot bitmap size
Refs: `src/DesktopVitals.cs:2584-2590` (robustness R27)
Done when: `RenderTargetBitmap` dimensions are capped (about 4000 px) or the scale factor is reduced so huge windows do not OOM; still only `vitals.png`.
Commit: [x] 9e4c716

### 17. Fix bad UI dash and mojibake strings
Refs: `src/DesktopVitals.cs:1824`, `2051` (robustness R31, R32)
Done when: temperature starting text uses ASCII `...` or `\u2026` (not mojibake); never-ran or similar task text uses ASCII `-` or `n/a` (no en dash `\u2013`).
Commit: [x] 06d4108

### 18. Format theme numbers with invariant culture
Refs: `src/DesktopVitals.cs:906-923` (robustness R33)
Done when: Theme numeric formatters use `CultureInfo.InvariantCulture` so decimals stay stable on non-English locales.
Commit: [x] ca1d42e

### 19. Make install shortcuts independent
Refs: `install.ps1:18-19` (robustness R36)
Done when: Start menu and desktop shortcut creation each use try/catch; a shortcut failure does not abort after a successful build; non-zero exit remains for build failure only.
Commit: [x] 60cd799

## Lean

### 20. Deduplicate SplitArgs
Refs: `src/DesktopVitals.cs:486-499`, `2087`, `2116-2129` (lean B1)
Done when: `VitalsWindow.SplitArgs` is removed; `ModelLabel` calls `Scripts.SplitArgs`.
Commit: [x] 31f8b19

### 21. Remove unused origOn
Refs: `src/DesktopVitals.cs:2381`, `2421`, `2485` (lean B2)
Done when: `origOn` is not built, passed, or accepted by `SaveReview`.
Commit: [x] 3ec2f10

### 22. Merge Short and Ago
Refs: `src/DesktopVitals.cs:1858-1872` (lean B3)
Done when: one helper covers both; call sites keep the same display text with fewer lines.
Commit: [x] 47eccda

### 23. ConfigFile always APPDATA
Refs: `src/DesktopVitals.cs:364-373` (lean B4)
Done when: settings path is always `%APPDATA%\DesktopVitals\watch.txt`; exe-adjacent fallbacks are gone.
Commit: [x] c9cdf3f

### 24. Remove Task Manager caption button
Refs: `src/DesktopVitals.cs:1499-1503` (lean D1)
Done when: the caption control that starts `taskmgr.exe` is gone; gear, Live, minimize, and close remain.
Commit: [x] bf97bb7

### 25. Remove focus switching and Task Scheduler MMC launch
Refs: `src/DesktopVitals.cs:2214`, `2231-2237` (lean D2; hard rule 1)
Done when: row clicks no longer call `SetForegroundWindow` / `ShowWindow` on other windows and no longer start `taskschd.msc`; editor/Explorer open paths stay only after item 1 is safe.
Commit: [x] 40ce263

### 26. Remove Live switch DropShadow
Refs: `src/DesktopVitals.cs:1488` (lean D3)
Done when: the Live switch has no `DropShadowEffect`.
Commit: [x] 04053fe

### 27. Remove bot crash log feature
Refs: `src/DesktopVitals.cs:352`, `362`, `427-429`, `744-746`, `760`, `807-833`, `1881-1893`, `2245-2252`; `watch.example.txt` crash/log samples (lean C1)
Done when: `| log:`, `| crash:`, `CountCrashes`, and crash jump-to-line are gone; `check:` and folder `| code:` remain.
Commit: [x] 4ffeab4

### 28. Trim HTTP health JSON extras
Refs: `src/DesktopVitals.cs:767-784` (lean C2)
Done when: loopback GET still sets last-sign / working; parsing of `uptimeMs`, `failing`, and `ready` into Problem/Uptime is removed.
Commit: [x] e52e52f

## Line budget (Phase 2 gate)

Net `src/DesktopVitals.cs` must be flat or down vs main (2685). Added after items 1-28 left the file at 2802.

### 30. Remove per-app VRAM list
Refs: lean E4; phase2-gate shrink list
Done when: `ShowVram`, `vramList`, `vramByPid`, `vramBusy`, `vramRead`, and `vramLines` are gone; System card keeps total video memory only.
Commit: [x] edf8ae8

### 31. Remove "Did I miss anything?"
Refs: lean E1; phase2-gate shrink list
Done when: missed-app discovery, Missed review card, and `Native.AppWindows` (plus PInvoke used only by it) are gone; the rest of first-run review stays.
Commit: [x]

### 32. Remove bar animations (if still over budget)
Refs: lean E3; phase2-gate shrink list
Done when: thread strip and StatRow bars set scale directly with no `DoubleAnimation`; only if items 30-31 leave the file over 2685 lines.
Commit: [ ]

## Packaging

### 29. Ship prebuilt exe and checksums
Refs: README install section; release assets (known 10; Phase 4 packaging may land the GitHub Actions piece)
Done when: a prebuilt `DesktopVitals.exe` is published with `SHA256SUMS.txt`; README first install is download the exe; source build with Git and ExecutionPolicy is second.
Commit: [ ]

## Dropped (not in this PLAN)

| Finding | Why dropped |
|---|---|
| Security N4: omit `Environment.MachineName` from snapshot | Privacy nice-to-have; not required to close harden risks; Phase 4 can note if needed. |
| Robustness R8: multi-GPU adapter pick | New feature; document as known limit in Phase 4. |
| Robustness R22: ARM64 build path / docs | Keep x64 in-box `csc` (hard rule 3); ARM notes belong in Phase 4 docs. |
| Robustness R28: light theme | New feature. |
| Robustness R4, R12, R24, R25, R26, R29, R35 | No code change required or already acceptable. |
| Security optional `taskmgr` `UseShellExecute` polish | Replaced by lean item 24 (delete the button). |
| Lean E2: remove whole first-run review | Large product cut; only if Danny asks. |
