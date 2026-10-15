# Phase 1 teardown: “What’s running that I started, and is it OK?”

Captured on a real Windows dev box (read-only) into untracked `scratch/`: `get-process*`, `tasklist /v`, `tasklist /svc`, `win32-process.csv`, `process-tree-sample.txt`, `net-tcp-listen.csv`, `top-by-*.txt`, `nvidia-smi-apps.csv`, `listen-user-ports.txt`, and v0.1.0 `scratch/v01/vitals.{txt,png}` after `build.ps1` + `--snapshot`.

**Inventory (user-started, this session):** 2× agent CLI, 1× IDE (+3 headless agent chains), 3× Python bots, 1× Node gateway bot, 1× speech-model script, 1× short-lived Node script, 2× scheduled tasks; plus startup noise (browser, sync, screenshot, RGB). **~312** processes total; **6** `python`, **11** `node`, **16** `Cursor`, **17** `chrome` rows in the flat list.

## Tools × questions

Legend: **Y** = reliably for most of the inventory without extra setup; **P** = partial / manual inference; **N** = not really; **—** = not applicable.

| Tool | What is it? | Which project? | Working vs just alive? | Port? | VRAM holder? | Effort (clicks / columns) |
|------|-------------|----------------|------------------------|-------|--------------|---------------------------|
| **Task Manager** (Processes + `Get-Process` / `tasklist`) | **P** — image name (`python.exe`×6, `node.exe`×11); command line only on Details with extra columns | **N** — no cwd; CLI hidden by default | **P** — CPU % / “Very high” power; no quiet/failed/ended; scheduled last result elsewhere | **P** — not on Processes; Performance → Resource Monitor or per-process Networking (~**5–7** clicks per PID) | **P** — GPU column is engine %, not MiB per owner; no “who loaded the model” | Default **~8** columns; **+2** tabs and column picks for CLI |
| **Process Explorer** (tree + `Win32_Process`) | **Y** — tree + full command line (e.g. agent CLIs, `cursor-agent` chains, bot `.venv` paths) | **P** — infer from path (`...\repo-a\...`) not grouped | **P** — CPU/IO columns; you decide “stuck” | **Y** — process **TCP/IP** tab (**~2** clicks from tree selection) | **N** — no VRAM per process without GPU tools | **~1** click to identity; **+1–2** for ports |
| **Resource Monitor** (`Get-NetTCPConnection -State Listen`) | **P** — PID → name join; many listeners are `svchost` / system | **N** | **N** — throughput only | **Y** — best native view of **listen** ports (**21** sockets here; two user dev ports on one `node` PID) | **N** | Open from TM (**~2** clicks) + Network tab filter |
| **btop / htop-style** (top-N by CPU/RAM) | **N** — flat hot rows; top CPU was `python` with no script name in the default 4-column table | **N** | **P** — CPU/RAM only | **N** | **N** | **0** extra if already in a terminal; **4** columns shown in capture |
| **Desktop Vitals v0.1.0** | **Y** — labels (“Agent: …”, bot names, script filenames) | **P** — bots/scripts by path fragment; agents by product, not repo folder | **P** — `[ok]` + “worked just now” / “just now” on bots; no quiet/failed for agents; scheduled **OK** + last run | **N** — no `:port` on snapshot lines; only script `--port` heuristics in code, not surfaced here | **P** — machine VRAM **11%** + one loaded model line; **11** “not loaded” disk models add noise; per-process VRAM not tied to agents | **0** at a glance but **8** sections / **~40** lines; same Python in **Bots**, **Scripts**, **Top apps** |

### Per–user-thing notes (same box, anonymized)

| Thing | Task Mgr | Proc Exp | ResMon | top-N | v0.1.0 |
|-------|----------|----------|--------|-------|--------|
| Agent CLI ×2 | 2× `claude.exe` | Sees flags under `cmd` tree | — | Buried below IDE | “2 sessions” under AI |
| IDE + agent chains | 16× `Cursor` | Tree shows `cursor-agent` → `powershell` | — | `Cursor` only | AI + Top apps duplicate |
| Python bots ×3 | 3–6× `python` | Script path in CLI | — | Generic `python` | Named in Bots; also Top apps “Python ×6” |
| Node gateway | `node` (bot path in Details) | CLI shows gateway | two ports listen | `node` | “Bot: <name>” in AI |
| Speech script | `pythonw` | Full path to the script | — | Often missing from top | Model + Scripts + Starts **triple** |
| Short-lived Node script | One of 11 `node` | CLI shows temp `index.js` | — | May appear briefly | Scripts section only |
| Scheduled ×2 | Not listed | — | — | — | Scheduled section; no tie to live PIDs |
| Startup clutter | Mixed with apps | Visible | — | — | Starts with Windows + Pinned Chrome |

## Five sharpest gaps

1. **Process names are not things** — six `python.exe` rows do not answer which bot, agent, or server is which without CLI archaeology.
2. **No project lens** — paths exist in Proc Exp / v0.1 paths, but nothing groups `repo-a` agents, scripts, and servers on one line.
3. **Alive ≠ OK** — low CPU does not distinguish quiet, failed, or ended; scheduled “OK” is separate from live processes.
4. **Ports are a second tool** — only Resource Monitor (or Proc Exp per process) ties localhost listeners to PIDs; v0.1 snapshot shows none.
5. **GPU story splits three ways** — TM GPU %, total VRAM %, disk-sized “not loaded” models, and no single “who holds VRAM for this agent/model” line (`nvidia-smi` here returned `[N/A]` / insufficient permissions for many PIDs).

## Proposed README / launch before–after (describe frames only)

**Before (v0.1.0 or Task Manager):** Full-height Desktop Vitals window or Task Manager Details. Machine stats at top. Body is **eight** section headers (AI, Bots, Scripts, Starts with Windows, Scheduled, Pinned, Top apps, …). AI lists **eleven** gray “not loaded” model rows. The same browser appears under **Starts with Windows** and **Pinned**. **Top apps** leads with Defender and **svchost ×84**. User must scroll and mentally dedupe to answer the question.

**After (v2 target):** Same machine **strip** only (CPU · RAM · GPU · VRAM · disk). One scrollable list grouped by **project folder** (e.g. `repo-a`, `repo-b`, **Other**). Each line is one rolled-up thing: name, state (`running` / `quiet` / `failed` / `ended`), CPU/RAM/VRAM, `localhost:18789` link, single click opens folder. Unloaded models collapsed to one line (“11 models on disk · 150 GB”). No startup/pinned/top-apps cards.

## Open questions

- Map listen PIDs to rolled-up “things” when the listener is a child `node` under a gateway tree.
- VRAM attribution without admin / when NVML per-process is `[N/A]`.
- Migrate `watch.txt` hides without reintroducing config surface area.
