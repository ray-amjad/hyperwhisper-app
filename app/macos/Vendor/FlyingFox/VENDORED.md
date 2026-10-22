# Vendored FlyingFox

This directory is a patched copy of [FlyingFox](https://github.com/swhitty/FlyingFox)
by Simon Whitty, under its MIT licence (`LICENSE`, unchanged).

- Upstream tag: `0.27.1` (commit `4e246d3fb515bd2069e5c169abdc75808b90548f`)
- Vendored: `LICENSE`, `Package.swift`, `FlyingFox/Sources`, `FlyingSocks/Sources`, `CSystemLinux`
- Not vendored: the upstream tests, docs and CI. `Package.swift` drops the two test targets for that reason.
- The app links the `FlyingFox` product through `XCLocalSwiftPackageReference "Vendor/FlyingFox"` in
  `hyperwhisper.xcodeproj`.

## Why it is vendored

Issue #1463. FlyingFox reads a request head with `bytes.lines`, whose `CollectUntil` checks the whole
line's suffix after every byte. One header line of n bytes costs about n²/2 steps, there is no limit on
a line or on the head, and there is no deadline. An 8 KB header took 2.5 s, a 40 KB one more than 15 s,
and a few 300 KB headers took the whole Local API down. The app's own guards run in route handlers,
after the head is read, so they cannot help. No upstream release through 0.27.1 changes this, and
FlyingFox has no public hook in front of its decoder.

## The patch

`hyperwhisper-1463.patch` in this directory is the exact diff from upstream 0.27.1 (apply with
`git apply` in a FlyingFox 0.27.1 checkout). Every changed line is marked `HyperWhisper patch (#1463)`.

- `HTTPServer+Configuration.swift`: `Configuration.requestHeadLimits` and the
  `HTTPServer.RequestHeadLimits` type (max line bytes, max head bytes, read timeout). The default,
  `.unlimited`, keeps upstream behaviour apart from the linear reader.
- `HTTPDecoder.swift`: `HeadLineReader` reads each byte once and counts it against the limits. A
  line is the bytes before its LF, less one trailing CR. `decodeRequest` reads the request line and the
  headers together through it, inside `withThrowingTimeout` when `readTimeout` is set.
  `decodeResponse` (the HTTP client) uses the same reader with no deadline. Over a limit it throws
  `HTTPDecoder.HeadTooLargeError`.
- `HTTPServer.swift`: passes the limits to each connection's decoder, and answers
  `HeadTooLargeError` with `HTTPConnection.refuseHeadTooLarge()` before it closes the connection.
- `HTTPConnection.swift`: `refuseHeadTooLarge()` sends `431 Request Header Fields Too Large` with
  `Connection: close`, half-closes the socket, and drains the client's remaining bytes for at most 1 s
  or 1 MiB, so the close does not reset the connection before the client reads the 431.

The app sets the limits in `LocalAPIServer.serverConfiguration(address:headReadDeadline:)`.

## Updating

To move to a newer upstream release: copy the same paths from the new tag, drop the test targets from
`Package.swift`, apply `hyperwhisper-1463.patch` (fix any conflict by hand), regenerate the patch, and
update the tag above. If upstream ever caps the request head itself, switch back to the remote package
and delete this directory.
