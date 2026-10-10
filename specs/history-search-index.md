# Spec: FTS5 trigram index for Local API `/recordings/search`

Status: DRAFT — interview not started. Repo `ray-amjad/hyperwhisper-app`, base `main` at `8b105134`.
Issues: [#1202](https://github.com/ray-amjad/hyperwhisper-app/issues/1202) (Windows), [#1197](https://github.com/ray-amjad/hyperwhisper-app/issues/1197) (Linux).

## Problem

The Local API search endpoint scans every transcript row with `LIKE '%term%'`. At 20,000 rows that costs more than the 20 ms target.

- Windows (#1202), after PR #1200: a no-match search at 20,007 rows has p50 33 ms (was 196 ms). The raw 3-column LIKE alone is 25.8 ms, so no LIKE scan can meet 20 ms.
- Linux (#1197), after PR #1195: 3 runs at 20,000 rows have p50 21.7 / 15.7 / 20.5 ms (was 211 ms). Plain FTS5 matches whole tokens only, so a substring search needs the trigram tokenizer.

## Done when

- Windows: p50 of 10 curls < 20 ms at 20,000 rows, and `HyperWhisper.SmokeTests -c Release` passes.
- Linux: 3 sets of 10 curls each give p50 < 20 ms at 20,000 rows, and `LocalApi.Tests -c Release` passes `RecordingsQueryRunsInSql`.
- The results of every search are the same as today, row for row (the match rule does not change).
- The PR body carries `Fixes #1202` and `Fixes #1197`.

## The decision

Add an FTS5 trigram index to both heads (schema change, migration, sync on write), or accept the current cost. See "Options".

## Code on main (checked 2026-10-10)

- Windows query: `app/windows/HyperWhisper/Services/HistoryService.cs` `QueryPage` (about lines 105-145). LIKE over `Text`, `PostProcessedText`, `TranscribedText`. A non-ASCII rune becomes `_` and an in-memory `OrdinalIgnoreCase` check narrows the rows. A term with NUL skips LIKE (#1198).
- Linux query: `app/shared-dotnet/HyperWhisper.Application/HistoryRepository.cs` `QueryPageAsync` (about lines 67-113). Same rule, but only 2 columns: `Text` and `TranscribedText`. Linux never searches `PostProcessedText`; a test asserts that.
- One schema for both heads: `HyperWhisper.Application.csproj` compiles the Windows `Data/HyperWhisperDbContext.cs`, `Data/Entities/*.cs` and `Migrations/*.cs`. So one migration under `app/windows/HyperWhisper/Migrations/` runs on Windows and Linux.
- `Transcripts` has a `Guid` key (TEXT), so it is a rowid table with an implicit `rowid`. Indexes: `Date`, `Status`.
- Both heads run `MigrateAsync` at startup: Windows `Data/DatabaseInitializer.cs:35`, Linux `ApplicationDb.cs:25`.
- Raw-SQL migration precedent (no Designer file, no snapshot bump): `Migrations/20260827090000_MigrateGoogleChirp3TierToGeminiTranscribe.cs`.
- The only bulk write to `Transcripts` is `ExecuteUpdateAsync` in `HistoryRepository.cs` (retry claim). It sets `Status`, `RetryCount` and `LastRetryDate`, never a text column. Triggers see it like any other write.
- SQLite: `Microsoft.EntityFrameworkCore.Sqlite` 9.0.19 on both heads ships its own `e_sqlite3` (3.4x). The trigram tokenizer needs 3.34+. The code checks it at runtime anyway.
- No `VACUUM` anywhere in the app code.

## Options

- **A. FTS5 trigram external-content table + triggers (recommended).** One raw-SQL migration makes `TranscriptsFts` with `content='Transcripts'` and `tokenize='trigram case_sensitive 0'` over the 3 text columns, adds INSERT, UPDATE and DELETE triggers, and runs `'rebuild'` to fill it. `Down` drops all of it. Cost: the DB grows by about 2-3x the text size.
- **B. Lower-cased shadow column + LIKE.** Still a full scan. LIKE measured 10.6-25.8 ms. Rejected.
- **C. Accept the cost and close both issues.** Windows stays at about 33 ms, Linux at about 16-22 ms.

## Design (option A)

### Migration `2026101xxxxxxx_AddTranscriptsTrigramIndex`

- `CREATE VIRTUAL TABLE TranscriptsFts USING fts5(Text, PostProcessedText, TranscribedText, content='Transcripts', content_rowid='rowid', tokenize='trigram case_sensitive 0');`
- Trigger `Transcripts_ai` AFTER INSERT: insert the new row into `TranscriptsFts`.
- Trigger `Transcripts_ad` AFTER DELETE: the FTS `'delete'` command with the old values.
- Trigger `Transcripts_au` AFTER UPDATE OF `Text`, `PostProcessedText`, `TranscribedText`: `'delete'` the old values, then insert the new ones. A status-only update (the retry claim) does not touch the index.
- `INSERT INTO TranscriptsFts(TranscriptsFts) VALUES('rebuild');` fills the index from the existing rows.
- `Down`: drop the 3 triggers and the table.
- If the SQLite build has no trigram tokenizer, `Up` skips the table and the triggers. The query then keeps LIKE. (How `Up` tests this: open question.)

### Query (both heads)

- Use the index only when the term is ASCII, has no NUL, and has 3 or more characters. Trigram cannot match a term of 1-2 characters.
- Then: `rowid IN (SELECT rowid FROM TranscriptsFts WHERE TranscriptsFts MATCH @q)`, plus the existing LIKE as an exact re-check on the few candidate rows.
- Why the re-check: trigram `case_sensitive 0` folds case by Unicode rules. It folds some non-ASCII letters onto ASCII ones (for example the Kelvin sign `K` onto `k`), and `OrdinalIgnoreCase` does not. Without the re-check a search could return a row that it does not return today.
- Windows matches on the 3 columns. Linux matches on `{Text TranscribedText}` only (an FTS5 column filter), so Linux still never matches `PostProcessedText`.
- The term goes into MATCH as one quoted FTS5 string (`"` doubled), so FTS5 syntax in a term (`AND`, `*`, `:`) is literal.
- All other terms (1-2 characters, non-ASCII, NUL) keep today's path, unchanged.
- No `TranscriptsFts` table (old SQLite, or a test DB made by `EnsureCreated`): keep today's LIKE path. Check `sqlite_master` once per process and cache the result.

### Tests

- Trap: `LocalApi.Tests/Program.cs` makes its DB with `EnsureCreatedAsync`, which makes no FTS table. Its cases use 1-character terms and assert that Linux never matches `PostProcessedText`. Change the fixture to `MigrateAsync`, and add cases with 3+ character terms, so the FTS path runs in the test.
- Add a test that the index follows every write: insert, update a text column, status-only update, delete. After each, the FTS result equals the LIKE result.
- Add a test that a migrated DB has the 3 triggers. This catches a later migration that rebuilds `Transcripts` (SQLite `AlterColumn` copies the table and drops its triggers).
- Keep `RecordingsQueryRunsInSql` green on Linux and `HyperWhisper.SmokeTests -c Release` green on Windows.
- Measure with the same 20,000-row curl method as #1202 and #1197, and put the numbers in the PR body.

## Risks

- The implicit `rowid` of a table with a TEXT key can change on `VACUUM` or on a table rebuild. Then the index points at the wrong rows. Today nothing runs `VACUUM`. The trigger test and the exact LIKE re-check limit the harm (a wrong candidate is dropped by the re-check; a missed row is not).
- DB size: about 2-3x the text size more.
- Migration time on a large history: `'rebuild'` runs once at startup.

## Open questions

1. Option A, B or C? (asked in the main thread)

## Out of scope

- macOS (it has its own store).
- Any change to the match rule, the columns per head, or the response shape.

## Decisions

(none yet)
