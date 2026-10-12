# File paths and the OpticStudio execution computer

The AI client and OpticStudio may run on different computers. The client may
also connect through a local proxy, so the visible MCP address does not prove
where OpticStudio or its files live.

All Zemax tools execute on the **OpticStudio Worker computer**. Their input
and output paths belong to that Windows filesystem, not the AI client's
filesystem. A local terminal, shell or file-search tool on the AI computer
cannot determine whether a Worker path exists.

## First steps for an AI client

1. Call `zemax_execution_context`. Its `executionComputer`,
   `pathNamespace=opticstudio-worker`, actual Zemax Data directory, current
   lens and connection mode identify the server-side environment.
2. Use `zemax_list_lens_files` when the selected tool profile permits browsing.
   Start with `root="samples"`, `"lenses"` or `"current"`; use returned
   relative directory names to navigate.
3. Supply an absolute **server-side** path to `zemax_open_file`. Confirm
   unsaved work before switching lenses.
4. An AI-client-local file needs an explicit, authorized transfer first.
   This MCP does not upload it automatically. Export/save paths likewise
   remain on the OpticStudio computer; returning a filename does not download
   the file to the AI computer.

The execution context deliberately reports `clientComputerKnown=false`
and `automaticFileTransfer=false`. It does not guess co-location from an IP,
localhost proxy or similarly named Windows directory. Missing Zemax directory
metadata remains null; it is not reconstructed from the AI computer.

## Discovery and compatibility

The Host supplies a common SDK `ServerInstructions` message through modern
`server/discover` and legacy `initialize`. Every tool description repeats a
short execution-location notice, and top-level file-path arguments identify
the Worker namespace. Thus clients that do not surface server instructions
still receive location information in the tool schemas.

`zemax_status` includes the same execution context. Diagnostic health
includes Worker-reported execution computer/current lens information when
the authentication mode permits detailed health. Standalone controls a
Worker-owned application that may differ from the user's visible OpticStudio
window.

These are guidance and provenance, not a guarantee that every AI model will
follow instructions. They do not alter the AI client's independent shell tools.

## Restricted file browser

`zemax_list_lens_files` is read-only and returns only one directory's
`.zos`/`.zmx` file names and non-link child directories. It does not read
contents, recurse, upload, download, open files or modify the active lens.

- Roots derive only from the Worker-reported Zemax Data directory and current
  lens path: Data/Samples, Data/Lens and the active lens directory.
- Arbitrary roots, absolute subpaths, parent traversal, alternate streams,
  wildcards and reparse-point/link directories are rejected.
- The browser supports local-drive roots on the Worker computer, not UNC
  network shares. This restriction does not change existing file-open tools.
- Return bounds are 1–200 entries. Enumeration stops after 2,000 entries or
  a two-second between-entry budget. Windows filesystem calls themselves are
  not interruptible; this is not a hard real-time filesystem deadline.
- Truncated listings are explicitly marked and are not a complete inventory.
- Filesystem permissions remain those of the Worker Windows account. The
  listing is not a general filesystem sandbox for other existing tools.

Execution-context metadata is available in every profile. The browser is
explicitly registered in full-expert, sequential-design,
nonsequential-stray-light and optimization-tolerance, but not basic-viewing.
Normal per-credential tool authorization still applies.

## Verification

Local regressions use separate simulated client/server directories, check
that client-only files cannot appear in a server listing, reject traversal
and directory junction escapes, and verify limits and cancellation.
Authenticated HTTP E2E verifies both modern discovery and legacy initialize
instructions without starting the fake Worker.

These tests do not claim real-machine acceptance of a newly deployed package.
