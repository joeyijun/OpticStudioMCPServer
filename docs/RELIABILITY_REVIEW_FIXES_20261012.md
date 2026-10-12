# Reliability review fixes — 2026-10-12

1. Explicit safety mappings now cover Get/Set/ClearVignetting, environment,
   polarization and stop-surface helper commands. Read-only requests no longer
   create mutation snapshots or fail the internal read-only gate. The manifest
   generator audits actual C# call syntax, including conditional expressions,
   local variables and helper parameter call sites; unresolved/unknown commands
   fail the build. Negative fixtures cover the former regex blind spots.
2. Launcher preferences are deferred. Active endpoint/token/installation are
   kept separately from edited preferences, including connection setup copying.
   Stop/Exit checks real activity and rejects busy/stale states. If health cannot
   be reached, force stop requires an explicit warning confirmation. Changing
   settings no longer implicitly kills the Host.
3. Host and private-RPC tool admission each bound pending requests (default 32)
   and queue wait (default 30 seconds). Normal tool requests enter admission
   before lease/RPC waits. Capacity/timeouts return structured tool errors;
   request IDs remain owned by the official SDK. Diagnostic endpoints and
   protocol cancellation bypass normal tool admission. Tests exercise full,
   timeout, cancellation, slot recovery and real HTTP/SSE errors while a Fake
   Worker operation is running.
4. Host/Worker source commits come from assembly informational versions, not a
   mutable runtime file. Health exposes the active certificate's public identity,
   expiry, remaining days and renewal recommendation, without passwords or PFX
   paths. Copy diagnostics includes these fields and the Launcher build.
   Certificates expiring within 30 days are replaced at a safe next Start; new
   remote trust still requires fingerprint review. No trust-store bypass is used.

Validation is performed with Fake Workers, vendor-independent read-only safety
boundaries and offline desktop rendering. These are not claims of new production
COM-hang or optical-result acceptance. Deploy the candidate before real-machine
read-only verification; production service is not changed by local tests.
