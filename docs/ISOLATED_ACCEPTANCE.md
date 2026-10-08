# Offline isolated acceptance

Run the separate acceptance bundle **on the OpticStudio computer**, not the AI-only computer.

1. Save normal optical work. Extract the bundle to a writable folder.
2. Double-click `Run-Isolated-Acceptance.cmd`. No installation, SDK, GitHub or Internet is required.
3. Return the generated `results-*` folder, including `summary.json`, per-phase `functional.json` reports and logs.

The script uses the Launcher preference's `zemaxRoot` and the current user's
`Documents\Zemax\Samples`. For nonstandard locations, run in Windows PowerShell:

```powershell
.\Run-IsolatedAcceptance.ps1 -ZemaxRoot 'C:\Program Files\Zemax OpticStudio' -SamplesRoot 'C:\Users\admin\Documents\Zemax\Samples' -AllowWorkerTermination
```

`-PlanOnly` validates files and prints the fixture plan without starting processes.
The four phases cover sequential editing/readback/snapshots, NSC Tasks,
tolerancing Tasks and optimization/cooperative cancellation/process restart.
The Samples themselves are not included in the bundle or modified.

Each phase starts a separate loopback-only Host on a free port using a temporary
runtime copy. It never edits installed settings, changes LAN sharing, opens a
firewall port or stops a name-wide set of processes. The checked test Worker can
be terminated in the final phase; other services are not selected. Cleanup first
closes the owned Host pipe to allow the Worker to dispose its optical session,
with a bounded fallback for that same checked test Worker only.

An additional Standalone instance needs an available API license. If the normal
service already uses the available license, startup may fail safely. Do not
stop normal work automatically; retain the diagnostic logs and discuss an idle
test window. Any unexpected leftover test optical window should be inspected
manually, not closed by a broad process-name kill.

The reports distinguish actual optical results from protocol simulations and
process crash recovery from non-cooperative COM grace-timeout recovery. A passing
bundle does not establish compatibility with other OpticStudio versions or
comprehensive correctness of all 135 tools. Publication remains gated on review
of the actual returned results and explicitly declared release support scope.
