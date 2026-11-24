// Pre-parse request size gate for a route that buffers a whole body
// (`/post-process` JSON). Reading the body before auth lets an anonymous
// caller make the machine hold it in RAM, so the route checks Content-Length
// first. The header is required so chunked transfer
// encoding cannot skip the cap.

import { errorResponse } from './responses';

/**
 * A byte cap in the largest binary unit it is a whole number of. The internal
 * licence-cache route caps its body at 1024 bytes, and dividing that by MiB
 * and rounding said "must be 0 MB or smaller" (#2704).
 */
export function capWords(maxBytes: number): string {
  if (maxBytes % (1024 * 1024) === 0) return `${maxBytes / (1024 * 1024)} MB`;
  if (maxBytes % 1024 === 0) return `${maxBytes / 1024} KB`;
  return `${maxBytes} bytes`;
}

export type ContentLengthCheck =
  | { ok: true }
  | { ok: false; response: Response; contentLength: number | null };

export function validateContentLength(
  contentLengthHeader: string | undefined,
  maxBytes: number,
): ContentLengthCheck {
  if (!contentLengthHeader) {
    return {
      ok: false,
      contentLength: null,
      response: errorResponse(400, 'Missing Content-Length', 'Content-Length header is required'),
    };
  }

  const contentLength = Number.parseInt(contentLengthHeader, 10);
  if (!Number.isFinite(contentLength) || contentLength <= 0) {
    return {
      ok: false,
      contentLength: Number.isFinite(contentLength) ? contentLength : null,
      response: errorResponse(400, 'Invalid Content-Length', 'Content-Length must be a positive integer'),
    };
  }

  if (contentLength > maxBytes) {
    return {
      ok: false,
      contentLength,
      response: errorResponse(413, 'Request too large',
        `Request body must be ${capWords(maxBytes)} or smaller`,
        { max_size_bytes: maxBytes, content_length: contentLength }),
    };
  }

  return { ok: true };
}
