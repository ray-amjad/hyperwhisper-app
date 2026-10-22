/**
 * JSON for an inline `<script type="application/ld+json">`.
 *
 * JSON.stringify leaves `<` as is, so a value holding `</script>` (an Outrank
 * post title, say) would close the script element and run as page markup
 * (#2009). `<` is the same character to a JSON parser, and the HTML
 * parser never sees a `<` inside the script.
 */
export function serializeJsonLd(value: unknown): string {
  return JSON.stringify(value).replace(/</g, "\\u003c");
}
