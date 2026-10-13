# P1 branch build and transport review — 2026-10-10

Base reviewed: `9ebaa6a1998b7931669041dcda32705fc5701c4c` on
`agent/p1-workflows-reliability-20261009`, Draft PR #40.
The branch is based on released 1.5.1 main and contains 146 public tools.
Its latest hosted Windows run passed, but a complete local net48 Worker build
initially failed. The hosted net10 simulations did not compile these sources
under the actual Worker framework, nor the ZOS-dependent tool wrappers.

## Fixes

- Correct malformed clear-ray coordinate validation in SequentialPupilSampler.
- Centralize finite-number, streaming SHA-256 and Windows fully-qualified path
  compatibility; retain the original finite-arithmetic checks and file hashes.
- Use net48-compatible dictionary admission, clamping and last-array indexing.
- Disambiguate global System namespaces from the Worker's Tools.System sibling.
- Pass clipping diagnostics' cancellation token by name, avoiding a newly added
  footprint geometry parameter receiving the token positionally.
- Add a vendor-independent **net48** optical-kernel compile/run projection.
  Its namespace sentinel mirrors Tools.System; its compile inventory must cover
  every optical kernel linked into the net10 simulation project.
- Reuse the request-ID-aware multiline SSE selector for Launcher Job actions.
  Ignore progress, server requests and unrelated responses; validate both the
  JSON-RPC ID and returned Job ID, and preserve structured denial errors.
- Bound Launcher RPC stream read/write waits as well as response-header waits.
- Preserve case-sensitive endpoint paths in credential-bound diagnostic cursors;
  normalize scheme/host without conflating different reverse-proxy applications.

## Verification boundary

The complete Worker compiled against the local OpticStudio **24.1.3.0**
reference with zero errors and 15 pre-existing nullable warnings. The net48
projection and net10 simulations passed. Launcher regression fixtures passed
for SSE/JSON Job results, absent/foreign results, endpoint/credential isolation,
Task presentation and offline WPF renders. Other software checks are recorded
in the accompanying local test output.

Also passed: 127 Worker class / 146-command registration, explicit metadata,
README tables, Stage A–F safety guards, static coverage inventory, official
HTTP Host/Fake Worker Tasks/ownership/recovery E2E, and updater preservation,
transient-lock retries and rollback. Public CI for the new local changes has
not run until they are pushed; the earlier green run belongs to the base SHA.

No production OpticStudio service, user lens or installed preferences were
modified. These are compilation/software tests, not new licensed numeric
acceptance, proof of real COM-hang behavior or completion of E1–E8. No version,
main merge or public release is changed by this local continuation.
