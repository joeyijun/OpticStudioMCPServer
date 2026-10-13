# ZRD diagnostics and simple HTTPS — 2026-10-10

## ZRD changes and verification boundary

The archived mixed-mode sample is not adequate evidence that every native ZRD
reader is broken. Two adapter issues were identified and corrected:

- The audit did not set a filter; it now explicitly clears `IZRDReader.Filter`
  instead of inheriting potentially unrelated viewer/model filtering.
- It rejected every Sequential mode system, including actual NSC-group mixed
  systems; those are now eligible, with native reader validity still authoritative.

Enumeration now shares the reader deadline, and failed audits retain the input
SHA-256 plus available native status/filter diagnostics. Zero native records
are still an error, not a fabricated successful empty graph. These changes
compile against real 24.1.3.0 references; live success is NOT yet established.

The source sequence was reviewed against the
[official 2024 R1 example](https://developer.ansys.com/docs/zos-api-interface-2024-r1/_c_sharp_standalone_05__read__z_r_d__file_8cs-example.xhtml).
An independent offline `Run-ZrdDiagnostics.cmd` creates its own Standalone
application and fresh 10-analysis-ray uncompressed-full database from the
official Beam splitter sample. It compares default/empty-filter reads of the
fresh database and the archived database, with 1024-ray/8192-segment/128-MiB
limits. The normal MCP Host is not contacted or terminated, official Samples
are not overwritten, and the application is closed in finally. It requires
one spare licensed Standalone instance. Diagnostic files are retained.

Run that script only on the OpticStudio computer; return its generated JSON.
Windows PowerShell 5.1 compilation was tested locally against vendor references;
no licensed application was started on the AI computer. A passing fresh native
read, followed by an MCP audit of the same generated file, is the pending
positive-path gate. Do not call the historical-sample error resolved merely
because the adapter has better diagnostics.

## HTTPS usability and trust boundary

The Launcher now has automatic RSA-3072 server-certificate generation. LAN IP,
machine name, localhost, IPv4/IPv6 loopback SANs are explicit. It produces a
one-year non-CA, server-auth-only certificate. A unique password-protected PFX
is kept under a directory restricted to the current SID and SYSTEM; passwords
remain DPAPI-protected in existing Launcher preferences.

Local or remote trust is a SEPARATE user-confirmed current-user operation.
Remote setup exposes DER public bytes and SHA-256 only. CA/private-key setup is
rejected. A fingerprint inside the same JSON is integrity metadata, NOT an
independent identity proof: compare with the trusted source computer before
approval. No process-wide certificate-validation callback is weakened and no
trust store is modified during generation or tests.

Launcher-managed connections are HTTPS-only. Start automatically prepares or
reuses the certificate; Settings only exposes Copy connection setup, not TLS
switches or PFX fields. Trust remains explicitly confirmed. On upgrade from
HTTP, refresh both computers' connection setup and AI-client configurations.
Certificates that are missing, invalid, expired or do not cover the current
endpoint IP are replaced at Start; remote users must confirm new fingerprints.
Existing valid saved PFX credentials are reused. Clients with independent CA
stores may still need their own trust configuration.

Local tests passed generation, SAN encoding, public/private separation, PFX
load, ACL isolation, absence of implicit trust, rejection of CA input and the
full offline desktop regression/render suite, first-run preparation, reuse and
address-change replacement. Real Windows HTTPS handshake, authenticated MCP
discovery and tool calls pass an isolated Fake Worker E2E. The user confirmed
both computers work with the preceding HTTPS handshake-fix package; the new
simplified Launcher still needs deployment confirmation.
