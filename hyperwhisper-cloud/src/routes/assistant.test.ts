import { describe, expect, test } from 'bun:test';
import { convertMessages, countInlineImages, detectImageMediaType, validateAssistantContentLength } from './assistant';
import {
  ASSISTANT_MAX_IMAGES,
  ASSISTANT_MAX_INLINE_IMAGE_BYTES,
  MAX_ASSISTANT_BODY_BYTES,
} from '../lib/constants';
import type { AnthropicContentBlock } from '../providers/anthropic';

function imageBlockFor(mediaType: string): AnthropicContentBlock {
  const { messages } = convertMessages([
    {
      role: 'user',
      content: [
        { type: 'text', text: 'What is on screen?' },
        { type: 'image_url', image_url: { url: 'ignored-for-multipart' } },
      ],
    },
  ], 'base64-payload', mediaType);

  const content = messages[0]?.content;
  expect(Array.isArray(content)).toBe(true);

  const imageBlock = (content as AnthropicContentBlock[]).find((block) => block.type === 'image');
  expect(imageBlock).toBeDefined();
  return imageBlock!;
}

describe('convertMessages image media_type', () => {
  test('uses the multipart image media type when it is supported', () => {
    const imageBlock = imageBlockFor('image/png');

    expect(imageBlock.source?.media_type).toBe('image/png');
    expect(imageBlock.source?.data).toBe('base64-payload');
  });

  test('normalizes supported multipart media types', () => {
    const imageBlock = imageBlockFor(' IMAGE/WEBP; charset=binary ');

    expect(imageBlock.source?.media_type).toBe('image/webp');
  });

  test('falls back to jpeg for unsupported multipart media types', () => {
    const imageBlock = imageBlockFor('image/bmp');

    expect(imageBlock.source?.media_type).toBe('image/jpeg');
  });

  test('uses supported inline data URI media types when no multipart image is present', () => {
    const { messages } = convertMessages([
      {
        role: 'user',
        content: [
          { type: 'image_url', image_url: { url: 'data:image/png;base64,inline-payload' } },
        ],
      },
    ], null);

    const content = messages[0]?.content;
    expect(Array.isArray(content)).toBe(true);

    const imageBlock = (content as AnthropicContentBlock[]).find((block) => block.type === 'image');
    expect(imageBlock?.source?.media_type).toBe('image/png');
    expect(imageBlock?.source?.data).toBe('inline-payload');
  });
});

describe('convertMessages image limits', () => {
  test('drops inline images that exceed the decoded size cap', () => {
    // base64 length ~4/3 of decoded bytes; build a payload well over the cap.
    const oversizedBase64 = 'A'.repeat(ASSISTANT_MAX_INLINE_IMAGE_BYTES * 2);
    const { messages, imageCount } = convertMessages([
      {
        role: 'user',
        content: [
          { type: 'text', text: 'What is on screen?' },
          { type: 'image_url', image_url: { url: `data:image/png;base64,${oversizedBase64}` } },
        ],
      },
    ], null);

    expect(imageCount).toBe(0);
    const content = messages[0]?.content as AnthropicContentBlock[];
    expect(content.some((block) => block.type === 'image')).toBe(false);
  });

  test('keeps inline images within the decoded size cap', () => {
    const { imageCount } = convertMessages([
      {
        role: 'user',
        content: [
          { type: 'image_url', image_url: { url: 'data:image/png;base64,aGVsbG8=' } },
        ],
      },
    ], null);

    expect(imageCount).toBe(1);
  });

  test('caps the number of forwarded images per request', () => {
    const imageParts = Array.from({ length: ASSISTANT_MAX_IMAGES + 3 }, () => ({
      type: 'image_url',
      image_url: { url: 'data:image/png;base64,aGVsbG8=' },
    }));
    const { messages, imageCount } = convertMessages([
      { role: 'user', content: imageParts },
    ], null);

    expect(imageCount).toBe(ASSISTANT_MAX_IMAGES);
    const content = messages[0]?.content as AnthropicContentBlock[];
    const imageBlocks = content.filter((block) => block.type === 'image');
    expect(imageBlocks.length).toBe(ASSISTANT_MAX_IMAGES);
  });
});

describe('countInlineImages', () => {
  test('counts inline image_url blocks across messages, clamped to the cap', () => {
    const messages = [
      { role: 'user', content: [{ type: 'image_url', image_url: { url: 'data:image/png;base64,aaaa' } }] },
      { role: 'assistant', content: 'sure' },
      {
        role: 'user',
        content: [
          { type: 'text', text: 'and this?' },
          { type: 'image_url', image_url: { url: 'data:image/png;base64,bbbb' } },
          { type: 'image_url', image_url: { url: 'data:image/png;base64,cccc' } },
        ],
      },
    ];

    expect(countInlineImages(messages)).toBe(ASSISTANT_MAX_IMAGES);
  });

  test('returns 0 when there are no image blocks', () => {
    expect(countInlineImages([{ role: 'user', content: 'hello' }])).toBe(0);
  });
});

describe('detectImageMediaType', () => {
  test('detects supported image signatures', () => {
    expect(detectImageMediaType(new Uint8Array([0xff, 0xd8, 0xff, 0xdb]))).toBe('image/jpeg');
    expect(detectImageMediaType(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))).toBe('image/png');
    expect(detectImageMediaType(new Uint8Array([0x47, 0x49, 0x46, 0x38, 0x39, 0x61]))).toBe('image/gif');
    expect(detectImageMediaType(new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]))).toBe('image/webp');
  });

  test('returns null for unsupported image signatures', () => {
    expect(detectImageMediaType(new Uint8Array([0x42, 0x4d, 0x00, 0x00]))).toBeNull();
  });
});

describe('validateAssistantContentLength', () => {
  test('accepts a body at the cap', () => {
    expect(validateAssistantContentLength(String(MAX_ASSISTANT_BODY_BYTES)).ok).toBe(true);
  });

  test('rejects a missing Content-Length header with 400', () => {
    const result = validateAssistantContentLength(undefined);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(400);
  });

  test('rejects non-numeric and non-positive Content-Length with 400', () => {
    for (const header of ['abc', '0', '-5']) {
      const result = validateAssistantContentLength(header);
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.response.status).toBe(400);
    }
  });

  test('rejects an oversized body with 413 before parsing', () => {
    const result = validateAssistantContentLength(String(MAX_ASSISTANT_BODY_BYTES + 1));
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.response.status).toBe(413);
  });
});
