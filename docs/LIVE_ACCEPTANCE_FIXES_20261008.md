# Licensed acceptance fixes (candidate, not yet accepted)

Baseline: `agent/official-tasks-adapter-20261008`, `6b11d18`.

The licensed OpticStudio 2024 R1.03 Chinese installation reproduced failures on the official Cooke and tolerancing samples. Default system/surface reading rejected infinity; FFT MTF report parsing matched English headings; tolerancing reported success without the expected ZTD. The diode scene also reported zero detectors despite containing a rectangle detector.

## Changes

- Surface thickness/semi-diameter use JSON null plus `thicknessState`/`semiDiameterState` (`Finite`, `PositiveInfinity`, `NegativeInfinity`). Finite values are unchanged. NaN remains an error. This intentionally changes infinite optical dimensions from a thrown exception to explicit structured data; clients must not coerce null to zero. General analysis non-finite validation stays strict.
- FFT MTF uses official DataSeries vectors/matrices, not localized text parsing. A field-only calculation validates the expected field count; a second calculation adds the diffraction curve and identifies it by a unique added series description rather than English keywords or a guessed position. Ambiguous/invalid data still fails explicitly. Client cancellation is rethrown.
- Tolerancing validates the accepted save-data settings and accepts only its unique generated filename beside the lens when an installed API resolves the output relative to the lens. Missing-output diagnostics include accepted settings, status and result report filename. This is compatibility hardening, not proof of the missing-ZTD cause or a confirmed real-machine fix. `UseDataRetention` is obsolete/no-op in the supplied API and is deliberately not used as a purported fix.
- NSC detector classification retains the official flag and explicitly recognizes built-in detector enums as a version-compatible fallback, independent of localized TypeName text.
- Optional ZOS safety verifier now reflects `RequiresSnapshot` instead of the removed `IsMutating` method.

## Acceptance still required

Deploy the candidate to both machines. Repeat the official-sample sweep and default system read. Confirm one detector in the diode scene and successful FFT MTF plus ZTD-backed sensitivity/Monte Carlo output. Enable official Tasks explicitly for its separate long-job completion/cancellation/recovery acceptance. Do not merge/publish until these results and exact-commit CI/signing gates pass.

The candidate retains embedded version 1.4.2 and is not a signed update release. Local pure regressions cover infinity/sign/NaN and localized MTF labels, matrix shape and non-finite data; they do not exercise COM or prove real numerical output.

