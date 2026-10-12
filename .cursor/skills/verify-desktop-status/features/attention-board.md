# Attention board

The attention board turns lines amber or red when a reading crosses a threshold in `THRESHOLDS`, and the heading names what needs a look.

## Sub-features

- `attention-heading` shows `Needs a look` in red.
- `attention-heat` shows a hot processor, a stopped fan, and a hot graphics card.
- `attention-disk-events` shows an unhealthy disk and unexpected restarts in 30 days.
- `attention-quiet` still does not call live hardware.

## How to get to it (user POV)

- Run `python3 desktop_status.py --fixture fixtures/attention --no-set`.
- Or run `python3 verify.py`.

## Driving it with verify.py

Preconditions:

- `python3 verify.py --doctor` prints `DOCTOR OK`.
- `DEG_RUNTIME_DIR` is a different folder from the healthy run.

- **Draw the board.** Run `DEG_RUNTIME_DIR="$PWD/.cursor/skills/verify-desktop-status/artifacts/attention-board" python3 desktop_status.py --fixture fixtures/attention --no-set`. Exit code is 0. Stdout contains `Needs a look` and `Wallpaper not changed (fixture)`.
- **Read the page.** The heading contains `Needs a look` and color `#f87171`. The page contains `Too hot, 92`, `CPU fan reads 0 rpm while the processor is warm`, `2 unexpected restarts`, `Unhealthy`, and `voltage high`.
- **Confirm the quiet run.** `commands.json` is `[]`.
- **Proof.** Keep `status.html` and `commands.json`.

## Gotchas

- The heading lists only the first three red labels. Judge the rows, not only the heading.
- A hot processor stays red when voltage is also high. The line still says `voltage high`.
- Threshold numbers live in `THRESHOLDS`. If you change one, update `verify.py` in the same change.
