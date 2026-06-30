// ASSISTANT ROUTE
// POST /assistant - vision LLM for screen-aware AI assistant mode
// Accepts multipart/form-data with screenshot + conversation messages
// Streams response as SSE (OpenAI-compatible delta format)

import type { Context } from 'hono';
import { generateRequestId, getClientIP } from '../lib/request-id';
import { MAX_ASSISTANT_BODY_BYTES } from '../lib/constants';
import { creditsForCost } from '../lib/cost-calculator';
import { isIPBlocked } from '../lib/redis';
import { MAX_ASSISTANT_IMAGE_BYTES } from '../lib/constants';
import { errorResponse, imageTooLargeResponse, CORS_HEADERS } from '../lib/responses';
import { validateAuth } from '../middleware/auth';
import { deductCredits, validateCredits } from '../middleware/credits';
import { streamAnthropicChat, type AnthropicContentBlock, type AnthropicMessage } from '../providers/anthropic';
import {
  ASSISTANT_MAX_IMAGES,
  ASSISTANT_MAX_INLINE_IMAGE_BYTES,
  ASSISTANT_MAX_MESSAGES_BYTES,
} from '../lib/constants';

// Estimated credits for upfront validation (vision requests are more expensive)
const ESTIMATED_ASSISTANT_CREDITS = 3.0;
const DEFAULT_IMAGE_MEDIA_TYPE = 'image/jpeg';
const SUPPORTED_IMAGE_MEDIA_TYPES = new Set([
  'image/jpeg',
  'image/png',
  'image/gif',
  'image/webp',
]);

function normalizeImageMediaType(mediaType: string | null | undefined): string | null {
  const normalized = mediaType?.split(';', 1)[0]?.trim().toLowerCase();
  return normalized && SUPPORTED_IMAGE_MEDIA_TYPES.has(normalized) ? normalized : null;
}

export function detectImageMediaType(bytes: Uint8Array): string | null {
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) {
    return 'image/jpeg';
  }

  if (
    bytes.length >= 8
    && bytes[0] === 0x89
    && bytes[1] === 0x50
    && bytes[2] === 0x4e
    && bytes[3] === 0x47
    && bytes[4] === 0x0d
    && bytes[5] === 0x0a
    && bytes[6] === 0x1a
    && bytes[7] === 0x0a
  ) {
    return 'image/png';
  }

  if (
    bytes.length >= 6
    && bytes[0] === 0x47
    && bytes[1] === 0x49
    && bytes[2] === 0x46
    && bytes[3] === 0x38
    && (bytes[4] === 0x37 || bytes[4] === 0x39)
    && bytes[5] === 0x61
  ) {
    return 'image/gif';
  }

  if (
    bytes.length >= 12
    && bytes[0] === 0x52
    && bytes[1] === 0x49
    && bytes[2] === 0x46
    && bytes[3] === 0x46
    && bytes[8] === 0x57
    && bytes[9] === 0x45
    && bytes[10] === 0x42
    && bytes[11] === 0x50
  ) {
    return 'image/webp';
  }

  return null;
}

export function validateAssistantContentLength(contentLengthHeader: string | undefined):
  | { ok: true }
  | { ok: false; response: Response } {
  if (!contentLengthHeader) {
    return { ok: false, response: errorResponse(400, 'Missing Content-Length', 'Content-Length header is required') };
  }

  const contentLength = Number.parseInt(contentLengthHeader, 10);
  if (!Number.isFinite(contentLength) || contentLength <= 0) {
    return { ok: false, response: errorResponse(400, 'Invalid Content-Length', 'Content-Length must be a positive integer') };
  }

  if (contentLength > MAX_ASSISTANT_BODY_BYTES) {
    return {
      ok: false,
      response: errorResponse(413, 'Request too large',
        `Request body must be ${Math.round(MAX_ASSISTANT_BODY_BYTES / (1024 * 1024))} MB or smaller`,
        { max_size_bytes: MAX_ASSISTANT_BODY_BYTES, content_length: contentLength }),
    };
  }

  return { ok: true };
}

/**
 * Estimate the decoded byte length of a base64 string without allocating a
 * buffer. Each 4 base64 chars decode to 3 bytes; trailing `=` padding reduces
 * the final group. Good enough to enforce a size cap before forwarding.
 */
function estimateBase64DecodedBytes(base64: string): number {
  const len = base64.length;
  if (len === 0) return 0;
  let padding = 0;
  if (base64.endsWith('==')) padding = 2;
  else if (base64.endsWith('=')) padding = 1;
  return Math.floor((len * 3) / 4) - padding;
}

/**
 * Convert the client's OpenAI-format messages to Anthropic Messages API format.
 * - Extracts the system message (returned separately for Anthropic's `system` param)
 * - Converts image_url content blocks to Anthropic image blocks
 */
