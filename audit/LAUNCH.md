# Launch harden audit

Branch: `launch/harden`  
Tip includes Danny `132b376` (install points at Releases + Frontier credit), plus audit and Live-cost wording on this branch.  
PR: https://github.com/degconsultinggroup/desktop-vitals/pull/3  
Author of agent commits: Desktop Vitals. Did not touch `cursor/vitals-complete`. No tag, no merge to main.

## Verdict

**GO** to merge `launch/harden` into `main` once CI is green on tip.  
**NO-GO** for strangers downloading an exe until Danny tags `v0.1.0` (no GitHub Release exists yet).

## Evidence table

| check | machine/config | result | evidence | fix commit |
|---|---|---|---|---|
| Process.Start only editor / Explorer / notepad; foreign paths UseShellExecute=false | GHA windows + windows-arm | PASS | smoke `PASS safety-static` | `76075d9` |
| No cmd/powershell/schtasks launch; no Kill / CloseMainWindow / service / task control | same | PASS | smoke `safety-static` | `76075d9` |
| Registry read-only | same | PASS | smoke `safety-static` | `76075d9` |
| Network loopback HTTP only; no other URL / socket stack | same | PASS | ReadHttp loopback + `safety-static`; RFC 2606 sample allowlist | `76075d9`, `6887764` |
| No telemetry / auto-update hooks | same | PASS | smoke `safety-static` | `76075d9` |
| app.manifest asInvoker; exe embeds asInvoker | GHA windows + arm | PASS | `PASS asInvoker-non-admin` | `76075d9` |
| Runtime zero non-loopback TCP; writes only APPDATA / LOCALAPPDATA DesktopVitals + snapshot dir | GHA windows-latest + windows-11-arm, 45s | PASS | `PASS safety-runtime`; run 38602268193 artifact | `23a895a` |
| Local 10 min outbound / write soak | Windows local (optional) | NOT RUN (script shipped) | `tests/safety-runtime-10min.ps1` | `23a895a` |
| Secrets redacted from tips, vitals.txt, errors.log, PNG | GHA windows + arm smoke | PASS | `PASS privacy-secrets` | `f1cb1ce`, `4256d5f`, `f10b860` |
| Locale Perflib de-DE / ja-JP fixtures; invariant GPU decimals | GHA windows + arm | PASS | `PASS locale-perflib` (6 nested) | `bcdc9e3`, `7b928cc` |
| GPU Process Memory via PerfName (non-English) | fixture + source gate | PASS | `rawEnglish=0; PerfNameCalls=3` | `bcdc9e3` |
| Models scan symlink / junction loop | GHA windows + arm | PASS | `PASS models-symlink-loop` (~3-5 s) | `bcdc9e3` |
| Missing Framework64 csc.exe message | build.ps1 | PASS (code) | clear install / repair hint | `6df2ca3` |
| Win10 22H2 / Win11 23H2 desktop / Sandbox | consumer lab | NOT RUN | No Sandbox or desktop lab; GHA is Server 2025 / ARM 26200 | - |
| Discrete NVIDIA / AMD / Intel GPU lab | lab GPUs | NOT RUN | Fallback chain only; no crash on blank GPU | - |
| Real localized OS UI (de-DE / ja-JP) | full OS image | NOT RUN | Fixture only; live GHA is en-US identity map | - |
| Light OS theme | AppsUseLightTheme | Known limit | App forces dark glass | - |
| Accuracy: Scripts / Scheduled / Starts / AI fixtures | GHA windows + arm Accuracy step | PASS | FN=0; product FP=0; run 38602034363 | `9a10f0a`, `dc4e40f`, `e21d412`, `6989517` |
| Live soak: no handle / GDI / thread / private leak | GHA windows-latest, 4 cores, ~8 min | PASS (data) | handles/GDI/private flat; harness fix | `ee81561`, `227e900` |
| Live CPU "~0.1% of the machine" | GHA windows-latest 4-core | Known limit | Measured ~1-2% Live on that runner | `a8fad93` |
| Minimized / Live-off idle | GHA stability | PASS when job green | timer.Stop when !live or minimized | `ee81561` |
| Sleep / resume / RDP / FUS / display unplug | physical Windows | NOT RUN | Known limit | - |
| First-run board then review; grey human reasons | GHA smoke + code path | PASS | board before review; Reading GPU / disk / Task Scheduler | `13e7b75`, `db7af88`, `6d1a2ff` |
| README install points at Releases | docs | PREP | No Release yet; tag `v0.1.0` required before strangers can download | Danny `132b376` (kept) |
| Full smoke + soak on tip | GHA windows + windows-arm + windows-stability | pending / prior green @ `db7af88` | tip `132b376` is README-only after green tip | - |

## Ship-blockers fixed today

1. Safety promises are CI checks (static + 45s runtime), not README-only.
2. Secrets no longer land in hover tips, vitals.txt, errors.log, or snapshot PNGs.
3. Locale VRAM-by-process counters use PerfName; GPU decimals stay invariant.
4. Model folder scan cannot hang on junction / symlink loops.
5. Accuracy fixtures prove Scripts, Scheduled, Starts, and AI with FN=0 and no Edge/OEM/svchost/updater noise in those cards.
6. Stability harness no longer fails a healthy soak; no leak/crash on ~8 min Live.
7. First-run shows the board before review; grey rows use short human reasons.

## Known limits (plain words)

- No Windows Sandbox, Win10 22H2 desktop, Win11 23H2 desktop, or real German/Japanese OS lab. Closest proof is GHA Server 2025 and windows-11-arm (build 26200) x64 emulation.
- Stability soak is about 8 minutes, not 2 hours. No sleep/resume, RDP, Fast User Switching, or multi-monitor unplug tests.
- Live cost on a 4-core GHA runner was about 1-2% of the machine, not 0.1%. README wording matches that evidence.
- The app stays dark even when Windows is set to light theme.
- Discrete GPU cards were not measured on lab hardware. Blank GPU paths do not crash.
- Ollama / llama-server paths were not exercised on GHA.
- DPI 150/200, OneDrive placeholders, unicode usernames, and true >64-CPU hosts remain lab gaps.
- GHA runners are often already Administrators; non-admin is enforced as asInvoker (no elevation request), not "runner is a standard user".
- Runtime outbound proof in CI is 45 seconds; the optional 10 minute script is local-only.
- There is still no GitHub Release. Strangers cannot download an exe until Danny tags `v0.1.0`.

## Release gate (Danny)

Do not run until `launch/harden` is on `main`. No tag from this agent.

```powershell
git checkout main
git pull origin main
git tag -a v0.1.0 -m "Desktop Vitals v0.1.0"
git push origin v0.1.0
```

Then open https://github.com/degconsultinggroup/desktop-vitals/releases and confirm `DesktopVitals.exe` + `SHA256SUMS.txt`.

## Evidence sources

- `internal/launch/safety.md`
- `internal/launch/privacy.md`
- `internal/launch/compat.md`
- `internal/launch/accuracy.md`
- `internal/launch/stability.md`
- `internal/launch/ux.md`
