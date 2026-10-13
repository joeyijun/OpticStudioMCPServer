# Windows HTTPS handshake fix

The previous candidate loaded the Host's TLS PFX with `EphemeralKeySet`.
Windows Schannel cannot use that private key for server TLS, so Kestrel could
be listening while clients received an EOF during TLS authentication. This
also affected the Launcher on the OpticStudio computer itself.

The Host now imports the key into the current Windows user's key scope,
without machine-wide key access, `Exportable`, or `PersistKeySet`. The
certificate remains alive through Kestrel's serving lifetime and is disposed
on normal Host shutdown. No client certificate checks are disabled and no
trust stores are changed by the Host.

## Verification

`dotnet run --project tests/ZemaxMCP.PrivateRpcTests -c Release -- --https-test`
starts an isolated HTTPS Host with a Fake Worker and verifies real Windows
TLS, authenticated health, static discovery, MCP tools/call and server/discover.
The test validates the exact fixture certificate, its name and a private
custom-root chain, without importing it into Windows trust stores. It also
checks that a wrong token and an untrusted certificate are rejected.

The old implementation failed this test during the TLS handshake. This is a
transport regression test, not a real OpticStudio optical-result test.

## Deployment

Install the fixed candidate on the OpticStudio computer and restart the Host
when no optical Job is active. Existing PFX/password settings can be retained;
generating a replacement certificate is not necessary for this fix.
The local Windows user must have explicitly trusted the server certificate.
For remote use, import the new HTTPS Copy secure setup on the AI computer and
compare fingerprints on both computers before confirming trust.

Do not copy the PFX or its password to the AI computer. Do not disable TLS
certificate validation. If certificate trust or endpoint identity still fails,
retain the Host log and the exact Launcher diagnostic message.
