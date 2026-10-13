# Task history and results

The Tasks page merges official MCP Tasks with their same-generation Worker Jobs,
so one operation does not appear twice. It displays all unfinished operations and
the most recent **30 terminal operations**, ordered newest first. A completed
history entry cannot displace an active operation. Filters/search apply to this
bounded recent window, not an unlimited archive.

The Worker retains metadata for the most recent 30 terminal Jobs, in addition to
its bounded pending/running queue. The Host event cache and official Task ledger
also prune old terminal metadata. Official Task admission remains bounded (94
records including active work and reserved admissions); saturation is rejected,
not converted into synchronous execution or silent cancellation.

Large result payloads have a separate limit: the newest 16 terminal results.
Older retained metadata may therefore report `resultExpired`. Export important
optical results before that window expires; history is not durable result storage.
The metadata-only official Task journal does not persist optical result arrays.

View result uses the same authenticated MCP transport and ownership checks as AI
calls. Job requests include the mandatory request-scoped client capabilities. The
last fetched display (maximum 24,000 characters) is retained across status refreshes
for the same selected operation and endpoint/credential only. Switching endpoint,
credential or selection never displays a previous operation's response.

Undefined blank/unweighted MFE numeric values are represented as JSON `null`, not
zero. Weighted invalid objective data still fails closed. Replacing a merit
function preserves OpticStudio's mandatory row as BLNK before the wizard starts
at row 1; failures continue to restore the saved pre-wizard merit function.
