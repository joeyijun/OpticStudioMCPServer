# Upgrade and generation reliability fixes

Scope: preserve upgrade data, prevent busy optical shutdown during upgrades,
release dead-generation foreground ownership, and keep update networking off the
WPF Dispatcher. Multi-adapter address selection is unchanged.

## Persistent installation data

The shared Installer/Updater classification now preserves `https` and
`task-journals`, alongside saved preferences, credentials, logs, snapshots and
shortcut icons. Portable initial-copy exclusions use the same directory names.
Successful replacements and rollback both preserve existing PFX/journal bytes;
staging data cannot overwrite them.

## Local maintenance boundary

The Host owns a current-Windows-user-only Named Pipe, `ZemaxMcpMaintenance-PID`.
It is not an HTTP route and cannot be used by a remote Bearer-token holder.
The client verifies the pipe server PID against the checked Host. Handshakes
have bounded connect/read/write waits, and a random operation ticket prevents
another local update from resuming or stealing a prepared service.

Preparation freezes Host admission only when no tool calls are active or queued.
It rejects background Job holds, freezes Worker invocation admission, waits for
startup ownership, and rejects cancellation drain. A short non-COM status RPC
also rejects background ZOS-API initialization, active Worker Jobs, or a Worker
that cannot explicitly report a completed initialization state. Acknowledged preparation
keeps admission closed until explicit same-ticket resume or process shutdown;
new tools/call requests return `service_maintenance`, retaining the SDK-managed
request ID. Static tools/list remains available.

The Updater checks every installed Host before stopping any checked process.
Unverifiable/old Hosts and unowned Workers postpone the update, rather than
guessing that a running process is idle. On preparation failure, live Hosts it
already prepared are resumed. Installation paths are verified before targeting
processes. The installation lock also blocks Launcher startup during replacement.
Initial repair of an incomplete installation refuses to kill unchecked processes.

In-app updates first download and verify the signed package without stopping the
service. The Launcher transfers the maintenance ticket and Host ownership to the
Updater before exiting, so window cleanup does not kill an unprepared service.
For an older build without maintenance support, safely finish/cancel Tasks and
use tray Exit before running the new installer. There is no automatic force-update
fallback or second portable service after a refused existing-install upgrade.

## Foreground control lease

Generation-start events bind the optical owner, including initial lazy Worker
startup inside a foreground lease. Generation-end invalidates that generation's
owner even without Job holds. If a foreground call is unwinding, cleanup waits
for its execution lease release. A delayed older-generation event cannot revoke
a newer generation's owner or Job hold.

## Responsive update download

Release metadata and ZIP downloads use asynchronous HTTP, streaming and bounded
sizes (2 MiB metadata, 512 MiB package). Header requests have a 60-second timeout;
update attempts request cancellation after ten minutes. A Cancel
download action also aborts a stalled response body, without certificate-validation
bypasses. Signature/hash verification and extraction run off the UI thread;
cancellation is checked between those phases and before any handoff, rather
than terminating a file operation mid-write. Download progress is coalesced to
avoid flooding the Dispatcher. Failed/cancelled attempts clean up their temporary
staging folder, resume any owned preparation and leave installed files untouched.

## Isolated validation

- Real net48 Updater against a copied .NET Host and Fake Worker: busy refusal
  leaves files/process alive; acknowledged idle replacement succeeds.
- Actual MCP HTTP tools/call after preparation is rejected; tools/list remains
  available; explicit resume restores tool execution.
- Background initialization, Job and cancelled-operation drain block preparation; unrelated
  maintenance tickets cannot resume/claim an existing preparation.
- Generation-only faults, active-call unwind and stale fault events preserve
  correct foreground ownership behavior.
- Installed PFX/Task journal bytes survive success and rollback, including
  conflicting copies supplied in staging.
- Asynchronous download tests cover BOM metadata, HTTP errors, size bounds and
  a stalled read that ignores its cancellation token.

These tests do not terminate or modify the deployed OpticStudio service. A new
package still requires deployment before real-machine upgrade acceptance.
