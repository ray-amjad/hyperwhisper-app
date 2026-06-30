# Localization Messages

## JSON Syntax Rules

**Never use curly quotes in JSON strings.** They break JSON parsing.

| Bad | Good |
|-----|------|
| `"下载"` (curly quotes) | `「下载」` (corner brackets) |
| `"text"` | `「text」` or `'text'` |

Use corner brackets `「」` for Chinese/Japanese or straight quotes `'` for other languages.
