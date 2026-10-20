// UPSTREAM ERROR FIELDS — WHAT A LOG LINE MAY SAY ABOUT A PROVIDER'S ERROR
//
// A provider's error body can quote the request it rejected: the user's
// transcript, the post-processing prompt, or vocabulary terms (a Chirp phrase-
// set 400, an LLM 400 that echoes the message). Every log line in this service
// ships to Axiom, so no part of an upstream error body may reach `console.*`
// or `logEvent`, directly or inside an Error message that a route logs later
// (#2163).
//
// This module turns a non-OK upstream response into content-free fields:
// the HTTP status, the provider's own error code / type when it is a short
// token-like identifier, the upstream request id from the response headers,
// and the body length. Log those. The raw body never leaves this module except
// as `classifierText`, which callers may test with a regex (quota markers, the
// Chirp adaptation retry) but must never log or put in an Error message that
// is logged.
//
// Pure apart from reading the response body once. No imports.

/** The 500-character truncation the old error-body preview used, so classifiers see the same text. */
const CLASSIFIER_TEXT_LIMIT = 500;

/**
 * An identifier a vendor puts in a `code` / `type` / `status` field:
 * `invalid_request_error`, `INVALID_ARGUMENT`, `quota_exceeded`, `400`.
 * No whitespace, so no prose; at most 64 characters, so no sentence fragment.
 * Anything else is dropped rather than truncated.
 */
const SAFE_TOKEN = /^[A-Za-z0-9_.:-]{1,64}$/;

/** A request id is an opaque token too, but some vendors' run longer. */
const SAFE_REQUEST_ID = /^[A-Za-z0-9_.:-]{1,128}$/;

/**
 * Response headers that carry the upstream's own id for the call, in the
 * order they are tried. What an operator hands vendor support.
 */
const REQUEST_ID_HEADERS = [
  'x-request-id', // OpenAI, Groq, Cerebras, Mistral, xAI, Soniox, many others
  'request-id', // Anthropic
  'dg-request-id', // Deepgram
  'apim-request-id', // Azure
  'x-requestid',
  'x-goog-request-id',
  'x-amzn-requestid',
  'cf-ray',
] as const;

/** Content-free facts about one non-OK upstream response. Safe to log. */
export interface UpstreamErrorFields {
  status: number;
  /** The provider's error code, when it is a short identifier. */
  errorCode?: string;
  /** The provider's error type / status enum, when it is a short identifier. */
  errorType?: string;
  /** The upstream's id for the call, from the response headers or body. */
  upstreamRequestId?: string;
  /** Characters in the error body. `-1` when the body could not be read. */
  bodyLength: number;
}

export interface UpstreamErrorRead {
  /** Content-free; log these. */
  fields: UpstreamErrorFields;
  /**
   * The first 500 characters of the body, or `<unreadable>`. For regex
   * classification ONLY (quota markers, the Chirp adaptation retry, the
   * Gemini 400-means-bad-key test). Never log it and never put it in an Error
   * message: a route logs every error message it catches.
   */
  classifierText: string;
}

/** `value` when it is a short token-like identifier, else undefined. */
export function safeErrorToken(value: unknown, pattern: RegExp = SAFE_TOKEN): string | undefined {
  if (typeof value === 'number' && Number.isInteger(value)) {
    value = String(value);
  }
  return typeof value === 'string' && pattern.test(value) ? value : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function firstToken(candidates: unknown[], pattern: RegExp = SAFE_TOKEN): string | undefined {
  for (const candidate of candidates) {
    const token = safeErrorToken(candidate, pattern);
    if (token !== undefined) return token;
  }
  return undefined;
}

/**
 * The content-free fields of an upstream error, from its status, headers and
 * body text. The body is parsed as JSON only to pick out the vendors' known
 * identifier fields; free-text fields (`message`, `err_msg`, `detail` as a
 * string, `error` as a sentence) are never read into the result.
 *
 * Shapes covered: OpenAI-compatible and Mistral
 * (`{error:{type,code}}` / `{type,code}`), Anthropic (`{type:'error',error:{type}}`),
 * Google (`{error:{code,status}}`), Azure (`{error:{code}}`), Deepgram
 * (`{err_code,request_id}`), ElevenLabs (`{detail:{code,status}}`), Soniox
 * (`{error_type,request_id}`).
 */
export function summarizeUpstreamError(
  status: number,
  headers: Headers | undefined,
  body: string | undefined,
): UpstreamErrorFields {
  const fields: UpstreamErrorFields = { status, bodyLength: body === undefined ? -1 : body.length };

  let json: unknown;
  if (body) {
    try {
      json = JSON.parse(body);
    } catch {
      json = undefined;
    }
  }

  if (isRecord(json)) {
    const error = isRecord(json.error) ? json.error : {};
    const detail = isRecord(json.detail) ? json.detail : {};
    // `json.error` as a bare string is AssemblyAI's / xAI's sentence; it is
    // only kept when it is itself an identifier, which `firstToken` decides.
    const errorCode = firstToken([error.code, json.code, json.err_code, json.error_code, detail.code]);
    const errorType = firstToken([
      error.type,
      error.status,
      json.error_type,
      json.type === 'error' ? undefined : json.type,
      detail.status,
      json.status,
      json.error,
    ]);
    if (errorCode !== undefined) fields.errorCode = errorCode;
    if (errorType !== undefined && errorType !== errorCode) fields.errorType = errorType;
    const bodyRequestId = firstToken([json.request_id, json.requestId, error.request_id], SAFE_REQUEST_ID);
    if (bodyRequestId !== undefined) fields.upstreamRequestId = bodyRequestId;
  }

  if (headers) {
    const headerRequestId = firstToken(REQUEST_ID_HEADERS.map((name) => headers.get(name)), SAFE_REQUEST_ID);
    if (headerRequestId !== undefined) fields.upstreamRequestId = headerRequestId;
  }

  return fields;
}

/**
 * Read a non-OK upstream response once and split it into loggable fields and
 * the classifier-only text. Never throws: an unreadable body gives
 * `bodyLength: -1` and `classifierText: '<unreadable>'`.
 */
export async function readUpstreamError(response: Response): Promise<UpstreamErrorRead> {
  let body: string | undefined;
  try {
    body = await response.text();
  } catch {
    body = undefined;
  }
  const fields = summarizeUpstreamError(response.status, response.headers, body);
  const classifierText = body === undefined
    ? '<unreadable>'
    : body.length <= CLASSIFIER_TEXT_LIMIT
      ? body
      : `${body.slice(0, CLASSIFIER_TEXT_LIMIT)}...`;
  return { fields, classifierText };
}

/**
 * A short, content-free reason for an Error message: the provider's error
 * code or type when it gave one, else `HTTP <status>`.
 */
export function upstreamErrorReason(fields: UpstreamErrorFields): string {
  return fields.errorCode ?? fields.errorType ?? `HTTP ${fields.status}`;
}

/**
 * The fields as a log-line object with no undefined members, so a JSON log
 * line stays compact and `toMatchObject` assertions read cleanly.
 */
export function upstreamErrorLogFields(fields: UpstreamErrorFields): Record<string, string | number> {
  const out: Record<string, string | number> = { status: fields.status, bodyLength: fields.bodyLength };
  if (fields.errorCode !== undefined) out.errorCode = fields.errorCode;
  if (fields.errorType !== undefined) out.errorType = fields.errorType;
  if (fields.upstreamRequestId !== undefined) out.upstreamRequestId = fields.upstreamRequestId;
  return out;
}
