import { getCloudflareContext } from "@opennextjs/cloudflare";

const USER_AGENT = "HyperWhisper-ModelSync/1.0";

const OPENAI_EXCLUDE = [
  "whisper", "tts", "dall-e", "embedding", "moderation", "davinci",
  "babbage", "realtime", "audio", "computer-use", "codex", "chatgpt",
  "search", "transcribe", "gpt-image", "sora", "o1", "o3", "o4-mini",
  "gpt-3.5", "gpt-4-", "gpt-4o", "gpt-4-turbo", "instruct", "-pro",
];
const GROQ_EXCLUDE = [
  "whisper", "tts", "distil", "tool-use", "guard", "safeguard",
  "orpheus", "allam", "prompt-guard",
];
const GEMINI_EXCLUDE = [
  "imagen", "tts", "embedding", "aqa", "gemma", "learnlm",
  "robotics", "computer-use", "deep-research", "image",
  "nano-banana", "exp-",
];
const XAI_EXCLUDE = ["image", "embedding", "tts", "audio", "vision"];

export type Model = { id: string; display_name: string };
export type ProviderResult = { ok: true; models: Model[] } | { ok: false; error: string };

/** One authenticated GET against a provider's model endpoint. */
type ApiGet = (url: string, headers?: Record<string, string>) => Promise<unknown>;

