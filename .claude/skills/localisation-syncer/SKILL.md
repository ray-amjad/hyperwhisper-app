---
name: localisation-syncer
description: Keep localization files in sync across platforms (macOS, Windows, Next.js website). Compares translations against the source-of-truth for each platform, identifies missing/extra keys, adds native translations, and finds unused keys in the base files.
context: fork
model: sonnet
allowed-tools:
    - Bash
    - Read
    - Edit
    - Write
    - Glob
    - Grep
---

# Localization Syncer

Keep localization files aligned with their source-of-truth, update translations in a native voice, and prune keys that are no longer referenced in the app code.

## Step 1: Ask What the User Wants to Do

Ask **intent first, then platform**. Use the AskUserQuestion tool for each.

### 1a. Intent

- **Sync missing translations** — a key exists in the base file but some locales don't have it. The `compare_*.py` scripts find them; you add native translations to every locale that's short.
- **Add new translations for new keys** — you just added a key to the base file and need every locale populated. Same flow as "sync missing" but driven by a known new key.
- **Find & remove unused keys** — keys exist in the base + locales but no longer appear in the app source. Run `find_unused_keys.py`, review, then `remove_keys.py` to strip them from all 40 files at once.

### 1b. Platform (multiSelect)

- **macOS** — `Localizable.strings` files in `app/macos/hyperwhisper/Localizations/`
- **Windows** — `.resx` XML files in `app/windows/HyperWhisper/Resources/`
- **Next.js Website** — JSON message files in `nextjs/messages/`

The rest of this doc has one section per platform with sync workflows, plus a cross-platform "Remove Unused Keys" workflow further down. Jump to the section that matches the user's intent + platform choice.

---

## MANDATORY: Verification Gate Before Reporting Success

**Your success summary MUST be derived from command output you actually ran and quoted in this run — never from your memory of what you intended to write.** This gate is not optional and applies to every platform.

> A previous run reported *"inserted native translations into all 39 locale files… the compare script confirms 0 missing entries"* — but `git status` showed **only the Base file changed**, every locale file was untouched, and the compare script had never been run. The batch `for` loop had silently failed to persist, yet the summary was written from intent. The user had to redo the entire sync by hand. **This must never happen again.**

Before you claim a sync/add is done, run BOTH commands below and **paste their raw output into your summary**:

1. **Prove which files actually changed on disk** (run from repo root):
   ```bash
   # macOS:   git status --short app/macos/hyperwhisper/Localizations/
   # Windows: git status --short app/windows/HyperWhisper/Resources/
   # Next.js: git status --short nextjs/messages/
   ```
   For an add/sync you MUST see a modified (`M`) entry for **every** locale file you claim to have touched. If only one — or a handful — show up, your writes did **not** land. STOP and fix before reporting anything.

2. **Count each added/changed key across ALL target files** — expected count = number of locales **including base** (40 for macOS/Windows):
   ```bash
   # macOS example:   grep -rl '"new.key.name"'  app/macos/hyperwhisper/Localizations/ | wc -l   # expect 40
   # Windows example: grep -rl 'name="NewKey"'    app/windows/HyperWhisper/Resources/ | wc -l    # expect 40
   ```
   Then re-run the platform `compare_*.py` script and **quote its actual "0 missing" output** — do not paraphrase it.

### Pass / Fail rule (no middle ground)

- **PASS** only if all three hold AND you quoted them: `git status` shows every claimed file modified, the per-key count equals the locale count, and `compare_*.py` reports 0 missing.
- **FAIL** otherwise: report **FAILURE**, list the exact files still missing the key (`grep -rL '"key"' <dir>`), and do NOT claim the sync is complete.

### Do NOT (anti-patterns that caused the past failure)

- Do NOT report "added to all N locales" from memory without running `git status` and the grep count this run.
- Do NOT claim "compare script confirms 0 missing" without pasting the script's real output.
- Do NOT trust that a batch Python `for` loop worked because it printed `OK:` lines — a heredoc can be sandboxed, skipped, or fail to persist. On-disk `git status` is the only proof a write landed.

---

## Platform: macOS

### Source of Truth
`app/macos/hyperwhisper/Localizations/Base.lproj/Localizable.strings` (English)

### Locale Files
**IMPORTANT: There are 39 locale files. You MUST sync ALL of them, not just a subset.**

Discover all locale files dynamically:
```bash
ls app/macos/hyperwhisper/Localizations/*.lproj/Localizable.strings
```

