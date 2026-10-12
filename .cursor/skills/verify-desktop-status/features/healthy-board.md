# Healthy board

The healthy board tells you the PC is fine: green dots, plain-English lines, and a detail page you can open from the desktop shortcut.

## Sub-features

- `healthy-heading` shows `All good` in green.
- `healthy-vitals` shows processor, graphics, fans, memory, disk, and 30-day stability.
- `healthy-programs` shows DEG tasks, the scorecard, the practice trader, and the assistants.
- `healthy-quiet` does not launch hardware tools and does not change the wallpaper.

## How to get to it (user POV)

- Run `python3 desktop_status.py --fixture fixtures/healthy --no-set`.
- Or run `python3 verify.py`, which drives this fixture with the others.
- On the tower, the scheduled task runs the same program without `--fixture`. That live path is not this feature.

## Driving it with verify.py

Preconditions:

- `python3 verify.py --doctor` prints `DOCTOR OK`.
- `DEG_RUNTIME_DIR` is an empty folder you created for this run.

- **Draw the board.** Run `DEG_RUNTIME_DIR="$PWD/.cursor/skills/verify-desktop-status/artifacts/healthy-board" python3 desktop_status.py --fixture fixtures/healthy --no-set`. Exit code is 0. Stdout contains `Board: All good` and `Wallpaper not changed (fixture)`.
- **Read the page.** Open `status.html` in that folder. The heading is `All good` with color `#4ade80`. The page contains `No unexpected restarts in 30 days`, `CPU fan 840 rpm, Case fan 610 rpm`, `5600 MT/s, 1.10 V`, `All disks healthy`, and `0.875 V`.
- **Read the picture.** `render/wallpaper-a.png` or `render/wallpaper-b.png` is 3440 by 1440.
- **Confirm the quiet run.** `commands.json` is `[]`.
- **Proof.** Leave `status.html`, the png, `commands.json`, and stdout in the proof folder.

## Gotchas

- `All good on the readings we have` is a different heading. It means something was grey. This feature expects `All good`.
- A `Not available:` line means the fixture was not the healthy one.
- Fixture health times are local wall times. Live health files on the tower are UTC. Do not mix them.
- `--no-set` and `--fixture` both refuse to change the wallpaper. The stdout line is the check.
