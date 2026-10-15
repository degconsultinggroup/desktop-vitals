# v2 grouping

Prototype: `tools/v2-proto/proto.cs`, built by `tools/v2-proto/build-proto.ps1` with the in-box C# 5 compiler. One snapshot in, Things as JSON out. No UI. Raw host captures stayed in an untracked `scratch/` directory.

## What v0.1.0 already has

`Procs.Snapshot` is one `NtQuerySystemInformation` class 5. It already reads image name, pid, create time, CPU time, and private bytes. Parent pid (offset 88) and session id (offset 100) are the missing fields; both matched WMI on 296 of 296 comparable processes. Command line is `NtQueryInformationProcess` class 60 with `PROCESS_QUERY_LIMITED_INFORMATION`. Cwd is the PEB walk, with the 32-bit PEB when the process is WOW64. Per-pid VRAM is the `GPU Process Memory` / `Local Usage` counter (`pid_<n>_`), English names translated through Perflib. Task Scheduler is `Schedule.Service`, read-only, skipping `\Microsoft`.

## Method

Facts decide. A display-name map runs after that and is not read by the decision.

1. Provenance. Walk parents while the parent's create time is strictly earlier (PID reuse and a dead parent both stop the walk). Session 0, or an ancestor `services.exe`, is the services tree and is hidden. A user scheduled task or a Startup/Run entry of something that is not an installed app is yours even when the chain touches `svchost`. A terminal or editor ancestor is yours, except the editor's own binaries. Explorer is yours only for a shell or for an image that is not under Windows, Program Files, `%LocalAppData%\Programs`, or WindowsApps. Children of a yours process inherit that, so a task's `wscript` → `cmd` → `node` tree stays one thing.
2. Project. Nearest ancestor of the cwd, else the script, else the image, that contains `.git`, `package.json`, `pyproject.toml`, `Cargo.toml`, `go.mod`, or `*.sln`. A root that contains the process's own binary is the tool, not the work, unless the cwd is inside that root.
3. Behavior. A listen port (GetExtendedTcpTable, `TCP_TABLE_OWNER_PID_LISTENER`, IPv4 and IPv6) makes it a server. More than 1 GB of VRAM, or a mapped weight file, makes it a model. Older than 10 minutes and a file in the cwd written in the last 90 seconds makes it a bot. Older than 5 minutes, idle, no port, and no recent write is quiet.
4. Weights. For yours processes, `VirtualQueryEx` + `GetMappedFileName` (`psapi`) with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`. `.gguf` and `.safetensors` count at any size; `.bin` / `.pt` / `.pth` only over 20 MB. Shards (`-00001-of-00002`) merge. The name comes from the file. If nothing is mapped, `-m` / `--model` on the command line is the fallback. `python -m module` is not a model path.

Shells (`cmd`, PowerShell, `bash`) are not a second thing when they are only hosting a child in the same project. `conhost`, `OpenConsole`, and `nohup` are infrastructure. WSL and Docker, when present as user processes, collapse to one line each.

## Decision table

| Fact | Yours / what it is | Hidden or unnamed |
| --- | --- | --- |
| Parent chain reaches a terminal or editor, and the image is not that app's own binaries | Yours. Launcher is the outermost terminal or editor | |
| Parent chain reaches Explorer, image is not an installed app | Yours | Installed app from Explorer is not yours |
| Command line matches a user scheduled task, or Startup/Run points at a non-installed image | Yours. Live task tree inherits | `\Microsoft`, an installed updater, or a task whose name contains "update" and whose script is not in a project |
| Session 0 or `services.exe` above, and no user task | Hidden | A not-yours image over 25% CPU or 2 GB RAM is one line under Other |
| Cwd / script / exe has a project marker | Project is that folder | Marker that only contains the tool's binary is ignored |
| TCP listen | Kind server, ports listed | |
| Mapped weights, or VRAM > 1 GB | Kind model, name from the weight file | |
| Age > 10 min and a cwd write in the last 90 s | Kind bot | A younger writer stays a script; `why` still records the write |
| Age > 5 min, ~0 CPU, no port, no write | State quiet | |

## This box

Warm machine, three passes, fixtures running (HTTP on 8765, a writer, a small exe, a process mapping two weight shards). 303 processes, 113 session 0, 190 in the user session, 51 marked yours, 20 things. No pid appeared in two things.

| Pass | ms | Identity ms | Map ms | Map procs | Extra RAM |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 1191 | 359 | 87 | 51 | 42.3 MB |
| 2 | 1137 | 312 | 88 | 51 | 42.2 MB |
| 3 | 1166 | 337 | 89 | 51 | 42.3 MB |

Mean warm pass **1165 ms**. Peak working set **58 MB**, about **42 MB** above the process start (~16 MB). Snapshot itself is ~7 ms. The first pass after the tool had been idle was **5.2–5.4 s**, almost all of it the per-process command-line and cwd reads (identity ~4.5 s). Repeat passes drop that to ~0.3 s.

Mapped-file walk: **~88 ms for ~50 yours processes**, max **12.7 ms** for one process. The one yours process that held any VRAM accounted for **~3 ms**. The weight fixture held **0 VRAM** and was still found. Limiting the walk to VRAM holders would miss CPU-mapped weights. The walk is cheap enough to run for every yours process. The pass cost is opening every user-session process for its command line and cwd, not the map.

No llama.cpp-style server was running, and nothing held more than 1 GB of VRAM. Fact 4 was tested with a process that mapped `fixture-weights-00001-of-00002.gguf` and `fixture-weights-00002-of-00002.gguf` (8 MB each). The thing came back as kind `model`, models `["fixture-weights"]`, shards merged, name taken from the file.

A node one-liner on 8766, started in its own git repo on a second pass, was yours, project matched, kind server, port 8766. The `node` on this shell's PATH is the editor's bundled binary, so the display map renamed the line to the editor. The port and the project did not depend on that name.

## Scores

Ground truth is "the user started it or scheduled it," including the fixtures that were actually up. Scheduled supervise-jobs are one logical thing with the python they left running, not two.

| | Count |
| --- | ---: |
| True positive | 16 |
| False positive | 1 |
| False negative | 4 |
| Precision | 16/17 = 94% |
| Recall | 16/20 = 80% |

Right on the true positives: project folder, listen port, and merge. The writer was still under 10 minutes old, so kind stayed `script` and `why` included `wrote beat.txt`. That is the age rule, not a missed write. The gateway process listens on its main port and a second port; both are shown because both are real listens on that pid.

Zero duplicate pids. Cursor's own `Cursor.exe` processes (the IDE, launched from Explorer, image under the install dir) stayed out. Session 0 stayed out. Chrome and the terminal app stayed out.

## Misses

| What | Fact that failed |
| --- | --- |
| Three long-running python bots (`run.py` / `record.py` under three different checkouts). Parent pid is not in the snapshot. The matching supervise task is listed as `ended` with a future next run. | Provenance. The parent exited, so the chain stops, and the live command line does not contain the task script's file name, so the task match does not fire. Project would have been available from the cwd. |
| A packaged agent (`codex.exe`) under `ChatGPT.exe` → `sihost` → `svchost` → `services.exe`. | Provenance. The services-tree rule hides it. The user did start it; the parent chain is the shell host, not a Windows service. |
| The editor's setup helper, two processes, no project, quiet. | Provenance. An editor ancestor means yours, and this helper is not inside the editor install directory, so it is not treated as part of the app. Noise. |
| Node one-liner named as the editor. | Display map, applied last. The binary path contains `cursor-agent`. Kind, port, and project were already set. |

WSL: only `wslservice.exe` on the services tree, no user `wsl.exe`, so no WSL line. Docker: no processes. No elevated yours process; `dwm` and similar returned access denied and stayed hidden.

## ASCII mock

Placeholders. Costs rolled up; state is the one word on the right.

```
CPU 12%   RAM 41%   GPU 4%   VRAM 2.1 / 12 GB   disk 61%

