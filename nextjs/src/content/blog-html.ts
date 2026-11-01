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

// Outrank's comparison tables arrive as a bare <table>, and on a phone a wide
// one pushed the whole page sideways (#2112). Wrap each table in its own
// horizontal scroller at read time, so stored posts need no rewrite. A CSS
// rule alone (`display: block` on the table) would shrink narrow tables to
// their content and lose the table role in some screen readers. The wrapper
// is a focusable region so a keyboard can scroll it too. It takes over the
// table's 2em margin (28 px at the table's 0.875em), because a scroll container
// stops the table's own margin collapsing with the paragraphs around it.
//
// The table also keeps a 36rem floor. Without it, a 4-column table squeezed
// itself into a 342 px phone column: the scroller had about 30 px to give,
// the last column was cut mid-word ("Traditio…", "$1,5…"), and a swipe
// seemed to do nothing. With the floor the columns stay readable and the
// table scrolls sideways on a phone. The 780 px desktop column is wider than
// 36rem, so on desktop the table still spans the column as before.
const TABLE_OPEN = /<table\b/gi;
const TABLE_CLOSE = /<\/table\s*>/gi;
const TABLE_SCROLLER =
  '<div class="my-[1.75em] overflow-x-auto [&>table]:my-0 [&>table]:min-w-[36rem]" role="region" aria-label="Table" tabindex="0">';

export function scrollableBodyTables(html: string): string {
  return html
    .replace(TABLE_OPEN, (tag) => `${TABLE_SCROLLER}${tag}`)
    .replace(TABLE_CLOSE, (tag) => `${tag}</div>`);
}
