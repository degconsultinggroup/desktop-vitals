# Desktop overlay

One process draws a frosted panel on the empty desktop. ON shows the readings. OFF leaves the pill and stops collecting. An unchanged board does not redraw.

## Sub-features

- `overlay-glass` is one rounded panel. The area outside that shape is transparent, and the top of the glass is lighter than the bottom.
- `overlay-pill` accepts a click on ON or OFF and ignores a click on the rest of the panel.
- `overlay-skip` reuses the cached glass and does not write a new bitmap when the lines are unchanged.
- `overlay-off` draws only the pill. `overlay-on` draws the rows again.

## How to get to it (user POV)

- On Windows, the scheduled task runs `run-desktop-status.vbs`, which starts `desktop_status.py --overlay`.
- Click ON or OFF on the pill. The choice is saved in `runtime\monitor.txt`.
- Open the desktop shortcut for the detail page. Each row names its source there.

## Driving it with verify.py

Preconditions:

- Pillow and psutil are installed.
- You do not need a Windows window. The checks call the draw and hit-test functions, then one fixture process.

- **Glass.** Build a panel from a flat wallpaper. The corner pixel is transparent. A pixel near the top is lighter than a pixel near the bottom.
- **Pill.** A point in the left half returns ON. A point in the right half returns OFF. A point on the panel body returns nothing.
- **Redraw.** Publish one frame, publish the same lines again, then change a line. The second publish does not rewrite the bitmap. The third does. A second glass build with the same key does not run the builder again.
- **Process.** Run `python3 desktop_status.py --overlay --fixture fixtures/healthy` with `DEG_RENDER_SIZE=640x480`. Stdout contains `Overlay window skipped (fixture)` and does not set a wallpaper.
- **Proof.** `python3 verify.py` asserts this.

## Gotchas

- Linux and fixture mode never open a window. The real layered window can only be seen on the tower.
- The glass is a blurred crop of the wallpaper file. It is not a live screenshot and not a DWM acrylic window.
- OFF skips collection. The 2-minute task only starts the process if it has exited. It does not turn monitoring back on.
