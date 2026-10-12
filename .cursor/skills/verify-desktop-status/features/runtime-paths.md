# Runtime paths

The board finds its script folder from its own path. State, pictures, and the detail page go in a gitignored `runtime` folder. The launcher finds `.venv` the same way.

## Sub-features

- `paths-runtime` writes `status.html`, `state.json`, and `render\wallpaper-*.png` under the runtime folder.
- `paths-not-beside-script` does not leave those files next to `desktop_status.py`.
- `paths-launcher` starts `pythonw.exe` from the `.venv` next to the vbs.
- `paths-home` does not bake a user profile into the script, the vbs, or the register script.

## How to get to it (user POV)

- Double-click or schedule `run-desktop-status.vbs`. It resolves the folder from `WScript.ScriptFullName`.
- Or run `python3 desktop_status.py --fixture fixtures/healthy --no-set` and look in `runtime\`.
- Set `DEG_RUNTIME_DIR` when you want the files somewhere else.

## Driving it with verify.py

Preconditions:

- `python3 verify.py --doctor` prints `DOCTOR OK`.
- No `status.html` is sitting next to `desktop_status.py` from an older run. If one is, move it aside first so the check is real.

- **Draw into a chosen folder.** Run `DEG_RUNTIME_DIR="$PWD/.cursor/skills/verify-desktop-status/artifacts/runtime-paths" python3 desktop_status.py --fixture fixtures/healthy --no-set`.
- **See the files.** That folder contains `status.html`, `state.json`, `latest-render.png`, and `render/wallpaper-a.png` or `wallpaper-b.png`.
- **See the script folder stay clean.** `status.html`, `latest-render.png`, and `state.json` are absent next to `desktop_status.py`.
- **Read the launcher.** `run-desktop-status.vbs` contains `WScript.ScriptFullName` and `.venv`, and does not contain a user profile path.
- **Proof.** `python3 verify.py` checks the same rules. Keep the proof folder.

## Gotchas

- `DEG_RUNTIME_DIR`, `DEG_DESK_ROOT`, `DEG_EDGE_PAPER`, and `DEG_SENSOR_FILE` override locations for a run. Unset them before you judge the defaults.
- The sensor default is still under `ProgramData\DEG\cpu-sensor\out\last.json`. That is the machine data file, not a home directory.
- Desk and paper files default to the current user's `Documents` folder. The logged-on user supplies that path.
- `verify.py --cleanup` deletes `<repo>/runtime` only. A custom `DEG_RUNTIME_DIR` is left alone.