type BasicModelResponse = { data: Array<{ id: string }> };
type AnthropicResponse = {
  data: Array<{ id: string; display_name?: string }>;
  has_more?: boolean;
};
type GeminiResponse = {
  models: Array<{
    name: string;
    displayName?: string;
    supportedGenerationMethods?: string[];
  }>;
  nextPageToken?: string;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isUnknownArray(value: unknown): value is unknown[] {
  return Array.isArray(value);
}

function isStringArray(value: unknown): value is string[] {
  return isUnknownArray(value) && value.every((item) => typeof item === "string");
}

function optionalString(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  if (value === undefined) return undefined;
  if (typeof value !== "string") throw new Error("Invalid provider response");
  return value;
}

function parseBasicModelResponse(value: unknown): BasicModelResponse {
  if (!isRecord(value)) throw new Error("Invalid provider response");
  if (value.data === undefined) return { data: [] };
  if (!isUnknownArray(value.data)) throw new Error("Invalid provider response");

  return {
    data: value.data.map((item) => {
      if (!isRecord(item) || typeof item.id !== "string") {
        throw new Error("Invalid provider response");
      }
      return { id: item.id };
    }),
  };
}

function parseAnthropicResponse(value: unknown): AnthropicResponse {
  if (!isRecord(value)) throw new Error("Invalid provider response");
  if (value.data !== undefined && !isUnknownArray(value.data)) {
    throw new Error("Invalid provider response");
  }
  if (value.has_more !== undefined && typeof value.has_more !== "boolean") {
    throw new Error("Invalid provider response");
  }

  const data = (value.data ?? []).map((item) => {
    if (!isRecord(item) || typeof item.id !== "string") {
      throw new Error("Invalid provider response");
    }
    return { id: item.id, display_name: optionalString(item, "display_name") };
  });

  return { data, has_more: value.has_more };
}

function parseGeminiResponse(value: unknown): GeminiResponse {
  if (!isRecord(value)) throw new Error("Invalid provider response");
  if (value.models !== undefined && !isUnknownArray(value.models)) {
    throw new Error("Invalid provider response");
  }

  const models = (value.models ?? []).map((item) => {
    if (!isRecord(item) || typeof item.name !== "string") {
      throw new Error("Invalid provider response");
    }
    const methods = item.supportedGenerationMethods;
    if (
      methods !== undefined &&
      !isStringArray(methods)
    ) {
      throw new Error("Invalid provider response");
    }
    return {
      name: item.name,
      displayName: optionalString(item, "displayName"),
      supportedGenerationMethods: methods,
    };
  });

  return {
    models,
    nextPageToken: optionalString(value, "nextPageToken"),
  };
}

// A 403 body is read only to pull out its error code, so it is bounded: a
// provider error object is a few hundred bytes, and a body that is larger or
// slower than this is dropped rather than allowed to stall the fan-out.
const ERROR_BODY_MAX_BYTES = 8 * 1024;
const ERROR_BODY_TIMEOUT_MS = 2_000;
// What a logged field may look like: a short identifier such as
// `invalid_api_key` or `NRT`. Anything else is dropped, never truncated.
const SAFE_LOG_TOKEN = /^[A-Za-z0-9_.\-]{1,64}$/;

function safeLogToken(value: unknown): string | undefined {
  return typeof value === "string" && SAFE_LOG_TOKEN.test(value) ? value : undefined;
}

/**
 * The upstream body as text, or undefined when it is missing, larger than
 * ERROR_BODY_MAX_BYTES, slower than ERROR_BODY_TIMEOUT_MS, or unreadable.
 * Never throws.
 */
async function readBoundedText(res: Response): Promise<string | undefined> {
  const body = res.body;
  if (!body) return undefined;
  let reader: ReadableStreamDefaultReader<Uint8Array>;
  try {
    reader = body.getReader();
  } catch {
    return undefined;
  }
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timedOut = new Promise<"timeout">((resolve) => {
    timer = setTimeout(() => resolve("timeout"), ERROR_BODY_TIMEOUT_MS);
  });
  const decoder = new TextDecoder();
  let text = "";
  let size = 0;
  try {
    for (;;) {
      const next = await Promise.race([reader.read(), timedOut]);
      if (next === "timeout") return undefined;
      if (next.done) break;
      size += next.value.byteLength;
      if (size > ERROR_BODY_MAX_BYTES) return undefined;
      text += decoder.decode(next.value, { stream: true });
    }
    return text + decoder.decode();
  } catch {
    return undefined;
  } finally {
    clearTimeout(timer);
    // Release the connection; a stream that already ended or errored rejects.
    reader.cancel().catch(() => {});
  }
}

/**
 * The provider's error code from a JSON error body, e.g. `invalid_api_key` or
 * `unsupported_country_region_territory`. Only the code is returned, and only
 * when it is a short safe token: the body itself is never logged, because a
 * provider could echo the request URL or key back in it.
 *
 * Shapes: OpenAI / Groq / xAI `{ error: { code, type } }`, Anthropic
 * `{ type: "error", error: { type } }`, Gemini `{ error: { code: 403, status } }`
 * (its numeric code is skipped for `status`), Cerebras top-level `{ code, type }`.
 */
async function readErrorCode(res: Response): Promise<string | undefined> {
  const text = await readBoundedText(res);
  if (text === undefined) return undefined;
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (!isRecord(parsed)) return undefined;
  const nested = isRecord(parsed.error) ? parsed.error : {};
  for (const candidate of [nested.code, parsed.code, nested.type, nested.status, parsed.type]) {
    const code = safeLogToken(candidate);
    if (code) return code;
  }
  return undefined;
}

/**
 * A non-OK provider response. `message` is `HTTP <status>` and nothing else,
 * because the public /models route returns it to clients as `error`.
 * `diagnostics` holds the log-only fields (` colo=… code=…`).
 */
class UpstreamHttpError extends Error {
  constructor(
    status: number,
    readonly diagnostics: string,
  ) {
    super(`HTTP ${status}`);
    this.name = "UpstreamHttpError";
  }
}

/**
 * ` colo=<colo>` and, when known, ` code=<code>`: enough to tell a bad key
 * (`code=invalid_api_key` from every colo) from an egress block (a region code,
 * or several providers failing at once from one colo).
 */
function diagnosticFields(colo: string | undefined, code?: string): string {
  return ` colo=${colo ?? "unknown"}${code ? ` code=${code}` : ""}`;
}

function makeApiGet(
  fetchImpl: typeof fetch,
  readColo: () => string | undefined,
  redact: (text: string) => string,
): ApiGet {
  return async function apiGet(url, headers = {}) {
    const res = await fetchImpl(url, {
      headers: { "User-Agent": USER_AGENT, ...headers },
      cache: "no-store",
    });
    if (!res.ok) {
      // Log host + path + status only. Never log the full URL (the Gemini
      // endpoint embeds the API key in its query string) and never log the
      // upstream body (a provider could echo the request URL/key back in its
      // error payload). Clients receive only the status code, never the body.
      // A 403 is ambiguous (revoked key, or the Worker's egress blocked by
      // region), so for it alone the body's error code is logged (#3029).
      let target = "upstream";
      try {
        const { host, pathname } = new URL(url);
        target = `${host}${pathname}`;
      } catch {
        // keep generic label if url is unparseable
      }
      const code = res.status === 403 ? await readErrorCode(res) : undefined;
      const diagnostics = diagnosticFields(readColo(), code);
      console.error(redact(`[model-list] ${target} -> HTTP ${res.status}${diagnostics}`));
      throw new UpstreamHttpError(res.status, diagnostics);
    }
    return res.json();
  };
}

function sortModels(models: Model[]): Model[] {
  return [...models].sort((a, b) => a.id.localeCompare(b.id));
}

async function fetchOpenAI(key: string, apiGet: ApiGet): Promise<Model[]> {
  const data = parseBasicModelResponse(
    await apiGet("https://api.openai.com/v1/models", {
      Authorization: `Bearer ${key}`,
    }),
  );
  const models: Model[] = [];
  for (const m of data.data ?? []) {
    const id = m.id.toLowerCase();
    if (OPENAI_EXCLUDE.some((ex) => id.includes(ex))) continue;
    if (/-\d{4}-\d{2}-\d{2}$/.test(m.id)) continue;
    if (m.id.endsWith("-chat-latest")) continue;
    models.push({ id: m.id, display_name: m.id });
  }
  return sortModels(models);
}

async function fetchAnthropic(key: string, apiGet: ApiGet): Promise<Model[]> {
  const all: Model[] = [];
  let url: string | null = "https://api.anthropic.com/v1/models?limit=1000";
  const headers = { "x-api-key": key, "anthropic-version": "2023-06-01" };
  while (url) {
    const data = parseAnthropicResponse(await apiGet(url, headers));
    for (const m of data.data ?? []) {
      all.push({ id: m.id, display_name: m.display_name ?? m.id });
    }
    if (data.has_more && data.data?.length) {
      const lastId = data.data[data.data.length - 1].id;
      url = `https://api.anthropic.com/v1/models?limit=1000&after_id=${encodeURIComponent(lastId)}`;
    } else {
      url = null;
    }
  }
  return sortModels(all);
}

async function fetchGemini(key: string, apiGet: ApiGet): Promise<Model[]> {
  const all: Model[] = [];
  let url: string | null = `https://generativelanguage.googleapis.com/v1beta/models?key=${encodeURIComponent(key)}&pageSize=1000`;
  while (url) {
    const data = parseGeminiResponse(await apiGet(url));
    for (const m of data.models ?? []) {
      const methods = m.supportedGenerationMethods ?? [];
      if (!methods.includes("generateContent")) continue;
      const id = m.name.replace(/^models\//, "");
      const lower = id.toLowerCase();
      if (GEMINI_EXCLUDE.some((kw) => lower.includes(kw))) continue;
      if (/preview-\d{2}-\d{4}$/.test(id)) continue;
      if (id.endsWith("-001") || id.endsWith("-latest")) continue;
      all.push({ id, display_name: m.displayName ?? id });
    }
    url = data.nextPageToken
      ? `https://generativelanguage.googleapis.com/v1beta/models?key=${encodeURIComponent(key)}&pageSize=1000&pageToken=${encodeURIComponent(data.nextPageToken)}`
      : null;
  }
  return sortModels(all);
}

async function fetchGroq(key: string, apiGet: ApiGet): Promise<Model[]> {
  const data = parseBasicModelResponse(
    await apiGet("https://api.groq.com/openai/v1/models", {
      Authorization: `Bearer ${key}`,
    }),
  );
  const models: Model[] = [];
  for (const m of data.data ?? []) {
    if (GROQ_EXCLUDE.some((ex) => m.id.toLowerCase().includes(ex))) continue;
    models.push({ id: m.id, display_name: m.id });
  }
  return sortModels(models);
}

async function fetchXAI(key: string, apiGet: ApiGet): Promise<Model[]> {
  const data = parseBasicModelResponse(
    await apiGet("https://api.x.ai/v1/models", {
      Authorization: `Bearer ${key}`,
    }),
  );
  const models: Model[] = [];
  for (const m of data.data ?? []) {
    const id = m.id.toLowerCase();
    if (XAI_EXCLUDE.some((ex) => id.includes(ex))) continue;
    models.push({ id: m.id, display_name: m.id });
  }
  return sortModels(models);
}

async function fetchCerebras(key: string, apiGet: ApiGet): Promise<Model[]> {
  const data = parseBasicModelResponse(
    await apiGet("https://api.cerebras.ai/v1/models", {
      Authorization: `Bearer ${key}`,
    }),
  );
  return sortModels((data.data ?? []).map((m) => ({ id: m.id, display_name: m.id })));
}

const PROVIDERS = [
  { name: "openai",    envKey: "OPENAI_API_KEY",    fetcher: fetchOpenAI },
  { name: "anthropic", envKey: "ANTHROPIC_API_KEY", fetcher: fetchAnthropic },
  { name: "gemini",    envKey: "GEMINI_API_KEY",    fetcher: fetchGemini },
  { name: "groq",      envKey: "GROQ_API_KEY",      fetcher: fetchGroq },
  { name: "grok",      envKey: "XAI_API_KEY",       fetcher: fetchXAI },
  { name: "cerebras",  envKey: "CEREBRAS_API_KEY",  fetcher: fetchCerebras },
] as const;

export type AvailableModels = {
  fetchedAt: string;
  providers: Record<string, ProviderResult>;
};

// Per-instance in-memory cache (~1h TTL). Bounds the 6-provider fan-out even
// against cache-busting query strings, which bypass any edge cache and hit
// the function directly. In-flight de-duplication collapses concurrent
// cold-start requests into a single upstream fan-out.
const CACHE_TTL_MS = 60 * 60 * 1000;
// If every provider failed (e.g. a transient cold-start network blip), cache
// only briefly so a fully-broken response isn't pinned for the full hour.
const FAILURE_CACHE_TTL_MS = 60 * 1000;

/**
 * The four ambient dependencies of the model list: the HTTP client, the API
 * keys, the clock, and the Cloudflare colo the Worker runs in. Each one defaults
 * to the real thing, so production code calls `fetchAvailableModels()` with no
 * arguments and behaves as before.
 */
export type ModelListDeps = {
  fetch: typeof fetch;
  env: Record<string, string | undefined>;
  now: () => number;
  /** The colo (e.g. `NRT`) serving the current request; undefined when unknown. Log-only. */
  colo: () => string | undefined;
};

/**
 * `request.cf.colo` of the Worker request in progress. `getCloudflareContext()`
 * throws outside a Worker request (node tests, `next dev`), which reads as
 * unknown.
 */
function workerColo(): string | undefined {
  try {
    const cf = getCloudflareContext().cf as Record<string, unknown> | undefined;
    return typeof cf?.colo === "string" ? cf.colo : undefined;
  } catch {
    return undefined;
  }
}

/** Strip an API key from text, raw and URL-encoded (fetchGemini encodes it). */
function redactKey(text: string, key: string): string {
  for (const form of [key, encodeURIComponent(key)]) {
    if (form) text = text.split(form).join("[redacted]");
  }
  return text;
}

/** undici's dual-stack connect failure is an AggregateError; show its parts. */
function describeCause(cause: unknown): string {
  if (cause instanceof AggregateError && cause.errors.length > 0) {
    const parts = cause.errors.map((err) => (err instanceof Error ? err.message : String(err)));
    return `${String(cause)}: ${parts.join("; ")}`;
  }
  return String(cause);
}

export type ModelList = {
  fetchAvailableModels: () => Promise<AvailableModels>;
  fetchAvailableModelsUncached: () => Promise<AvailableModels>;
};

/**
 * Build a model list with its own cache and its own in-flight slot. The cache
 * and the de-duplication state live here, not on the module, so a test can hold
 * an instance whose state no other test can see.
 */
export function createModelList(deps: Partial<ModelListDeps> = {}): ModelList {
  // Read the global through a wrapper so a caller that swaps `globalThis.fetch`
  // after this instance is built still reaches the swapped one, as the direct
  // `fetch(...)` call this replaced did.
  const fetchImpl: typeof fetch = deps.fetch ?? ((...args) => globalThis.fetch(...args));
  const env = deps.env ?? process.env;
  const now = deps.now ?? Date.now;
  const coloSource = deps.colo ?? workerColo;
  // Diagnostics must never break the fan-out: a throwing or odd colo source,
  // or a value that is not a short token, reads as unknown.
  const readColo = (): string | undefined => {
    try {
      return safeLogToken(coloSource());
    } catch {
      return undefined;
    }
  };

  let cache: { value: AvailableModels; expiresAt: number } | null = null;
  let inFlight: Promise<AvailableModels> | null = null;

  async function fetchAvailableModelsUncached(): Promise<AvailableModels> {
    const entries = await Promise.all(
      PROVIDERS.map(async ({ name, envKey, fetcher }): Promise<[string, ProviderResult]> => {
        const key = env[envKey];
        if (!key) return [name, { ok: false, error: `missing ${envKey}` }];
        const redact = (text: string) => redactKey(text, key);
        try {
          const models = await fetcher(key, makeApiGet(fetchImpl, readColo, redact));
          return [name, { ok: true, models }];
        } catch (e) {
          // The Gemini URL carries the key in its query string, and an error
          // message can quote the URL or the upstream body. The error is also
          // returned to the public /models route, so redact both.
          const reason = redactKey(e instanceof Error ? e.message : String(e), key);
          // Node puts the real reason for a network failure in `cause`.
          const cause =
            e instanceof Error && e.cause ? ` (cause: ${describeCause(e.cause)})` : "";
          // Log-only colo/code fields, appended after the leading text the
          // Workers Observability query matches. Never part of `error`.
          const diagnostics =
            e instanceof UpstreamHttpError ? e.diagnostics : diagnosticFields(readColo());
          console.error(redact(`[model-list] ${name} fetch failed: ${reason}${cause}${diagnostics}`));
          return [name, { ok: false, error: reason }];
        }
      })
    );

    return {
      fetchedAt: new Date(now()).toISOString(),
      providers: Object.fromEntries(entries),
    };
  }

  async function fetchAvailableModels(): Promise<AvailableModels> {
    if (cache && cache.expiresAt > now()) return cache.value;
    if (inFlight) return inFlight;

    inFlight = (async () => {
      try {
        const value = await fetchAvailableModelsUncached();
        const anyOk = Object.values(value.providers).some((p) => p.ok);
        const ttl = anyOk ? CACHE_TTL_MS : FAILURE_CACHE_TTL_MS;
        cache = { value, expiresAt: now() + ttl };
        return value;
      } finally {
        inFlight = null;
      }
    })();

    return inFlight;
  }

  return { fetchAvailableModels, fetchAvailableModelsUncached };
}

// The process-wide instance the route handlers share. One per server instance,
// exactly as the module-level cache it replaced was.
const defaultModelList = createModelList();

export function fetchAvailableModels(): Promise<AvailableModels> {
  return defaultModelList.fetchAvailableModels();
}
