// Regenerates the smoke-test audio fixtures from ElevenLabs TTS.
//
// These mp3s are the source audio the deploy smoke test sends to /transcribe
// (see scripts/smoke-test.ts). They live in git so CI needs no live TTS key —
// only re-run this when you want to refresh the voice set.
//
//   ELEVENLABS_API_KEY=sk_... bun run scripts/fixtures/generate-fixtures.ts
//
// Writes one mp3 per entry below plus manifest.json (consumed by the smoke test).
// A spread of languages, accents, and genders so the smoke test exercises the
// multilingual transcription path, not just English.

import { writeFile } from 'node:fs/promises';

const API_KEY = process.env.ELEVENLABS_API_KEY;
if (!API_KEY) {
  console.error('✗ ELEVENLABS_API_KEY is required');
  process.exit(1);
}

const MODEL_ID = 'eleven_multilingual_v2';
const OUTPUT_FORMAT = 'mp3_22050_32'; // small files, ample quality for STT
const OUT_DIR = new URL('.', import.meta.url).pathname;

interface Fixture {
  file: string;
  language: string; // ISO-ish hint for transcription
  voice: string;
  voiceId: string;
  text: string;
}

const FIXTURES: Fixture[] = [
  { file: 'en-us-sarah.mp3', language: 'en', voice: 'Sarah',  voiceId: 'EXAVITQu4vr4xnSDxMaL', text: 'The quick brown fox jumps over the lazy dog near the river bank.' },
  { file: 'en-gb-george.mp3', language: 'en', voice: 'George', voiceId: 'JBFqnCBsd6RMkjVDRZzb', text: 'Good morning, this is a smoke test of the transcription service.' },
  { file: 'es-m.mp3',         language: 'es', voice: 'M',      voiceId: 'CjD6JLhiDwLIE7zTlpAR', text: 'Hola, esto es una prueba del servicio de transcripción en español.' },
  { file: 'fr-lily.mp3',      language: 'fr', voice: 'Lily',   voiceId: 'pFZP5JQG7iQjIQuC4Bku', text: 'Bonjour, ceci est un test du service de transcription en français.' },
  { file: 'de-brian.mp3',     language: 'de', voice: 'Brian',  voiceId: 'nPczCjzI2devNBz1zQrb', text: 'Guten Tag, dies ist ein Test des Transkriptionsdienstes auf Deutsch.' },
  { file: 'it-alice.mp3',     language: 'it', voice: 'Alice',  voiceId: 'Xb7hH8MSUJpSbSDYk0k2', text: 'Buongiorno, questo è un test del servizio di trascrizione in italiano.' },
  { file: 'pt-eric.mp3',      language: 'pt', voice: 'Eric',   voiceId: 'cjVigY5qzO86Huf0OWal', text: 'Olá, este é um teste do serviço de transcrição em português.' },
  { file: 'hi-rashmi.mp3',    language: 'hi', voice: 'Rashmi', voiceId: 'bsF1NNjoIXl26JIdKiFz', text: 'नमस्ते, यह ट्रांसक्रिप्शन सेवा का एक परीक्षण है।' },
  { file: 'ja-yuki.mp3',      language: 'ja', voice: 'Yuki',   voiceId: 'JTlYtJrcTzPC71hMLOxo', text: 'こんにちは、これは文字起こしサービスのテストです。' },
  { file: 'zh-roger.mp3',     language: 'zh', voice: 'Roger',  voiceId: 'CwhRBWXzGAHq8TQ4Fs17', text: '你好，这是转录服务的一个测试。' },
];

async function generate(f: Fixture): Promise<void> {
  const res = await fetch(
    `https://api.elevenlabs.io/v1/text-to-speech/${f.voiceId}?output_format=${OUTPUT_FORMAT}`,
    {
      method: 'POST',
      headers: { 'xi-api-key': API_KEY!, 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: f.text, model_id: MODEL_ID }),
    },
  );
  if (!res.ok) {
    throw new Error(`${f.file}: ${res.status} ${await res.text()}`);
  }
  const bytes = Buffer.from(await res.arrayBuffer());
  await writeFile(`${OUT_DIR}${f.file}`, bytes);
  console.log(`✓ ${f.file.padEnd(18)} ${f.language}  ${(bytes.length / 1024).toFixed(1)} KB  (${f.voice})`);
}

for (const f of FIXTURES) {
  await generate(f);
}

const manifest = FIXTURES.map(({ file, language, voice, text }) => ({ file, language, voice, text }));
await writeFile(`${OUT_DIR}manifest.json`, JSON.stringify(manifest, null, 2) + '\n');
console.log(`\n✓ Wrote ${FIXTURES.length} fixtures + manifest.json`);
