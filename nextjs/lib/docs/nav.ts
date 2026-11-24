// The ordered docs tree: the one source for the sidebar, the prev/next links,
// the breadcrumbs, generateStaticParams, the sitemaps and /docs/llms.txt.
// Adding a page means adding its .mdx under src/content/docs and one entry
// here; tests/docs-content.test.ts fails when the two disagree.
//
// The tree is the old Mintlify docs.json navigation, nested groups included.
// Its "Local API Reference" group also listed one generated page per
// operation in public/docs/api-reference/local-api/openapi.yaml; those are the
// `endpoint` entries, at the same /docs/api-reference/<slug> paths Mintlify
// gave them (tests/docs-old-urls.test.ts holds every old URL).
//
// Pure data and string helpers only: client components and the node tests
// import this file.

export type DocPage = {
  kind: "page";
  /** Path under /docs, and the .mdx path under src/content/docs. */
  slug: string;
  title: string;
  href: string;
};

export type DocEndpoint = {
  kind: "endpoint";
  slug: string;
  /** The operation's `summary` in openapi.yaml. */
  title: string;
  href: string;
  method: "GET" | "POST" | "PATCH" | "DELETE" | "PUT";
  /** The OpenAPI path, e.g. `/modes/{id}`. */
  path: string;
};

export type DocEntry = DocPage | DocEndpoint;
export type DocGroup = { kind: "group"; title: string; items: DocNode[] };
export type DocNode = DocEntry | DocGroup;

export const DOCS_BASE = "/docs";

export function docHref(slug: string): string {
  return slug === "index" ? DOCS_BASE : `${DOCS_BASE}/${slug}`;
}

/**
 * Mintlify's slug for a generated endpoint page: the summary, lower-cased,
 * with everything but letters, digits, spaces, `+` and `-` dropped and each
 * space turned into `-`. "Server + provider snapshot" is
 * `server-+-provider-snapshot`, which is the live URL.
 */
export function endpointSlug(summary: string): string {
  return summary
    .toLowerCase()
    .replace(/[^a-z0-9 +-]/g, "")
    .trim()
    .replace(/\s+/g, "-");
}

function page(slug: string, title: string): DocPage {
  return { kind: "page", slug, title, href: docHref(slug) };
}

function endpoint(
  method: DocEndpoint["method"],
  path: string,
  summary: string,
): DocEndpoint {
  const slug = `api-reference/${endpointSlug(summary)}`;

  return { kind: "endpoint", slug, title: summary, href: docHref(slug), method, path };
}

function group(title: string, items: DocNode[]): DocGroup {
  return { kind: "group", title, items };
}