repo-a
  Cursor                  from cmd          1%   220 MB          running
  Claude Code             from cmd          0%   340 MB          quiet

repo-b
  Cursor                  from cmd          0%   150 MB          quiet

repo-c
  Claude Code             from cmd          0%    40 MB          quiet

repo-http
  http.server       :8765 from cmd          0%    18 MB          running

repo-loop
  beat.py                 from cmd          0%    12 MB          running

repo-exe
  fixture-exe             from cmd          0%     4 MB          running

repo-map
  fixture-map · fixture-weights
                          from cmd          0%    30 MB          running

app-gateway
  gateway           :18789 from Scheduled task
                                            0%   180 MB          running

Other
  script-startup          from Startup      0%     8 MB          quiet
  app-rgb                 from Scheduled task
                                            0%     2 MB          quiet
  Desktop Vitals          from Explorer     0%    87 MB          quiet
  job-daily-a             from Scheduled task
                          next tomorrow     —                    ended
  job-daily-b             from Scheduled task
                          next tomorrow     —                    ended
  job-sensor              from Scheduled task
                                            —                    ended
```

Not on this list, and they should be: the three bots whose parents have exited, and the packaged agent behind the shell host. The setup helper should not be on the list.

## Open risks

- Elevated processes. `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` fails with access denied. The prototype would show "running as admin" and the image name only. None of the yours processes on this box were elevated, so that path was not observed live.
- WSL. A running distro was not available. The service process alone stays hidden. A user `wsl.exe` should collapse to one line; that was not exercised.
- Docker. No Docker processes were running. The same one-line collapse is implemented and untested here.
- PID reuse. The parent is ignored when its create time is not strictly earlier. This run's misses were dead parents (pid absent), not reused pids. A reused pid whose new process is older than the child would still be wrong; that case was not seen.
- Dead parents. Orphaned bots become invisible, and their scheduled task looks finished. The live cwd still has the project. A later version can attach an orphan to a user task whose action directory is the cwd, or remember the last parent chain.
- `sihost` / packaged apps. Treating every `svchost` ancestor as the services tree hides agents the user launched from a Store or packaged host. The cut should be "session 0 or `services.exe` is the parent," not "any ancestor is session 0," once the process is in the user session and its host is `sihost` rather than a service.
- Cold identity cost. ~5 s the first time, ~1.2 s warm, at ~300 processes. The product budget is well under that if it runs every tick. Parent and session come from the one system snapshot and do not need `OpenProcess`; command line and cwd can be limited to processes the snapshot already calls candidates.
- Display map vs bundled runtimes. A path segment of a known agent renames a thing that merely uses that agent's `node.exe`. Keep the map on the script or argv0, not on every directory in the image path.