This will return all non-Base locale directories (ar, bg, ca, cs, da, de, el, es, et, fi, fr, he, hi, hr, hu, id, is, it, ja, ko, lt, lv, ms, nb, nl, pl, pt, ro, ru, sk, sl, sr, sv, th, tr, uk, vi, zh-Hans, zh-Hant).

### CRITICAL: File Encoding Rules

**macOS `.strings` files are extremely sensitive to encoding and line endings.**

1. **CRLF Line Endings Required**: All `.strings` files use Windows-style CRLF (`\r\n`) line endings. The `plutil` validator and Xcode's `builtin-copyStrings` will fail with "Unexpected character / at line 1" if this is changed.

2. **NEVER use the Edit tool** on `.strings` files - it converts CRLF to LF and corrupts the file.

3. **NEVER rewrite entire files** - This risks encoding corruption.

4. **Always validate after changes**:
   ```bash
   plutil -lint path/to/Localizable.strings
   ```

5. **Check line endings**:
   ```bash
   file path/to/Localizable.strings
   # Should show: "with CRLF line terminators"
   ```

### Workflow

#### 1. Find Missing Keys

The compare script auto-discovers all 39 non-Base locale files:

```bash
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/macos/compare_localizable.py
```

#### 2. Get English Values for Missing Keys

```bash
cd $(git rev-parse --show-toplevel)/app/macos/hyperwhisper/Localizations
grep '^"missing.key.name"' Base.lproj/Localizable.strings
```

#### 3. Add Translations to ALL 39 Locales

**IMPORTANT: You must insert translations into ALL locale files that are missing keys, not just a subset.**

Use this batch Python pattern that inserts a key into all locales at once, preserving CRLF encoding. Build a `translations` dict mapping each locale code to its native translation, then insert after a nearby existing key:

```python
python3 << 'PYEOF'
import os

translations = {
    "ar": "Arabic translation",
    "bg": "Bulgarian translation",
    # ... ALL 39 locales ...
    "zh-Hant": "Traditional Chinese translation",
}

base_dir = "app/macos/hyperwhisper/Localizations"  # relative to repo root — run this from the repo root
marker = b'"existing.nearby.key"'

for locale, value in translations.items():
    filepath = os.path.join(base_dir, f"{locale}.lproj", "Localizable.strings")
    if not os.path.exists(filepath):
        print(f"SKIP (not found): {filepath}")
        continue
    with open(filepath, 'rb') as f:
        content = f.read()
    if b'"new.key.name"' in content:
        print(f"SKIP (already exists): {locale}")
        continue
    idx = content.find(marker)
    if idx == -1:
        print(f"SKIP (marker not found): {locale}")
        continue
    end_line = content.find(b'\r\n', idx)
    if end_line == -1:
        end_line = content.find(b'\n', idx)
        end_line = end_line + 1 if end_line != -1 else len(content)
    else:
        end_line += 2
    insert_text = f'"new.key.name" = "{value}";\r\n'.encode('utf-8')
    new_content = content[:end_line] + insert_text + content[end_line:]
    with open(filepath, 'wb') as f:
        f.write(new_content)
    print(f"OK: {locale} -> {value}")
print("\nDone!")
PYEOF
```

For multiple missing keys, repeat the pattern for each key (or nest the key loop inside the locale loop).

Alternatively, for a single locale file, use the insert script:
```bash
python3 .claude/skills/localisation-syncer/scripts/macos/insert_translation.py \
    --file app/macos/hyperwhisper/Localizations/zh-Hans.lproj/Localizable.strings \
    --after "existing.key.name" \
    --key "new.key.name" \
    --value "翻译文本"
```

#### 4. Validate ALL Files and Re-check

```bash
# Validate all locale files
for f in $(git rev-parse --show-toplevel)/app/macos/hyperwhisper/Localizations/*.lproj/Localizable.strings; do
    result=$(plutil -lint "$f" 2>&1)
    if ! echo "$result" | grep -q "OK"; then echo "FAIL: $f - $result"; fi
done

# Verify no locales are missing the key
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/macos/compare_localizable.py
```

