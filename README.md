# Fuzz coverage

This branch carries no code. It holds one file, `fuzz-coverage.json`, which records what every
scheduled fuzz round walked: the platform, the routes, the commit it built, and what it filed.

**Do not merge this branch into `main`.** It is an orphan branch with no shared history.

## Why it exists

The fuzz rotation is `linux -> win -> mac`, one platform per round. For its first two months it only
ever ran `linux`.

Each round used to write its coverage as a `FUZZ-COVERAGE` line in a Slack reply, and the next round
looked for that line in the Slack **channel**. A channel read prints only top-level messages, so the
marker was never found. An empty result read as "no round has ever run", so the round started the
rotation again at `linux`. Every round did the same, and each report looked correct on its own.

The record is a file now, and one command reads and writes it. A round is told what to run; it parses
nothing.

## How to read it

```bash
fuzz-coverage pick     # what the next round must run, and why
fuzz-coverage read     # the whole record
fuzz-coverage matrix -o coverage.html
```

## The shape

`rounds` is append-only. Everything else is derived when it is read — the next platform, the
least-covered routes, the matrix. There is deliberately **no** `next_platform` field: a stored derived
value goes stale silently, which is the exact fault this replaces.

`routes` is the route list for this app. Add a route by editing it here. That needs no change to the
`fuzz-desktop` skill.
