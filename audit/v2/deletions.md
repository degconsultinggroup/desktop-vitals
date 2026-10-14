# Desktop Vitals v2 — deletion inventory (Phase 1)

Baseline: **v0.1.0** on branch `launch/harden` — `src/DesktopVitals.cs` (**3435** lines), `README.md` (112), `watch.example.txt` (44), `install.ps1` (21).  
Target: v2 per shared brief (one list, five rules; zero `watch.txt` config).

Legend: **Delete** = remove in Phase 3 first; **Replace** = rework to serve a v2 rule; **Keep** = stays (may move or shrink).

---

## 1. Feature inventory (v0.1.0 → v2)

### 1.1 UI layout: eight cards + review

| Feature | v0.1.0 lines (approx) | Action | ~Lines saved | v2 replacement (brief rule) |
|--------|------------------------|--------|--------------|-----------------------------|
| **CardFlow** multi-column dashboard | 1661–1717, 1928–1990 | Delete | ~90 | Single scrollable list grouped by project (**#2**); optional compact top strip (**#5**) |
| **System card** (heading + glass card) | 1935–1943, 2067–2099, 2212–2273 | Replace | ~40 net | **Machine strip** at top: CPU · RAM · GPU · VRAM · disk (**#5**), not a full card |
| **AI card** (tagged lines: Agent/App/Model/Bot/Pinned) | 1945–1948, 2279–2288, 2533–2587, 2651–2787, 1626–1646 | Delete | ~420 | One line per *thing* with rolled-up CPU/RAM/VRAM and state (**#1**, **#3**, **#4**); models collapse to one summary line when unloaded (**#4**) |
| **Bot card** (`\| check:` lines) | 1950–1951, 2301–2313, 2468–2487, 1178–1261 | Delete | ~200 | Bots inferred from long-running / periodic script behavior (**#3**); no separate card |
| **Script card** (max 12 lines) | 1952–1955, 2315–2347 | Delete | ~80 | Scripts roll into project groups as thing lines (**#1**, **#2**) |
| **Starts with Windows card** | 1957–1960, 2349–2375, 930–1058 (`StartupList`) | Delete | ~200 | Startup apps/vendor noise removed (**#5**); sign-in tasks fold into thing/task lines only when user-owned |
| **Scheduled / Task card** | 1962–1965, 2377–2414, 838–928 (`TaskList` UI use) | Replace | ~60 net | Same `TaskList` data; next/last run inline on one line per task (**#3**), under project or Other |
| **Watching / Pinned card** (`Label = match` rules) | 1967–1968, 2290–2299, 1593–1601 | Delete | ~50 | **Pin** via right-click → `%APPDATA%\DesktopVitals\` prefs (**Simplicity**); no config file |
| **Top apps card** (exe groups, sort CPU/Mem) | 1969–1989, 2416–2434, 1719–1726, 293–331 (`AppIdentity` for top rows) | Delete | ~120 | Gone (**#5**); exception: outsider >25% CPU or >2 GB → one line under Other |
| **First-run / gear review screen** | 1796–1799, 1827–1828, 1869–1871, 2436–2437, 3026–3230, 2029 | Delete | ~220 | No review UI; Hide/Pin on board (**Simplicity**) |
| **Kind tags** (`Theme.Kinds`, `ProcRow` kind chip, `KindOrder`) | 1295–1310, 1505–1642, 1757–1758, 1902–1914 | Delete | ~120 | No Agent/App/Model/Bot/Pinned tags (**#1**); state dot + text only (**#3**) |
| **Per-thread CPU bars** (`CoreLayout`, `ThreadStrip`, tick loop) | 47–87, 1356–1426, 1777–1778, 1940–1942, 2214–2223, 3247–3248 | Delete | ~130 | Strip shows aggregate CPU only (**#5**) |
| **Subtitle process count** (“Up … · N processes”) | 2275–2277 | Replace | ~5 | Strip-focused subtitle or uptime only |

### 1.2 `watch.txt`, `review.txt`, and directives

| Feature | v0.1.0 lines | Action | ~Lines saved | v2 replacement |
|--------|--------------|--------|--------------|----------------|
| **`watch.txt` file** (`WatchRule.ConfigFile`, `ReadLines`, 1 MB cap) | 348–372, 1888–1915, 2613–2621, 3217–3229 | Delete | ~80 | `%APPDATA%\DesktopVitals\` JSON or ini for hides/pins only; one-time migration |
| **`review.txt`** (`@hide`, `@kind`, `@reviewed`) | 353–356, 1892–1914, 3163–3199 | Delete | ~60 | `hidden` / `pinned` in app state file; no `@reviewed` gate |
| **Rule lines `Label = match`** | 400–427, 1894–1897 | Replace | ~30 net | Migration → **Pin** list (match label + pattern); not editable as text |
| **`\| check:`** (file/folder/http bot health) | 338, 416, 1187–1229, 2468–2487 | Delete | ~100 | **Quiet** = alive, no CPU/IO 5 min (**#3**); optional loopback port on click (**#4**); keep loopback-only guard for any probe |
| **`\| code:`** | 339, 417, 2490–2491, 2990 | Delete | ~15 | Click name → folder in Cursor/VS Code/Explorer (**#4**) |
| **`\| ai`** and **`\| kind:`** | 340–341, 418–423, 2544–2584 | Delete | ~40 | Auto-detect + rollup (**#1**); no kind override |
| **`@models = path`** | 374–385, 2604, 589–836 (scan roots) | Delete config only | ~15 | `ModelStore.Scan` keeps default roots (drive `models`, HF, Ollama, LM Studio, beside-script); no extra dirs from file |
| **`@hide = name`** | 387–398, 1900–1901, 1757 | Replace | ~10 net | **Hide** list in prefs (from migration + UI) |
| **`@kind = name \| Agent`** | 1902–1914, 2580–2584 | Delete | ~25 | Dropped |
| **`@reviewed = date`** | 1828, 3178, 3196 | Delete | ~10 | No first-run review |
| **`@temperature-file = path`** | 2789–2818, README 83, watch.example 38–44 | Delete | ~35 | CPU temp not in v2 strip (GPU temp from NVML stays where available) |
| **`watch.example.txt`** (entire file) | 1–44 | Delete | 44 (file) | Remove from repo; README points to zero config |
| **CLI `--review`, `--accept-review`** | 3411–3413, 1869–1871, smoke/accept tests | Delete | ~25 | `--snapshot` / new `--json` only |
| **Gear “Review what’s shown”** | 2029 | Delete | ~3 | Gear: Hide/Pin management + Frontier Deployments link |
| **Gear refresh copy** (“settings”) | 2028 | Replace | ~1 | “Look again” without settings mention |
| **“settings file too large”** subtitle | 2154, 2277 | Delete | ~4 | N/A without watch.txt |
| **Open settings in Notepad** | 3217–3229 | Delete | ~13 | Dropped |

`watch.example.txt` directive map (documentation only today):

| Directive | Action |
|-----------|--------|
| `Label = match` | Migrate → **Pin** |
| `\| check:` / `\| code:` | Drop (behavior + click-to-folder) |
| `\| ai` / `\| kind:` | Drop |
| `@models` | Drop (scan defaults remain) |
| `@hide` / `@kind` / `@reviewed` | Migrate hides only; drop kind/reviewed |
| `@temperature-file` | Drop |

### 1.3 Detection logic (keep vs merge vs delete)

| Area | Lines (approx) | Action | ~Lines saved | v2 replacement |
|------|----------------|--------|--------------|----------------|
| **`Procs` / `Native` / sampling** | 25–291, 2169–2204 | Keep | 0 | Same single `NtQuerySystemInformation` tick |
| **`Scripts` discovery** | 437–574 | Keep | 0 | Feeds thing rollup + project path (**#1**, **#2**) |
| **`Identify` / `AiOf` agent-app heuristics** | 2489–2531, 2544–2553 | Replace | ~40 net | Naming for thing lines, not AI card kinds |
| **`ModelStore` + `AddModels` + per-model AI rows** | 576–836, 2589–2611, 2651–2702, 2727–2748 | Replace | ~200 net | Loaded models on thing lines; unloaded → `"N models on disk · X GB"` (**#4**) |
| **`TaskList`** | 838–928 | Keep | 0 | Scheduled inline on thing lines (**#3**) |
| **`StartupList`** | 930–1058 | Delete | ~128 | **#5** |
| **`BotStatus` + configured http/file checks** | 1178–1261 | Delete | ~85 | Quiet/failed from CPU/IO + task last result (**#3**) |
| **`AppIdentity` + icons** | 293–331, 1584–1591 | Replace | ~30 net | Optional: drop icons on list; keep path resolution for clicks |
| **`GpuCounters` / `Nvml` / VRAM per PID** | 113–157, 1060–1176, 2997–3023 | Keep | 0 | Roll up VRAM per thing (**#4**) |
| **`Privacy` / `ErrorLog`** | 3348–3397 | Keep | 0 | All surfaced strings + `--json` |
| **`WriteSnapshot` text sections** | 3234–3290 | Replace | ~30 net | New section names: strip + project groups; `[ok]`/`[QUIET]`/`[DOWN]` mapping to v2 states |
| **`WritePrivacyProof`** | 3292–3298 | Replace | ~10 | Row list from unified model |
| **Snapshot PNG full window** | 3261–3272 | Keep | 0 | Still `--snapshot` |
| **`Card()` glass chrome** | 2067–2099 | Replace | ~20 net | Lighter list rows; strip at top |

### 1.4 README.md & install.ps1 (Phase 3 doc deletes)

| Content | Lines | Action | v2 |
|---------|-------|--------|-----|
| Eight-section “What it shows” | 21–33 | Delete/replace | One list + strip |
| First run / review | 62–64 | Delete | Hide/Pin |
| Settings / `watch.txt` / Pinned / Bots / AI kinds / temperature | 75–85 | Delete | Zero config + gear |
| Snapshot `--review` | 93 | Delete | `--json` note |
| `install.ps1` description “bots” | 13 | Replace | “what you started” wording |

---

## 2. Test suite (#3): assertions tied to deleted UI

Tests must stay green by **extending fixtures**, not a second harness. Below: assertions that assume v0.1.0 sections or `watch.txt` UX.

### `tests/smoke.ps1`

| Check | File:line | Current assertion | Replacement assertion (v2) |
|-------|-----------|-------------------|------------------------------|
| **snapshot** (review artifacts) | 373–384 | Runs `--review` snapshot; saves `review.png` / `review.txt` | Drop `--review`; snapshot still writes `vitals.txt` + `vitals.png`; optional smoke that PNG is non-empty |
| **scripts-listed** | 418–433 | `vitals.txt` has section header line `Scripts` and script filenames indented under it | `vitals.txt` contains each fixture script name (e.g. `dv_smoke_ps.ps1`) under a project heading or flat thing list; no `Scripts` section header |
| **http-local-only** | 477, 509–510 | `Set-ConfigText` with `check: http://example.com`; expect text `check must be a local address` | Remove watch-based setup; if v2 retains loopback guard for port probes, assert no outbound TCP to example.com during snapshot (keep monitor); drop message string check or retarget to internal validation flag in `vitals.txt`/`--json` |
| **huge-watch-txt** | 526–535 | 1 MB+ `watch.txt` → `settings file too large` in snapshot | Delete check or replace: oversized **prefs** file ignored / defaults used |
| **watch-txt-intact** | 546–576 | `--accept-review` leaves `watch.txt` bytes unchanged; `review.txt` has `@reviewed` | Delete; add **migration-once** test: sample `watch.txt` with `@hide` + pin line produces prefs file with equivalent hide/pin (no `watch.txt` reread) |
| **http-ipv6-loopback** | 607–611 | `watch.txt` bot `SmokeV6`; reject `check must be a local address`; row `SmokeV6` in text | Start fixture process; expect thing line matching label or script marker; IPv6 localhost port reachable from rollup click path (no config file) |
| **long-command-line** | 627–632 | `LongCmd = marker` in watch.txt; snapshot contains `LongCmd` | Pin migrated or auto-detected: snapshot contains marker or pinned display name |
| **models-symlink-loop** | 698–700 | `Set-ConfigText "@models = …"` then snapshot completes quickly | Place junction under `%USERPROFILE%\models\` or fixture tree; snapshot completes without hang (no `@models`) |
| **privacy-secrets** | 746–749 | `PrivSmoke = marker` in watch.txt; row in snapshot | Process with marker in argv appears as thing line; secrets still absent from text/png/log |

Unchanged smoke checks (still valid): build gate, `Process.Start` count, injection, default snapshot path, `errors.log`, `locale-perflib`, secret redaction blobs.

### `tests/accuracy.ps1`

| Check | File:line | Current assertion | Replacement assertion (v2) |
|-------|-----------|-------------------|------------------------------|
| **scripts-cards** | 333–355 | `Get-Section $text 'Scripts'`; fixture script names inside | Assert fixture names appear in `vitals.txt` (any section/group); `Assert-NoNoise` on that region |
| **scheduled-cards** | 357–367 | `Get-Section 'Scheduled'`; task names; `running` / `failed` | Task names on thing lines with failed/running state text (**#3**) |
| **starts-cards** | 369–385 | `Get-Section 'Starts with Windows'`; shortcut present; disabled absent | **Delete** starts expectations; assert disabled startup name **not** anywhere in `vitals.txt` |
| **ai-model** | 387–404 | `Get-Section 'AI'` contains fake model stem | Model stem or rollup line (`dv-acc-fake-…` or `"… models on disk"`) in output; not under `AI` header |

Helper `Get-Section` (112–128) can remain for strip parsing or be replaced with “contains needle” on full text.

### `tests/stability.ps1`

| Check | File:line | Current assertion | Replacement assertion (v2) |
|-------|-----------|-------------------|------------------------------|
| **binary-watch-txt** | 172–187 | Corrupt `watch.txt`; snapshot OK | Corrupt **prefs** file; snapshot OK |
| **locked-watch-txt** | 190–207 | Locked `watch.txt` with `Pinned = notepad.exe` | Locked prefs; snapshot OK |
| **soak prep** | 241–243 | Writes `review.txt` `@reviewed` to skip review | Remove; default UI has no review gate |

### `tests/safety-runtime.ps1`

| Area | Notes |
|------|--------|
| watch.txt backup/restore | Retarget to prefs file under `%APPDATA%\DesktopVitals\` |
| Allowed write roots | Add new state filename; drop requirement for `watch.txt` |

### `tests/locale-perflib.ps1`

No v0.1.0 UI section dependencies — **keep** as-is.

---

## 3. One-time `watch.txt` → Hide / Pin migration

**When:** First launch after upgrade if `%APPDATA%\DesktopVitals\watch.txt` or `review.txt` exists and prefs have no `migratedFromWatch` flag.

| Source | Maps to | Notes |
|--------|---------|--------|
| `Label = match` (not bot, not `\| ai`) | **Pin** `{ label, match, byExe }` | Former Pinned / Watching rules |
| `Label = … \| check: …` (bot) | **Pin** optional if user still wants quick find; no `check` stored | Behavior from runtime (**#3**) |
| `\| ai` / `\| kind:` lines | **Pin** if standalone match; else ignore kind | Display name = `Label` |
| `@hide = name` (watch or review) | **Hide** set | Same names as v0.1.0 board labels |
| `@kind = …` | *Dropped* | No kind tags in v2 |
| `@models`, `@temperature-file`, `@reviewed` | *Dropped* | Scan defaults / no CPU temp file |
| `review.txt` only hides | **Hide** set | Merge with watch hides |

**Not migrated:** `| check:`, `| code:`, file paths in checks (user re-opens via click), AI kind overrides, extra model roots.

**After success:** Write prefs + `migratedFromWatch=1`; rename `watch.txt` → `watch.txt.migrated` and `review.txt` → `review.txt.migrated` (do not delete user data).

**User message (one line, shown once):**  
`Imported hides and pins from your old watch.txt — change them anytime with right-click.`

---

## 4. v2 `DesktopVitals.cs` line-count estimate

| | Lines |
|--|------|
| v0.1.0 `DesktopVitals.cs` | 3435 |
| Estimated deletions (cards, review, watch/review config, startup list, top apps, thread strip, kind UI, bot check machinery, temperature file, CLI review) | **−1500 to −1750** |
| Estimated additions (thing rollup, project grouping, states, strip UI, hide/pin prefs, migration, port open, `--json`, outsider line, snapshot format) | **+850 to +1150** |
| **Projected v2 total** | **~2550–2900** (midpoint **~2700**, ~21% smaller than v0.1.0) |

Supporting file deltas (Phase 3): delete `watch.example.txt` (−44); README net −40 to −60; tests net +80 to +120 (new parsers, migration fixture).

---

## 5. Phase 3 delete-first order (suggested)

1. Review UI + `--review` / `--accept-review` + `@reviewed` gate.  
2. `watch.txt` / `review.txt` runtime (keep migration reader temporarily).  
3. Cards: Top apps → Starts → Watching/Bot/AI/Script shells → CardFlow.  
4. Thread strip + `CoreLayout`.  
5. Kind tags + `Theme.Kinds` card headers.  
6. `StartupList`, configured `BotStatus` checks, `@temperature-file`.  
7. README / `watch.example.txt` / test assertion updates.

---

## 6. Open questions

1. **Prefs format:** single JSON file vs `settings.json` + legacy rename — coordinator choice for C# 5 without NuGet.  
2. **Bot lines with only `check:` and no running process:** migrate to pin or discard?  
3. **`--json` schema:** mirror snapshot text groups or strict tree by project?  
4. **Accuracy `starts-cards`:** delete check entirely vs assert absence of disabled startup name in full output (recommended).  
5. **GPU CPU temp in strip:** NVML only on NVIDIA — confirm AMD/Intel strip omits GPU temp (v0.1.0 behavior).

---

*Inventory only — no code changes in Phase 1. Agent: DELETION.*
