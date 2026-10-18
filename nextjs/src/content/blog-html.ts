// Stored blog bodies arrive from the Outrank webhook as raw HTML, so their
// <img> tags carry no loading hint and every picture is fetched on page load,
// even the ones far below the fold (#919). Add `loading="lazy"` and
// `decoding="async"` at read time, so already-stored posts need no backfill.
// An attribute the HTML already sets is left as it is.

const IMG_TAG = /<img\b((?:[^>"']|"[^"]*"|'[^']*')*)>/gi;
const ATTRIBUTE =
  /\s*([^\s"'=<>/`]+)(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?/g;

function attributeNames(attributes: string): Set<string> {
  const names = new Set<string>();

  attributes.replace(ATTRIBUTE, (_attribute, name: string) => {
    names.add(name.toLowerCase());

    return "";
  });

  return names;
}

export function lazyLoadBodyImages(html: string): string {
  return html.replace(IMG_TAG, (tag, attributes: string) => {
    const names = attributeNames(attributes);
    const added = [
      names.has("loading") ? "" : ' loading="lazy"',
      names.has("decoding") ? "" : ' decoding="async"',
    ].join("");

    if (!added) return tag;
    // A trailing "/" closes the tag only after a space or a quote; in
    // `src=https://a/b/` it is part of the unquoted value.
    const selfClosing = /\s*(?<![^\s"'])\/\s*$/.exec(attributes);
    const body = selfClosing
      ? attributes.slice(0, selfClosing.index)
      : attributes;

    return `<img${body}${added}${selfClosing ? " /" : ""}>`;
  });
}