**Then apply the [MANDATORY Verification Gate](#mandatory-verification-gate-before-reporting-success) — quote `git status --short` and the per-key grep count before reporting success.**

---

## Platform: Windows

### Source of Truth
`app/windows/HyperWhisper/Resources/Strings.resx` (English)

### Locale Files
**IMPORTANT: There are 39 locale files. You MUST sync ALL of them, not just a subset.**

Discover all locale files dynamically:
```bash
ls app/windows/HyperWhisper/Resources/Strings.*.resx
```

This will return all locale files (ar, bg, ca, cs, da, de, el, es, et, fi, fr, he, hi, hr, hu, id, is, it, ja, ko, lt, lv, ms, nb, nl, pl, pt, ro, ru, sk, sl, sr, sv, th, tr, uk, vi, zh-Hans, zh-Hant).

### File Format
Windows `.resx` files are XML. Each translatable string is a `<data>` element:
```xml
<data name="KeyName" xml:space="preserve">
  <value>English text</value>
</data>
```

### Workflow

#### 1. Find Missing Keys

```bash
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/windows/compare_resx.py
```

#### 2. Get English Values for Missing Keys

Read the base `Strings.resx` file and find the `<data>` elements for missing keys.

#### 3. Add Translations

**IMPORTANT: You MUST add translations to ALL locale files that are missing keys, not just a subset. The compare script will show issues for every locale file — fix them all.**

Use the Edit tool to add missing `<data>` elements to the locale `.resx` files. Place them in the same order as the base file. The XML format is straightforward and safe to edit directly (no CRLF sensitivity like macOS `.strings`).

To be efficient with many locale files, batch similar edits together and process all files in parallel where possible.

Example insertion:
```xml
<data name="NewKey" xml:space="preserve">
  <value>Translated text</value>
</data>
```

#### 4. Re-check

```bash
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/windows/compare_resx.py
```

**Then apply the [MANDATORY Verification Gate](#mandatory-verification-gate-before-reporting-success) — quote `git status --short` and the per-key grep count before reporting success.**

---

## Platform: Next.js Website

### Source of Truth
`nextjs/messages/en.json` (English)

### Locale Files
All other JSON files in `nextjs/messages/` (40+ locales: ar, bg, ca, cs, da, de, el, es, et, fi, fr, he, hi, hr, hu, id, is, it, ja, ko, lt, lv, ms, nb, nl, pl, pt, ro, ru, sk, sl, sr, sv, th, tr, uk, vi, zh, zh-Hant).

### File Format
Nested JSON objects using next-intl conventions. Keys are dot-separated via nesting:
```json
{
  "section": {
    "key": "Translated value"
  }
}
```

### CRITICAL: JSON Syntax Rules

**Never use curly quotes in JSON strings.** They break JSON parsing.

| Bad | Good |
|-----|------|
| `"下载"` (curly quotes) | `「下载」` (corner brackets) |
| `"text"` | `「text」` or `'text'` |

Use corner brackets `「」` for Chinese/Japanese or straight quotes `'` for other languages.

### Workflow

#### 1. Find Missing Keys

Check all locales at once:

```bash
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/nextjs/compare_messages.py
```

Or check specific locales:

```bash
python3 .claude/skills/localisation-syncer/scripts/nextjs/compare_messages.py --langs nextjs/messages/ja.json nextjs/messages/es.json
```

#### 2. Add Translations

Use the Edit tool to add missing keys to locale JSON files. Match the nesting structure from `en.json`.

#### 3. Validate JSON and Re-check

```bash
cd $(git rev-parse --show-toplevel)
# Validate all JSON files parse correctly
for f in nextjs/messages/*.json; do python3 -m json.tool "$f" > /dev/null && echo "OK: $f" || echo "FAIL: $f"; done

# Re-run comparison to verify
python3 .claude/skills/localisation-syncer/scripts/nextjs/compare_messages.py
```

**Then apply the [MANDATORY Verification Gate](#mandatory-verification-gate-before-reporting-success) — quote `git status --short` and the per-key grep count before reporting success.**

---

## Workflow: Remove Unused Keys (macOS & Windows)

Use this flow when the user says "find unused translations", "prune unused keys", "clean up localizations", or similar.

The flow has three stages: **find → curate → remove**. Never skip curation — the `find` script is conservative but not infallible.

### Stage 1 — Find

Each `find_unused_keys.py` classifies every base-file key into one of three buckets:

- **Used** — the key appears as a literal string (`"key.name"`) somewhere in the source tree.
- **Maybe-used (dyn)** — the key wasn't found literally, but its prefix matches a string-interpolation site (Swift `"prefix.\(x)"` or C# `$"prefix.{x}"`). The key might be assembled at runtime.
- **Definitely unused** — no literal match and no dynamic prefix match. Safest removal candidates.

**macOS.** Scans `.swift` under `app/macos/hyperwhisper/`, skipping `Libraries/`, `Pods/`, `.build/`, `build/`, `DerivedData/`.

```bash
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/macos/find_unused_keys.py --json /tmp/mac_unused.json
```

**Windows.** Scans `.cs` and `.xaml` under `app/windows/HyperWhisper/`, skipping `bin/`, `obj/`, `.vs/`, `packages/`, `Migrations/`, `*.Designer.cs`, `*.g.cs`. Recognises both `Loc.S("key")` (C#) and `{loc:Loc key}` (XAML).

```bash
cd $(git rev-parse --show-toplevel)
python3 .claude/skills/localisation-syncer/scripts/windows/find_unused_keys.py --json /tmp/win_unused.json
```

### Stage 2 — Curate

Extract the `definitely-unused` list into a plain-text file, one key per line:

```bash
python3 -c "import json,sys; [print(k) for k in json.load(open(sys.argv[1]))['unused']]" /tmp/mac_unused.json > /tmp/mac_to_remove.txt
python3 -c "import json,sys; [print(k) for k in json.load(open(sys.argv[1]))['unused']]" /tmp/win_unused.json > /tmp/win_to_remove.txt
```

**Show the list to the user via AskUserQuestion and get explicit confirmation.** Spot-check any suspicious-looking keys with `grep` first (e.g. constants that might be assembled from an enum or referenced from a nib/xib/xaml resource dictionary). Let the user strike out any keys they want to keep — edit the `.txt` file accordingly. The file accepts `#` comments and blank lines, so the user can annotate freely.

### Stage 3 — Remove

The `remove_keys.py` scripts take the curated key list and strip each key from the base file **and every locale file** in one pass. Both scripts support `--dry-run` so you can preview the touch count before committing.

**macOS.** Binary-mode edits that preserve the file's CRLF line endings, validated with `plutil -lint` afterwards:

```bash
# Preview:
python3 .claude/skills/localisation-syncer/scripts/macos/remove_keys.py \
    --keys-file /tmp/mac_to_remove.txt --dry-run

# Commit:
python3 .claude/skills/localisation-syncer/scripts/macos/remove_keys.py \
    --keys-file /tmp/mac_to_remove.txt
```

**Windows.** String-based block removal that preserves the file's indentation and XSD header verbatim, validated by parsing afterwards:

```bash
python3 .claude/skills/localisation-syncer/scripts/windows/remove_keys.py \
    --keys-file /tmp/win_to_remove.txt --dry-run

python3 .claude/skills/localisation-syncer/scripts/windows/remove_keys.py \
    --keys-file /tmp/win_to_remove.txt
```

Both scripts print a per-locale removal count. Expect `N` (the curated list size) keys removed from all 40 files — if a file is short, the base/locale pair was already out of sync and you should re-run `compare_*.py` to investigate.

### Stage 4 — Verify

```bash
# macOS: confirm no locale drifted out of sync
python3 .claude/skills/localisation-syncer/scripts/macos/compare_localizable.py

# Windows: same
python3 .claude/skills/localisation-syncer/scripts/windows/compare_resx.py
```

Total issues should be 0. If not, a removal mismatch leaked a key in one locale — investigate before committing.

---

## Translation Requirements (All Platforms)

- Preserve all format tokens (`%@`, `%d`, `%0.2f`, `{variable}`) and spacing.
- Preserve ellipses and punctuation (`…`, `...`, `:`) exactly as intended.
- Preserve newlines and list formatting (`\n`, bullets) without reflow.
- Keep product names and model names unchanged unless already localized in that locale file.
- Prefer native, natural UI phrasing over literal translation.

## Troubleshooting

### macOS: "Unexpected character / at line 1" Error

This means the file encoding was corrupted. To fix:

1. Check if there's a good version in git:
   ```bash
   git log --oneline -5 -- path/to/Localizable.strings
   git show <commit>:path/to/Localizable.strings > path/to/Localizable.strings
   plutil -lint path/to/Localizable.strings
   ```

2. If the file validates, carefully re-add your translations using the binary Python method above.

### macOS: Line Ending Issues

If `file` command shows LF instead of CRLF:
```bash
# DO NOT use perl or sed to convert - they may corrupt encoding
# Instead, restore from git and re-apply changes
```

## Notes

- Do not rename keys on any platform.
- Do not delete existing translations unless explicitly requested.
- Do not use the Edit tool on macOS .strings files.
- Do not sort or reorder macOS .strings files - this risks corruption.
- If a locale file is missing entirely, copy the source-of-truth and translate in place.