export function convertMessages(
  clientMessages: unknown[],
  imageBase64: string | null,
  imageMediaType = DEFAULT_IMAGE_MEDIA_TYPE
): { systemPrompt: string; messages: AnthropicMessage[]; imageCount: number } {
  let systemPrompt = '';
  const messages: AnthropicMessage[] = [];
  // Cap total images (multipart + inline) forwarded to Anthropic so a client
  // cannot drive unbounded vision spend by embedding many image blocks.
  let imageCount = 0;

  for (const msg of clientMessages) {
    if (!msg || typeof msg !== 'object') continue;
    const m = msg as Record<string, unknown>;
    const role = m.role as string;

    if (role === 'system') {
      systemPrompt = typeof m.content === 'string' ? m.content : '';
      continue;
    }

    if (role !== 'user' && role !== 'assistant') continue;

    // Handle string content
    if (typeof m.content === 'string') {
      messages.push({ role, content: m.content });
      continue;
    }

    // Handle array content (multimodal — image_url + text)
    if (Array.isArray(m.content)) {
      const blocks: AnthropicContentBlock[] = [];

      for (const part of m.content) {
        if (!part || typeof part !== 'object') continue;
        const p = part as Record<string, unknown>;

        if (p.type === 'text' && typeof p.text === 'string') {
          blocks.push({ type: 'text', text: p.text });
        }

        if (p.type === 'image_url') {
          // Drop any images beyond the per-request cap instead of forwarding
          // them to Anthropic (unbounded vision cost).
          if (imageCount >= ASSISTANT_MAX_IMAGES) continue;

          // The client embeds base64 inline as data:image/jpeg;base64,...
          // But for HyperWhisper Cloud, the image comes as a separate multipart file
          // We use the multipart image if available, otherwise parse inline
          let base64Data = imageBase64;
          let mediaType = normalizeImageMediaType(imageMediaType) || DEFAULT_IMAGE_MEDIA_TYPE;

          if (!base64Data && p.image_url && typeof p.image_url === 'object') {
            const urlObj = p.image_url as Record<string, unknown>;
            const url = urlObj.url as string;
            if (url?.startsWith('data:')) {
              const match = url.match(/^data:(image\/\w+);base64,(.+)$/);
              if (match) {
                const parsedMediaType = normalizeImageMediaType(match[1]);
                // Reject oversized inline images. The multipart path is already
                // bounded by the request body size gate; inline base64 is not.
                if (parsedMediaType && estimateBase64DecodedBytes(match[2]) <= ASSISTANT_MAX_INLINE_IMAGE_BYTES) {
                  mediaType = parsedMediaType;
                  base64Data = match[2];
                }
              }
            }
          }

          if (base64Data) {
            blocks.push({
              type: 'image',
              source: {
                type: 'base64',
                media_type: mediaType,
                data: base64Data,
              },
            });
            imageCount += 1;
          }
        }
      }

      if (blocks.length > 0) {
        messages.push({ role, content: blocks });
      }
      continue;
    }
  }

  return { systemPrompt, messages, imageCount };
}

/**
 * Count the inline `image_url` content blocks in client messages, capped at
 * `ASSISTANT_MAX_IMAGES`. Used to scale the upfront credit estimate so a client
 * cannot pass a flat pre-check while forwarding multiple billable images.
 */
export function countInlineImages(clientMessages: unknown[]): number {
  let count = 0;
  for (const msg of clientMessages) {
    if (!msg || typeof msg !== 'object') continue;
    const content = (msg as Record<string, unknown>).content;
    if (!Array.isArray(content)) continue;
    for (const part of content) {
      if (!part || typeof part !== 'object') continue;
      if ((part as Record<string, unknown>).type === 'image_url') {
        count += 1;
        if (count >= ASSISTANT_MAX_IMAGES) return count;
      }
    }
  }
  return count;
}