export const DOCS_NAV: DocGroup[] = [
  group("Getting Started", [
    page("index", "Introduction"),
    page("system-requirements", "System Requirements"),
    page("linux-installation", "Install HyperWhisper on Linux"),
    page("macos-first-run", "macOS First Run"),
    page("permissions", "macOS Permissions"),
    page("best-practices", "Best Practices"),
  ]),
  group("Plans & Billing", [
    page("licensing", "Pricing & Cloud Credits"),
    page("cloud-credits", "HyperWhisper Cloud Credits"),
    page("going-open-source", "Going Open Source"),
  ]),
  group("Transcription & Models", [
    page("transcription-modes", "Transcription Modes"),
    page("models", "Models"),
    page("choosing-a-provider", "Providers"),
    page("api-keys", "API Keys"),
    page("custom-endpoints", "Custom OpenAI-Compatible Endpoints"),
    page("language-detection", "Language & Detection"),
  ]),
  group("The Interface", [
    page("recording-window", "The Recording Window"),
    page("menu-bar", "Menu Bar & Tray Controls"),
    page("history", "Viewing Transcription History"),
  ]),
  group("Features", [
    page("keyboard-shortcuts", "Keyboard Shortcuts & Accessibility"),
    page("auto-paste", "Auto Paste & Clipboard Behavior"),
    page("file-transcription", "Transcribe a File"),
    page("voice-commands", "Voice Commands"),
    page("voice-coding", "Voice Coding in AI IDEs"),
    page("streaming-settings", "Streaming Transcription"),
    group("Audio Input", [
      page("audio-input", "Audio Input Volume"),
      page("microphone-selection", "Select a Microphone or Audio Input Device"),
      page("keep-microphone-warm", "Keep Microphone Warm"),
      page("voice-activity-detection", "Voice Activity Detection & Silence Trimming"),
      page("system-audio", "Transcribing System Audio"),
    ]),
    group("Sounds & Media", [
      page("sound-effects", "Sound Effects"),
      page("media-control", "Mute Audio While Recording"),
    ]),
    group("Vocabulary", [
      page("vocabulary", "Custom Vocabulary"),
      page("vocabulary-import-export", "Import & Export Vocabulary"),
      page("vocabulary-cloud-sync", "Cloud Sync Vocabulary Across Devices"),
    ]),
  ]),
  group("Settings", [
    page("general-settings", "General Settings"),
    page("output-settings", "Text Output & Formatting"),
    page("appearance-settings", "Appearance Settings"),
    page("storage-settings", "Storage Settings"),
    page("advanced-settings", "Advanced Settings"),
    page("backup-export-settings", "Backup & Export Settings"),
  ]),
  group("Privacy & Security", [
    page("data-privacy", "Data Privacy"),
    page("network-filtering", "Network Filtering"),
  ]),
  group("Developer & API", [
    page("local-api", "Local API Server"),
    page("developer-mode", "Developer Mode"),
    group("Local API Reference", [
      page("api-reference/local-api/overview", "Local API Overview"),
      page("api-reference/local-api/mcp-setup", "MCP Setup"),
      endpoint("GET", "/health", "Server + provider snapshot"),
      endpoint("GET", "/models", "List models"),
      endpoint("GET", "/modes", "List saved Modes"),
      endpoint("POST", "/modes", "Create a new Mode"),
      endpoint("GET", "/modes/{id}", "Fetch one Mode"),
      endpoint("PATCH", "/modes/{id}", "Partial-field update"),
      endpoint("DELETE", "/modes/{id}", "Delete a Mode"),
      endpoint("POST", "/transcribe", "Transcribe audio"),
      endpoint("POST", "/post-process", "Rewrite text with AI post-processing"),
      endpoint("GET", "/recordings/search", "Search past transcripts"),
      endpoint("GET", "/recordings", "Search past transcripts (alias)"),
      endpoint("GET", "/recordings/{id}", "Fetch one recording"),
      endpoint("POST", "/recording/toggle", "Start the microphone, or stop it and transcribe"),
      endpoint("POST", "/recording/cancel", "Stop the microphone and keep no transcript"),
    ]),
  ]),
  group("Troubleshooting", [
    page("troubleshooting", "Troubleshooting"),
    page("hallucinations", "Fixing Transcription Hallucinations"),
    page("performance-tips", "Performance Tips"),
    page("provider-health", "Provider Health & Automatic Failover"),
    page("updates", "App Updates"),
    page("support", "Support & Refunds"),
  ]),
  group("Windows", [
    page("windows-getting-started", "Windows Getting Started"),
    page("windows-permissions", "Windows Permissions"),
    page("windows/faq", "Windows FAQ"),
  ]),
];

function flatten(nodes: DocNode[], trail: string[], out: DocLocated[]) {
  for (const node of nodes) {
    if (node.kind === "group") flatten(node.items, [...trail, node.title], out);
    else out.push({ ...node, groups: trail });
  }

  return out;
}

/** An entry with the titles of the groups it sits in, outermost first. */
export type DocLocated = DocEntry & { groups: string[] };

/** Every page and endpoint, in sidebar order. */
export const DOCS_ENTRIES: DocLocated[] = flatten(DOCS_NAV, [], []);

/** Old docs URLs that now live elsewhere (docs.json `redirects`). */
export const DOCS_REDIRECTS: { source: string; destination: string }[] = [
  { source: "/docs/model-management", destination: "/docs/models" },
];

export function normalizeDocsPath(pathname: string): string {
  const trimmed = pathname.replace(/\/+$/, "");

  return trimmed === "" ? "/" : trimmed;
}

export function findDocBySlug(slug: string): DocLocated | undefined {
  return DOCS_ENTRIES.find((entry) => entry.slug === slug);
}

export function findDocByPath(pathname: string): DocLocated | undefined {
  const path = normalizeDocsPath(pathname);

  // A browser sends `+` as is, but an encoded `%2B` must find the page too.
  let decoded = path;

  try {
    decoded = decodeURIComponent(path);
  } catch {
    // Malformed escapes keep the raw path.
  }

  return DOCS_ENTRIES.find((entry) => entry.href === decoded);
}

export function prevNext(slug: string): {
  prev?: DocLocated;
  next?: DocLocated;
} {
  const i = DOCS_ENTRIES.findIndex((entry) => entry.slug === slug);

  if (i === -1) return {};

  return { prev: DOCS_ENTRIES[i - 1], next: DOCS_ENTRIES[i + 1] };
}

/** The slug of an optional catch-all route's segments (`[]` is the index). */
export function slugFromSegments(segments: string[] | undefined): string {
  if (!segments || segments.length === 0) return "index";

  return segments
    .map((segment) => {
      try {
        return decodeURIComponent(segment);
      } catch {
        return segment;
      }
    })
    .join("/");
}
