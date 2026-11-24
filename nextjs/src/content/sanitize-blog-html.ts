// Outrank article HTML is third-party content rendered with
// dangerouslySetInnerHTML on www.hyperwhisper.com, so it passes this
// allow-list before it is stored (the webhook) and again before it is rendered
// (getBlogPost), which also covers rows stored before the sanitiser (#2009).
//
// Server-only by use: the webhook route and src/content/blog.ts (which imports
// "server-only") are its only callers. It does not import "server-only"
// itself, because the webhook tests load the real route under node --test,
// where that package does not resolve.
import sanitizeHtml from "sanitize-html";

/** An iframe survives only when its src starts with one of these (Ray, #2009). */
const IFRAME_SRC_PREFIXES = [
  "https://www.youtube.com/embed/",
  "https://player.vimeo.com/",
] as const;

/**
 * The accessible name an iframe gets when Outrank sends it without a `title`
 * (#2953: axe frame-title, WCAG 4.1.2). A non-blank title it does send wins.
 */
const IFRAME_TITLES: Record<(typeof IFRAME_SRC_PREFIXES)[number], string> = {
  "https://www.youtube.com/embed/": "YouTube video",
  "https://player.vimeo.com/": "Vimeo video",
};

const ARTICLE_TAGS = [
  "h1",
  "h2",
  "h3",
  "h4",
  "h5",
  "h6",
  "p",
  "br",
  "hr",
  "div",
  "span",
  "section",
  "article",
  "header",
  "footer",
  "aside",
  "a",
  "strong",
  "b",
  "em",
  "i",
  "u",
  "s",
  "del",
  "ins",
  "mark",
  "small",
  "sub",
  "sup",
  "abbr",
  "cite",
  "q",
  "kbd",
  "samp",
  "var",
  "time",
  "code",
  "pre",
  "blockquote",
  "ul",
  "ol",
  "li",
  "dl",
  "dt",
  "dd",
  "table",
  "caption",
  "colgroup",
  "col",
  "thead",
  "tbody",
  "tfoot",
  "tr",
  "th",
  "td",
  "figure",
  "figcaption",
  "picture",
  "source",
  "img",
  "iframe",
  "details",
  "summary",
];

const HEADING_ATTRIBUTES = ["id"];

const OPTIONS: sanitizeHtml.IOptions = {
  allowedTags: ARTICLE_TAGS,
  // No `style`, no `class` and no `on*` anywhere: only what is listed here.
  allowedAttributes: {
    a: ["href", "title", "target", "rel", "name", "id"],
    img: [
      "src",
      "srcset",
      "sizes",
      "alt",
      "title",
      "width",
      "height",
      "loading",
      "decoding",
    ],
    source: ["srcset", "sizes", "type", "media", "width", "height"],
    iframe: [
      "src",
      "width",
      "height",
      "title",
      "allow",
      "allowfullscreen",
      "frameborder",
      "loading",
      "referrerpolicy",
    ],
    h1: HEADING_ATTRIBUTES,
    h2: HEADING_ATTRIBUTES,
    h3: HEADING_ATTRIBUTES,
    h4: HEADING_ATTRIBUTES,
    h5: HEADING_ATTRIBUTES,
    h6: HEADING_ATTRIBUTES,
    ol: ["start", "reversed", "type"],
    li: ["value"],
    th: ["colspan", "rowspan", "scope", "abbr", "align"],
    td: ["colspan", "rowspan", "headers", "align"],
    col: ["span"],
    colgroup: ["span"],
    time: ["datetime"],
    abbr: ["title"],
    q: ["cite"],
    blockquote: ["cite"],
    del: ["datetime"],
    ins: ["datetime"],
    details: ["open"],
  },
  // A relative URL or an #anchor has no scheme and is always kept. Every
  // other scheme (javascript:, data:, vbscript:, ...) is dropped.
  allowedSchemes: ["https", "http", "mailto"],
  allowedSchemesByTag: {
    img: ["https", "http"],
    source: ["https", "http"],
    iframe: ["https"],
  },
  allowedSchemesAppliedToAttributes: ["href", "src", "cite", "srcset"],
  allowProtocolRelative: false,
  allowedIframeHostnames: ["www.youtube.com", "player.vimeo.com"],
  allowIframeRelativeUrls: false,
  // Runs before exclusiveFilter, which still removes a disallowed iframe whole.
  transformTags: { iframe: addIframeTitle },
  // An iframe whose src is not one of the exact prefixes is removed whole,
  // not left behind as an empty frame.
  exclusiveFilter: (frame) =>
    frame.tag === "iframe" && !isAllowedIframeSrc(frame.attribs.src),
  // script, style, textarea, noscript and option are dropped with their
  // contents; any other unlisted tag is dropped but keeps its text.
  disallowedTagsMode: "discard",
};

function isAllowedIframeSrc(src: string | undefined): boolean {
  if (!src) return false;

  return IFRAME_SRC_PREFIXES.some((prefix) => src.startsWith(prefix));
}

function addIframeTitle(
  tagName: string,
  attribs: sanitizeHtml.Attributes,
): sanitizeHtml.Tag {
  if (attribs.title?.trim()) return { tagName, attribs };

  const src = attribs.src;
  const prefix = src
    ? IFRAME_SRC_PREFIXES.find((candidate) => src.startsWith(candidate))
    : undefined;

  if (!prefix) return { tagName, attribs };

  return { tagName, attribs: { ...attribs, title: IFRAME_TITLES[prefix] } };
}

/** Returns `html` with only the article allow-list above left in it. */
export function sanitizeBlogHtml(html: string): string {
  return sanitizeHtml(html, OPTIONS);
}
