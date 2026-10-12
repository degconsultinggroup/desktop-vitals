# Disk events

Disk errors are events from the disk provider at level 2 (error) or level 3 (warning), with ids 7, 51, 153, and 157. Kernel-Boot event 153 is information about VBS and stays out of the count.

## Sub-features

- `disk-provider` limits the query to `Provider[@Name='disk']`.
- `disk-level` keeps level 2 and level 3 and drops information events.
- `disk-ids` still counts bad block (7), paging error (51), reset (153), and surprise removal (157).
- `disk-kernel-boot` leaves Microsoft-Windows-Kernel-Boot 153 out, including a level 2 event from that provider.

## How to get to it (user POV)

- The stability section of the detail page shows a disk error count for the last 30 days.
- A machine with VBS enabled writes Kernel-Boot 153 as information. That line must stay at zero disk errors when no real disk event is present.

## Driving it with verify.py

Preconditions:

- `python3 verify.py --doctor` prints `DOCTOR OK`.

- **Run the suite.** `python3 verify.py` includes the step `disk events stay on the disk provider`.
- **See the query.** `xpath_disk()` contains `Provider[@Name='disk']`, `Level=2`, and `Level=3`.
- **Kernel-Boot stays out.** Provider `Microsoft-Windows-Kernel-Boot`, event 153, level 4 does not match.
- **A real disk error stays in.** Provider `disk`, event 153, level 2 matches. Event 11 does not.

## Gotchas

- Level 4 is information. A disk-provider 153 at level 4 is not a disk error.
- The fixture host still returns the `disk` count when the query contains `EventID=7`. The new filter keeps that id.
