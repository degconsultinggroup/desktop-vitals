# v2 grouping — reuse from v0.1.0

Skim of `src/DesktopVitals.cs` for the four facts. Nothing here is wired into the app yet.

## Process snapshot (`Procs.Snapshot`)

One `NtQuerySystemInformation` class 5 (`SystemProcessInformation`) per pass, buffer grown on `STATUS_INFO_LENGTH_MISMATCH`. x64 `SYSTEM_PROCESS_INFORMATION` fields already read: image name (56/64), pid (80), create time (32), user+kernel time (40+48), private working set (8), private page count (200). Parent pid sits at 88 (`InheritedFromUniqueProcessId`) and session id at 100; the current snapshot does not read them. Both are required for provenance and for hiding session 0. Create time is already there, so a parent can be rejected when its create time is not strictly earlier than the child's (PID reuse).

## PEB cwd and command line (`Procs.CurrentDirectory`, `Procs.CommandLine`)

Command line uses `NtQueryInformationProcess` class 60 with `PROCESS_QUERY_LIMITED_INFORMATION` only. Cwd walks the PEB (`ProcessBasicInformation` → `ProcessParameters` + 0x38) and needs `PROCESS_VM_READ` as well; failure returns null and is display-only. `ImagePath` is `QueryFullProcessImageName` on the same limited handle. Elevated and protected processes fail the open; the prototype should treat that as "running as admin" and stop.

## VRAM per pid (`ReadVram`)

Dedicated GPU memory per process from the `GPU Process Memory` / `Local Usage` counter, instance names `pid_<n>_…`, summed. English counter names are translated through Perflib 009 vs `CurrentLanguage` (`GpuCounters.PerfName`). Read off the UI thread, at most every 5 s. No NVML per-pid path; NVML is machine-level only.

## Task Scheduler reader (`TaskList.Refresh`)

`Schedule.Service` COM, off the UI thread, at most every 30 s, one reader at a time. Walks folders, skips `\Microsoft`, keeps enabled tasks whose action is a non-system program or a script. Also records state, last result, last/next run, and logon vs timed triggers. Reuse this for "started by a task that isn't Microsoft or a vendor," and for ended/failed when the process is gone. Do not create or change tasks. Startup-folder and Run-key reads live in `StartupList` and are the other half of fact 1.