export async function assistantRoute(c: Context) {
  const requestId = generateRequestId();
  const clientIP = getClientIP(c);

  if (await isIPBlocked(clientIP)) {
    return errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
  }

  // Size gate BEFORE parsing — `formData()` buffers the entire body into RAM,
  // so an unauthenticated oversized upload would OOM the machine before auth
  // runs (same pattern as /transcribe). Content-Length is required so the cap
  // can't be bypassed with chunked transfer encoding.
  const sizeCheck = validateAssistantContentLength(c.req.header('Content-Length'));
  if (!sizeCheck.ok) {
    return sizeCheck.response;
  }

  // Parse multipart form data
  let formData: FormData;
  try {
    formData = await c.req.formData();
  } catch {
    return errorResponse(400, 'Invalid request', 'Request must be multipart/form-data');
  }

  // `account_key` is the canonical field; `license_key` is the legacy alias that
  // installed native apps still send, so we accept either.
  const licenseKey = (formData.get('account_key') ||
    formData.get('license_key')) as string | null;
  const messagesRaw = formData.get('messages');
  const promptOverride = formData.get('prompt') as string | null;
  const imageFile = formData.get('image') as File | null;

  // Validate messages. A malformed multipart request can send `messages` as a
  // file part, in which case formData.get returns a File; reject any non-string
  // before measuring/parsing so it surfaces as a 400 rather than a 500.
  if (typeof messagesRaw !== 'string' || !messagesRaw) {
    return errorResponse(400, 'Missing field', 'Request must include "messages" field');
  }

  // Cap the serialized messages payload. Inline base64 images live inside this
  // JSON; without a cap a client could embed many MBs of image data and bypass
  // the flat credit pre-check, driving unbounded Anthropic vision spend. Sized
  // to admit ASSISTANT_MAX_IMAGES inline images at ASSISTANT_MAX_INLINE_IMAGE_BYTES
  // (base64 ~4/3 expansion) plus text headroom.
  if (Buffer.byteLength(messagesRaw, 'utf8') > ASSISTANT_MAX_MESSAGES_BYTES) {
    return errorResponse(413, 'Messages too large',
      `The "messages" payload must be ${Math.round(ASSISTANT_MAX_MESSAGES_BYTES / (1024 * 1024))} MB or smaller.`);
  }

  let clientMessages: unknown[];
  try {
    clientMessages = JSON.parse(messagesRaw);
    if (!Array.isArray(clientMessages)) throw new Error('not an array');
  } catch {
    return errorResponse(400, 'Invalid messages', 'Messages must be a valid JSON array');
  }

  // Auth — Cloud is licensed-only; a valid license key is required.
  const authResult = await validateAuth({ licenseKey: licenseKey || undefined });
  if (!authResult.ok) {
    return authResult.response;
  }

  // Credit check — scale the estimate by the number of images that will be
  // forwarded so a low-balance client cannot pass a flat pre-check and then
  // burn vision spend on multiple images.
  const estimatedImageCount = Math.max(1, countInlineImages(clientMessages));
  const estimatedCredits = ESTIMATED_ASSISTANT_CREDITS * estimatedImageCount;
  const creditCheck = await validateCredits(authResult.value, estimatedCredits, clientIP);
  if (!creditCheck.ok) {
    return creditCheck.response;
  }

  // Read image as base64
  let imageBase64: string | null = null;
  let imageMediaType = DEFAULT_IMAGE_MEDIA_TYPE;
  if (imageFile) {
    // Hard size cap BEFORE allocating the ArrayBuffer. Reading the file and
    // base64-encoding it (~1.33x expansion) on an unbounded upload can exhaust
    // the Bun process and OOM the machine, so reject oversized images upfront.
    if (imageFile.size > MAX_ASSISTANT_IMAGE_BYTES) {
      return imageTooLargeResponse(imageFile.size, MAX_ASSISTANT_IMAGE_BYTES);
    }
    const imageBuffer = await imageFile.arrayBuffer();
    const imageBytes = new Uint8Array(imageBuffer);
    const declaredMediaType = normalizeImageMediaType(imageFile.type);
    const detectedMediaType = detectImageMediaType(imageBytes);
    imageMediaType = detectedMediaType || declaredMediaType || '';
    if (!imageMediaType) {
      return errorResponse(400, 'Unsupported image type', 'Image must be JPEG, PNG, GIF, or WebP');
    }
    imageBase64 = Buffer.from(imageBytes).toString('base64');
  }

  // Convert messages to Anthropic format
  const { systemPrompt, messages } = convertMessages(clientMessages, imageBase64, imageMediaType);

  // Use the prompt override if provided (takes precedence over system message in conversation)
  const finalSystemPrompt = promptOverride || systemPrompt || 'You are a helpful screen-aware assistant.';

  console.log(`[${requestId}] Assistant request: ${messages.length} messages, image=${!!imageBase64}, ip=${clientIP}`);

  // Stream the response
  const { stream, costPromise } = streamAnthropicChat(finalSystemPrompt, messages, requestId);

  // Deduct credits after stream completes (fire-and-forget)
  costPromise.then((costUsd) => {
    if (costUsd > 0) {
      deductCredits(
        authResult.value,
        costUsd,
        {
          assistant_cost_usd: costUsd,
          message_count: messages.length,
          has_image: !!imageBase64,
          endpoint: '/assistant',
          llm_provider: 'anthropic',
        },
        clientIP
      ).catch(console.error);
    }
  }).catch(console.error);

  return new Response(stream, {
    headers: {
      ...CORS_HEADERS,
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      'Connection': 'keep-alive',
      'X-Accel-Buffering': 'no',
      'X-Request-ID': requestId,
    },
  });
}