Sources: [official FFT MTF DataSeries example](https://developer.synopsys.com/docs/zos-api-interface-2025-r2-sp02/_c_sharp_standalone_04_pull_data_from__f_f_t_m_t_f_8cs-example.xhtml), [2024 tolerancing interface](https://developer.synopsys.com/docs/zos-api-interface-2024-r1/interface_z_o_s_a_p_i_1_1_tools_1_1_tolerancing_1_1_i_tolerancing.xhtml).

## RC3 follow-up

RC2 passed detailed detector inspection (50×50, 2500 pixels), but ZTD output and its temporary text report were still absent. The [official Zemax staff explanation](https://community.zemax.com/got-a-question-7/how-to-save-all-tolerancing-data-montecarlo-sensitivity-and-summary-in-zosapi-5125?postid=18798) explicitly requires filename-only TolDataFile/OutputFile; both outputs are always beside the saved lens. The earlier full-path assignment was incorrect, even though the API accepted its readback and reported Succeeded.

RC3 assigns unique filenames without directories, requires a saved lens, reads the explicit report before closing, moves the unique ZTD to staging for the viewer, and cleans up only the generated ZTD/TXT files. This code correction still requires deployed licensed acceptance; it is not yet a successful tolerancing result.

The user-supplied optical illustration is converted into a 32-bit multi-frame ICO (16/20/24/32/40/48/64/96/128/256 pixels), preserving source aspect ratio. Launcher window/executable/tray and Installer share the same existing icon resource. Original source PNG is unchanged; the reproducible conversion script and source asset are retained.

## RC3 licensed results and RC4 candidate

The official-sample sweep on deployed RC3 completed 22 calls: 21 passed. Default surface/system reads, Chinese FFT MTF, ray diagnostics, tolerance editor reads and the NSC ray trace passed. The original lens was reopened. Tolerancing now produces a non-empty uniquely named ZTD, but its viewer fails at startup; structured tolerance results are **not accepted** yet.

RC4 keeps the generated ZTD beside its lens throughout viewer use. The synchronous viewer uses the official `RunAndWaitForCompletion` entry point; asynchronous viewers retain bounded polling. Failure diagnostics include actual viewer filename, byte count, validity, execution mode, status and API error instead of the former generic startup message. Synchronous COM cannot be cooperatively time-bounded here; Host generation hard recovery remains its safety boundary. This is a candidate correction requiring deployed retesting, not proof of successful tolerance parsing.

The icon's dark background was extracted with the built-in imagegen tool (prompt: remove only the dark vignette; preserve the lens, colored rays, focal point and translucent glow). `ApplicationIconTransparent.png` retains real alpha; the original `ApplicationIconSource.png` is unchanged. The ICO converter now clears to transparent and preserves source alpha, with transparent-corner checks for all ten frames. Launcher, tray and Installer consume the shared replacement ICO.

Reference: [official Tolerance Data Viewer interface](https://developer.synopsys.com/docs/zos-api-interface-2025-r2-sp04/interface_z_o_s_a_p_i_1_1_tools_1_1_tolerancing_1_1_i_tolerance_data_viewer.xhtml), [Zemax staff viewer usage example](https://community.zemax.com/got-a-question-7/how-to-save-all-tolerancing-data-montecarlo-sensitivity-and-summary-in-zosapi-5125?sort=oldestFirst).

## RC4 acceptance / RC5 usability

Deployed RC4 passed 37 recorded calls (setup, repeated polling and restoration included). Tolerance ZTD reading succeeds: 3×43 Monte Carlo matrix, 41 sensitivity operands, ranked worst effects and explicit yield threshold consistency. Chinese MTF and detailed 50×50 NSC detector reads pass. Background NSC Job completes with successful actual result and 100% progress. Original lens was restored; official Samples were not overwritten. This is not comprehensive acceptance of every tool or of official Tasks.

RC5 adds an English official Tasks checkbox to Run configuration and enables it by default in Launcher and command-line Host, as requested. Missing settings adopt ON; explicitly saved OFF is respected. Changes persist and apply on the next Stop / Start, never automatically interrupting optical work. Compatible clients must still opt in. Licensed official Tasks cancellation/recovery acceptance remains pending.

The built-in imagegen edit uses the transparent icon as its target: square tight framing, complete lens and focal point, shorter incident beam ends, minimal margins, unchanged palette and true alpha. Final source is `ApplicationIconCompact.png`; earlier sources remain intact. All ten ICO sizes are rebuilt from this compact asset so the lens occupies substantially more of the available icon height.

## RC5 acceptance / RC6 installation fixes

RC5 completed licensed official Tasks NSC and tolerancing runs, actual result retrieval, empty Task update, late-cancel terminal immutability and cooperative Global Search cancellation with unchanged Worker generation. Its 22 ordinary-client sample calls also passed. Non-cooperative hard recovery and forced licensed process-loss acceptance are still separate pending gates.

RC6 freezes terminal Job duration at CompletedAt. Simulation regressions assert cancelled/completed durations remain fixed on later polls.

The updater previously deleted every root file, including launcher-settings.json. RC6 shares an explicit runtime-data policy between GUI Installer and Updater: preserve settings/backup, clients.json, update.log and the updater lock; preserve logs/snapshots/shortcut-icons directories. Package runtime files are never copied over local ones. Obsolete product files are still removed. Launcher load-time events cannot save partial defaults; writes are atomic with a backup, and unreadable settings are preserved instead of silently replaced. This cannot recover preferences already deleted by earlier RC upgrades.

Updater shuts down only known executables whose verified absolute path is within the selected installation, then waits for exit. File copy/delete retries transient IO/access failures for up to five seconds per operation; permanent ACL/policy denials still fail and are logged, not bypassed. A per-install file lock prevents concurrent updaters. Backup recovery paths are logged before replacement. GUI installation is asynchronous with buttons/close disabled during installation; it does not kill an updater halfway through a transaction on timeout. Portable installation now checks the live errorlevel rather than an expanded value captured before execution.

GUI desktop shortcuts use an immutable content-hashed ICO path and notify Windows of the changed shortcut/icon association. Earlier icon paths remain for existing shortcuts. The package contains the current standalone ZemaxMCP.ico as well as embedded Launcher/Installer resources. Explorer/pinned taskbar cache behavior still requires deployed UI confirmation.

Local tests exercise upgrade and rollback runtime preservation despite contaminated staging, transient lock release, atomic preference/backup recovery, initial GUI copy preservation and hash-changing shortcut icon paths without installing over the user's live copy or altering the desktop shortcut.

## RC6 licensed acceptance / RC7 presentation

Deployed RC6 passed the official Tasks sample suite: NSC completion with actual result, empty update, late cancellation immutability, tolerancing (3×43 Monte Carlo matrix, 41 sensitivity operands), and cooperative Global Search cancellation. Worker generation remained unchanged and subsequent calls succeeded. Ordinary-client sequential, Chinese MTF, tolerance and NSC sample calls also passed. The original `LENS.zos` was reopened. Terminal Job elapsed durations stayed fixed across repeated polling (NSC 1.1 s, tolerancing 0.4 s, cancelled search 2.1 s in this run).

This does not prove every registered tool, non-cooperative hard recovery, or actual desktop installer/cache behavior. The latter still needs user confirmation after a GUI upgrade.

RC7 is a presentation-only follow-up: default dashboard size 960×740, slimmer rounded scrollbars with a 12-DIP drag target, and owned card-style confirmations for detected AI client setup and token replacement. Setup lists detected client names and configuration states instead of a comma-separated system MessageBox. No redundant first-run prompt is shown when all eligible clients are already configured. Existing configuration backup and update-preservation fixes remain intact.

The offline layout test strips event handlers and never constructs the live Launcher. It checks compact dimensions, reachable lower content, scrollbar drag target and actual scrolling; renders the dashboard and owned client dialog; and runs through the existing desktop material CI smoke test.
