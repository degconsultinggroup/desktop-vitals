# Missing readings

When a source has nothing to say, that line is grey and says `Not available:` plus a short reason. The rest of the board still draws.

## Sub-features

- `missing-fans` explains that no fan speed was found.
- `missing-disk` explains that Windows did not return disk health.
- `missing-events` explains that the event log could not be read.
- `missing-voltage` keeps graphics and memory speed, and says voltage is not available.
- `missing-heading` stays green because a gap is not a fault.

## How to get to it (user POV)

- Run `python3 desktop_status.py --fixture fixtures/missing --no-set`.
- Or run `python3 verify.py`.

## Driving it with verify.py

Preconditions:

- `python3 verify.py --doctor` prints `DOCTOR OK`.
- `DEG_RUNTIME_DIR` is a fresh folder.

- **Draw the board.** Run `DEG_RUNTIME_DIR="$PWD/.cursor/skills/verify-desktop-status/artifacts/missing-board" python3 desktop_status.py --fixture fixtures/missing --no-set`. Exit code is 0.
- **Read the page.** The heading is `All good on the readings we have`. The page contains `Not available: no fan speed in the CPU sensor file or Windows fan list`, `Not available: Windows did not return disk health counters`, `Not available: the Windows event log could not be read`, `Voltage not available: nvidia-smi did not report GPU voltage`, and `Voltage not available: Windows did not report memory voltage`. Grey rows use `#949caa`.
- **Confirm it did not crash.** `commands.json` is `[]` and stdout contains `Wallpaper not changed (fixture)`.
- **Proof.** Keep `status.html`.

## Gotchas

- Grey is not red. `Needs a look` on this fixture means a missing reading was mis-classified.
- A Windows fan report of 0 rpm is treated as "no reading", not as a stopped fan. A 0 from the sensor file is a real reading.
- GPU voltage stays on the graphics detail line. The graphics dot still follows temperature.
