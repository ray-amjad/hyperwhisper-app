// The six LLM providers with an OpenAI-compatible `/chat/completions`
// endpoint, as rows of one table, and the one client that sends to any of them.

import { isRecord } from '../lib/utils';
import { isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import { computeLLMChatCost, llmModelRow, type LLMProvider } from '../lib/llm-models';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError, requireLLMApiKey } from './llm-errors';
import { readUpstreamError, upstreamErrorLogFields } from './upstream-error';
import { computeLLMRequestTimeoutMs, estimateMissingLLMUsage, fetchLLMWithTimeout, promptCharCount, readLLMJson, transcriptCharCount } from './llm-fetch';

export type OpenAICompatChatResult = { raw: unknown; usage?: GroqUsage; costUsd: number };

export type OpenAICompatProvider = Exclude<LLMProvider, 'anthropic'>;

type OpenAICompatAdapter = {
  baseUrl: string;
  /** Env vars holding the API key, first set one wins; the first is named when none is. */
  apiKeyEnv: [string, ...string[]];
  errorLogLabel: string;
  errorChatLabel: string;
  /**
   * Vendor fields added after the shared payload and the row's output cap
   * (`model` goes first, `stream: false` last).
   */
  extraBody?: (model: string) => Record<string, unknown>;
  /** Put `extraBody` right after `model` instead of after the payload (xAI's byte order before the table). */
  extraBodyFirst?: boolean;
};

// The 2.5-flash and 3.8-flash families default to a dynamic thinking budget and
// bill thinking tokens as output — wasted latency/cost for simple text cleanup —
// so each is sent the lowest reasoning_effort it accepts. Listed per family rather
// than matched on a generic /gemini-\d+.*flash/ so an id we have not checked (e.g.
// a future 3.x flash, or any -pro id) keeps its own default instead of silently
// inheriting one of these. First match wins, so keep a longer prefix above a
// shorter one it extends.
//
// - 2.5-flash: 'none' turns thinking off.
// - 3.8-flash: thinking cannot be turned off; its levels are low/medium/high
//   (https://ai.google.dev/gemini-api/docs/thinking, read 2026-10-07). Measured on
//   the OpenAI-compat surface 2026-10-15: 'minimal' is a 400 ("Thinking level
//   MINIMAL is not supported for this model"), while 'none' is a 200 that still
//   thinks — about the same hidden tokens as 'low'. So send 'low', the floor the
//   docs name, rather than rely on Google quietly remapping 'none'. A 400 here
//   would NOT fall back: shouldFallback() fails over on a 5xx or 429 only. This
//   matches the transcription client (the `thinking` field on the gemini rows
//   in stt-models.ts).
const GEMINI_REASONING_EFFORT_BY_PREFIX: ReadonlyArray<readonly [prefix: string, effort: string]> = [
  ['gemini-2.5-flash', 'none'],
  ['gemini-3.8-flash', 'low'],
];

const OPENAI_COMPAT_ADAPTERS: Record<OpenAICompatProvider, OpenAICompatAdapter> = {
  cerebras: {
    baseUrl: 'https://api.cerebras.ai/v1',
    apiKeyEnv: ['CEREBRAS_API_KEY'],
    errorLogLabel: 'Cerebras API',
    errorChatLabel: 'Cerebras chat',
    extraBody: () => ({ reasoning_effort: 'low' }),
  },
  groq: {
    baseUrl: 'https://api.groq.com/openai/v1',
    apiKeyEnv: ['GROQ_API_KEY'],
    errorLogLabel: 'Groq LLM API',
    errorChatLabel: 'Groq chat',
    // The output ceiling comes from its llm-models.ts row (maxOutputTokens).
    extraBody: () => ({ reasoning_effort: 'low' }),
  },
  grok: {
    baseUrl: 'https://api.x.ai/v1',
    apiKeyEnv: ['XAI_API_KEY', 'GROK_API_KEY'],
    errorLogLabel: 'SpaceXAI Grok API',
    errorChatLabel: 'SpaceXAI Grok chat',
    extraBody: () => ({ reasoning_effort: 'none' }),
    extraBodyFirst: true,
  },
  // GPT-5 family on /v1/chat/completions only supports the default
  // `temperature` (1), so `temperature: 0` errors and buildBody drops it.
  // `reasoning_effort: 'none'` is the lowest-latency setting gpt-5.6-luna accepts
  // (none|low|medium|high|xhigh|max, default medium; `'minimal'` is NOT accepted)
  // and keeps post-processing fast/cheap. Verified against the OpenAI model doc
  // 2026-09-25: https://developers.openai.com/api/docs/models/gpt-5.6-luna
  openai: {
    baseUrl: 'https://api.openai.com/v1',
    apiKeyEnv: ['OPENAI_API_KEY'],
    errorLogLabel: 'OpenAI API',
    errorChatLabel: 'OpenAI chat',
    extraBody: () => ({ reasoning_effort: 'none' }),
  },
  // Gemini exposes an OpenAI-compatible surface that accepts the shared chat
  // payload unchanged. Verified 2026-06-19.
  gemini: {
    baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai',
    apiKeyEnv: ['GEMINI_API_KEY', 'GOOGLE_GEMINI_API_KEY'],
    errorLogLabel: 'Gemini API',
    errorChatLabel: 'Gemini chat',
    extraBody: (model) => {
      const effort = GEMINI_REASONING_EFFORT_BY_PREFIX.find(([prefix]) => model.startsWith(prefix))?.[1];
      return effort ? { reasoning_effort: effort } : {};
    },
  },
  // Mistral's chat/completions accepts the shared chat payload unchanged.
  // Verified 2026-06-19.
  mistral: {
    baseUrl: 'https://api.mistral.ai/v1',
    apiKeyEnv: ['MISTRAL_API_KEY'],
    errorLogLabel: 'Mistral API',
    errorChatLabel: 'Mistral chat',
  },
};

