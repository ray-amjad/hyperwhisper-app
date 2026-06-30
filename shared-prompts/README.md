# Shared Prompt Templates

Cross-platform AI post-processing system prompts for HyperWhisper. These templates are the single source of truth for all platforms (macOS, Windows, iOS, Android).

## Directory Structure

```
shared-prompts/
├── presets/          # One file per preset mode
│   ├── hyper.txt
│   ├── message.txt
│   ├── mail.txt
│   ├── note.txt
│   ├── meeting.txt
│   ├── code.txt
│   └── custom.txt
├── contextual/       # App-aware blocks injected by prompt builders
│   ├── email.txt
│   ├── work-message.txt
│   ├── personal-message.txt
│   ├── document.txt
│   ├── code.txt
│   └── terminal.txt
├── fragments/        # Reusable prompt fragments
│   ├── email-formatting-rules.txt
│   ├── anti-reply-directive.txt
│   └── override-directive.txt
└── README.md
```

## Placeholder Contract

Each template contains `{{PLACEHOLDER}}` markers that must be substituted at runtime by the platform-specific prompt builder.

| Placeholder | Description | Used in |
|---|---|---|
| `{{CONTEXTUAL_FORMATTING_BLOCK}}` | Conditionally injected app-aware formatting block derived from application classification | hyper.txt, message.txt, note.txt, meeting.txt, code.txt, custom.txt |
| `{{EMAIL_FORMATTING_RULES}}` | Loaded from `fragments/email-formatting-rules.txt` | mail.txt |
| `{{CUSTOM_INSTRUCTIONS}}` | User-provided custom instructions | custom.txt |

### Placeholder Details

**System Info** -- Dynamic context (time, timezone, locale, app context, vocabulary) is NOT in the templates. It is returned separately by `PromptBuilder.systemInfo()` and prepended to the user message at call time. This enables prompt caching — the static system prompt stays identical across requests.

**`{{CONTEXTUAL_FORMATTING_BLOCK}}`** -- Present when a preset supports app-aware behavior. Hyper uses the detected app type directly; explicit presets use the same shared blocks more narrowly so the selected preset remains the stronger signal. When no app-aware block applies, this placeholder is replaced with an empty string.

**`{{EMAIL_FORMATTING_RULES}}`** -- The contents of `fragments/email-formatting-rules.txt`, injected directly into the mail preset.

**`{{CUSTOM_INSTRUCTIONS}}`** -- The user's custom instruction text from their mode configuration. Falls back to "Process the text according to your best judgment." if empty.

## Fragment Files

Fragments are shared text blocks used by multiple presets or injected conditionally:

- **email-formatting-rules.txt** -- Email greeting/sign-off/formatting rules. Used by the email contextual block and the Mail preset (via `{{EMAIL_FORMATTING_RULES}}`).
- **anti-reply-directive.txt** -- Prevents the model from treating transcripts as conversation. Prepended to all presets by the prompt builder.
- **override-directive.txt** -- Allows user system prompts to override admin instructions. Prepended to all presets by the prompt builder.

## Contextual Blocks

Contextual blocks are selected by the macOS and Windows prompt builders after application classification:

- **email.txt** -- Hyper/app-aware email formatting without adding greetings or sign-offs unless dictated.
- **work-message.txt** -- Concise professional chat formatting for Slack, Teams, Intercom, etc.
- **personal-message.txt** -- Lightweight conversational formatting for personal messaging.
- **document.txt** -- Paragraph/list structure for documents and notes.
- **code.txt** -- Technical spelling and identifier preservation for code/editor contexts.
- **terminal.txt** -- Conservative command-safe formatting for terminals.

## How Prompts Are Assembled

The final system prompt is assembled by the platform-specific prompt builder in this order:

1. `fragments/override-directive.txt`
2. `fragments/anti-reply-directive.txt`
3. Newline
4. Preset template (with placeholders substituted)
5. Mode flags (`<MODE_FLAGS>` block for punctuation, capitalization, profanity filter)
6. User system prompt (if set, wrapped in `<USER_SYSTEM_PROMPT>` tags)
