When dealing with Deepgram vocabulary boosting method based on the language parameter:

| Language Setting | Deepgram Parameter | Description |
|-----------------|-------------------|-------------|
| Specific (e.g., `en`, `ja`) | `keyterm` | **Monolingual mode**: Up to 90% improvement in Keyword Recall Rate (KRR). Best accuracy for named entities, product names, and industry jargon. |
| `auto` or not specified | None | **Multilingual mode**: No vocabulary support for Nova-3. The `keywords` parameter is rejected by Nova-3, and `keyterm` is silently ignored when using `detect_language=true`. |

**Important: Nova-3 Limitations:**
- Nova-3 ONLY supports the `keyterm` parameter (does NOT support `keywords`)
- `keyterm` only works when language is explicitly specified (monolingual transcription)
- When using auto-detect (`language=auto`), vocabulary boosting is not available
- Nova-2, Nova-1, and Enhanced models support `keywords` for multilingual mode

**Implementation Details:**
- Client sends vocabulary terms as comma-separated string via `initial_prompt` field
- Backend appends ONE repeated query value per term — `keyterm=a&keyterm=b` (and `keywords=a&keywords=b` on Nova-2). NOT a single comma-joined value. `keyterm` does NOT accept a `:boost` suffix (that's the legacy Nova-2 `keywords` syntax); send the bare term.
- Maximum 100 terms per request (enforced at both client and backend)
- `keyterm` is used when language is explicitly specified with Nova-3 (monolingual transcription)
- No vocabulary parameter is used when language is "auto" (Nova-3 doesn't support `keywords`, and `keyterm` is ignored with `detect_language=true`)