/** The request body sent to `provider` for `model`. */
export function buildOpenAICompatBody(
  provider: OpenAICompatProvider,
  payload: CorrectionRequestPayload,
  model: string,
): Record<string, unknown> {
  const { temperature, ...withoutTemperature } = payload;
  const shared = provider === 'openai' ? withoutTemperature : payload;
  const { maxOutputTokens } = llmModelRow(provider, model);
  const cap = maxOutputTokens === undefined ? {} : { max_completion_tokens: maxOutputTokens };
  const adapter = OPENAI_COMPAT_ADAPTERS[provider];
  const extra = adapter.extraBody?.(model);
  return adapter.extraBodyFirst
    ? { model, ...extra, ...shared, ...cap, stream: false }
    : { model, ...shared, ...cap, ...extra, stream: false };
}

// A missing/unrecognized `usage` block is billed by the shared char-based
// estimate (estimateMissingLLMUsage in llm-fetch.ts), never at 0.
function reportMissingUsage(
  provider: string,
  payload: CorrectionRequestPayload,
  json: unknown,
  requestId: string
): GroqUsage {
  let completionChars = 0;
  if (isRecord(json)) {
    const choices = json['choices'];
    const message = Array.isArray(choices) && isRecord(choices[0]) ? choices[0]['message'] : undefined;
    const content = isRecord(message) ? message['content'] : undefined;
    completionChars = typeof content === 'string' ? content.length : JSON.stringify(json).length;
  }

  return estimateMissingLLMUsage(provider, promptCharCount(payload.messages), completionChars, requestId);
}

export async function requestOpenAICompatibleChat(
  provider: OpenAICompatProvider,
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string,
): Promise<OpenAICompatChatResult> {
  const config = OPENAI_COMPAT_ADAPTERS[provider];
  const apiKey = requireLLMApiKey(provider, ...config.apiKeyEnv);
  const { json, usage } = await fetchLLMWithTimeout(
    provider,
    `${config.baseUrl}/chat/completions`,
    {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${apiKey}`,
        'content-type': 'application/json',
      },
      body: JSON.stringify(buildOpenAICompatBody(provider, payload, model)),
    },
    async (response) => {
      if (!response.ok) {
        // Never the body: an LLM 400 can quote the transcript or prompt it
        // rejected (#2163). Status, error code/type and request id only.
        const { fields } = await readUpstreamError(response);
        console.error(`${config.errorLogLabel} returned error`, {
          requestId,
          ...upstreamErrorLogFields(fields),
        });
        throw new LLMRequestError(
          `${config.errorChatLabel} failed with status ${response.status}`,
          response.status,
          provider,
        );
      }

      const json = await readLLMJson(response, provider, requestId);
      const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
      return { json, usage };
    },
    requestId,
    computeLLMRequestTimeoutMs(transcriptCharCount(payload.messages)),
  );

  const costUsd = computeLLMChatCost(provider, model, usage ?? reportMissingUsage(provider, payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
