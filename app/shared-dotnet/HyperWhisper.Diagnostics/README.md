# HyperWhisper diagnostics core

This project provides an opt-in, local diagnostics log and support archive for desktop clients.

The archive contract is deliberately narrow. It contains exactly `system.json`,
`capabilities.json`, and `logs/events.jsonl`. Events use fixed enums and contain no free-form
message or metadata field. System strings are length- and character-filtered before export.
Settings, transcripts, audio, prompts, clipboard contents, credentials, account identifiers,
and raw home paths are never inputs to the archive model.

Logs rotate within caller-selected bounds. On Unix, log directories use mode `0700`, and logs
and archives use mode `0600`. Archive creation is cancellable and atomic: a failed export removes
its temporary file and does not publish a partial ZIP.
