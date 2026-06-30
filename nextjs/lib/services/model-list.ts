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

async function apiGet(url: string, headers: Record<string, string> = {}): Promise<unknown> {
  const res = await fetch(url, {
    headers: { "User-Agent": USER_AGENT, ...headers },
    cache: "no-store",
  });
  if (!res.ok) {
    // Log host + path + status only. Never log the full URL (the Gemini
    // endpoint embeds the API key in its query string) and never log the
    // upstream body (a provider could echo the request URL/key back in its
    // error payload). Clients receive only the status code, never the body.
    let target = "upstream";
    try {
      const { host, pathname } = new URL(url);
      target = `${host}${pathname}`;
    } catch {
      // keep generic label if url is unparseable
    }
    console.error(`[model-list] ${target} -> HTTP ${res.status}`);
    throw new Error(`HTTP ${res.status}`);
  }
  return res.json();
}

function sortModels(models: Model[]): Model[] {
  return [...models].sort((a, b) => a.id.localeCompare(b.id));
}

async function fetchOpenAI(key: string): Promise<Model[]> {
  const data = (await apiGet("https://api.openai.com/v1/models", {
    Authorization: `Bearer ${key}`,
  })) as { data?: Array<{ id: string }> };
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

async function fetchAnthropic(key: string): Promise<Model[]> {
  const all: Model[] = [];
  let url: string | null = "https://api.anthropic.com/v1/models?limit=1000";
  const headers = { "x-api-key": key, "anthropic-version": "2023-06-01" };
  while (url) {
    const data = (await apiGet(url, headers)) as {
      data?: Array<{ id: string; display_name?: string }>;
      has_more?: boolean;
    };
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

async function fetchGemini(key: string): Promise<Model[]> {
  const all: Model[] = [];
  let url: string | null = `https://generativelanguage.googleapis.com/v1beta/models?key=${encodeURIComponent(key)}&pageSize=1000`;
  while (url) {
    const data = (await apiGet(url)) as {
      models?: Array<{ name: string; displayName?: string; supportedGenerationMethods?: string[] }>;
      nextPageToken?: string;
    };
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

async function fetchGroq(key: string): Promise<Model[]> {
  const data = (await apiGet("https://api.groq.com/openai/v1/models", {
    Authorization: `Bearer ${key}`,
  })) as { data?: Array<{ id: string }> };
  const models: Model[] = [];
  for (const m of data.data ?? []) {
    if (GROQ_EXCLUDE.some((ex) => m.id.toLowerCase().includes(ex))) continue;
    models.push({ id: m.id, display_name: m.id });
  }
  return sortModels(models);
}

async function fetchXAI(key: string): Promise<Model[]> {
  const data = (await apiGet("https://api.x.ai/v1/models", {
    Authorization: `Bearer ${key}`,
  })) as { data?: Array<{ id: string }> };
  const models: Model[] = [];
  for (const m of data.data ?? []) {
    const id = m.id.toLowerCase();
    if (XAI_EXCLUDE.some((ex) => id.includes(ex))) continue;
    models.push({ id: m.id, display_name: m.id });
  }
  return sortModels(models);
}

async function fetchCerebras(key: string): Promise<Model[]> {
  const data = (await apiGet("https://api.cerebras.ai/v1/models", {
    Authorization: `Bearer ${key}`,
  })) as { data?: Array<{ id: string }> };
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

async function fetchAvailableModelsUncached(): Promise<AvailableModels> {
  const entries = await Promise.all(
    PROVIDERS.map(async ({ name, envKey, fetcher }): Promise<[string, ProviderResult]> => {
      const key = process.env[envKey];
      if (!key) return [name, { ok: false, error: `missing ${envKey}` }];
      try {
        const models = await fetcher(key);
        return [name, { ok: true, models }];
      } catch (e) {
        return [name, { ok: false, error: e instanceof Error ? e.message : String(e) }];
      }
    })
  );

  return {
    fetchedAt: new Date().toISOString(),
    providers: Object.fromEntries(entries),
  };
}

// Per-instance in-memory cache (~1h TTL). Bounds the 6-provider fan-out even
// against cache-busting query strings, which bypass Vercel's edge cache and
// hit the function directly. In-flight de-duplication collapses concurrent
// cold-start requests into a single upstream fan-out.
const CACHE_TTL_MS = 60 * 60 * 1000;
// If every provider failed (e.g. a transient cold-start network blip), cache
// only briefly so a fully-broken response isn't pinned for the full hour.
const FAILURE_CACHE_TTL_MS = 60 * 1000;
let cache: { value: AvailableModels; expiresAt: number } | null = null;
let inFlight: Promise<AvailableModels> | null = null;

export async function fetchAvailableModels(): Promise<AvailableModels> {
  if (cache && cache.expiresAt > Date.now()) return cache.value;
  if (inFlight) return inFlight;

  inFlight = (async () => {
    try {
      const value = await fetchAvailableModelsUncached();
      const anyOk = Object.values(value.providers).some((p) => p.ok);
      const ttl = anyOk ? CACHE_TTL_MS : FAILURE_CACHE_TTL_MS;
      cache = { value, expiresAt: Date.now() + ttl };
      return value;
    } finally {
      inFlight = null;
    }
  })();

  return inFlight;
}
