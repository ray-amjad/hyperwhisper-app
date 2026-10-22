// Pre-parse request size gate for a route that buffers a whole body
// (`/post-process` JSON). Reading the body before auth lets an anonymous
// caller make the machine hold it in RAM, so the route checks Content-Length
// first. The header is required so chunked transfer
// encoding cannot skip the cap.

import { errorResponse } from './responses';

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
        `Request body must be ${Math.round(maxBytes / (1024 * 1024))} MB or smaller`,
        { max_size_bytes: maxBytes, content_length: contentLength }),
    };
  }

  return { ok: true };
}
