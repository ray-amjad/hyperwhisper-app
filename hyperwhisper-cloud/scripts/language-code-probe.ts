// Language-code probe for the STT providers.
//
// Answers one question per row: what does each vendor ACTUALLY do with a given
// language code? The audit that motivated this script could only prove the
// contract for two providers from the docs; every other "risky" verdict rested
// on a doc sentence, not on a response. This script gets the response.
//
// It calls each VENDOR API DIRECTLY — it does not go through /transcribe. That
// is the point: our adapters rewrite the code before it leaves the box (strip
// the region, lowercase, swap the field), so a probe through our own edge
// measures our transform, not the vendor's contract.
//
// Run it with the Infisical secrets, from hyperwhisper-cloud/. This directory is
// not linked to a project yet, so link it once with `infisical init` (or pass
// `--projectId`), then:
//
//   infisical run --env prod -- bun run scripts/language-code-probe.ts
//   infisical run --env prod -- bun run scripts/language-code-probe.ts --provider google-chirp
//   bun run scripts/language-code-probe.ts --dry-run     (prints the matrix, calls nothing)
//
// The probe reads the same secret NAMES the adapters read, so a row with no key
// reports SKIPPED instead of failing. It never prints a key. It does print
// transcripts and vendor error bodies — that is the whole output — so keep the
// run local and do not paste a full log anywhere public.
//
// CAUTION: every non-dry row is a paid vendor call that uploads a 4-second
// English audio fixture to that vendor. The full matrix is about 45 calls.
// Cost is a few cents. Do not wire this into CI.
//
// The fixture is always English (`en-us-sarah.mp3`, "The quick brown fox jumps
// over the lazy dog near the river bank."). That makes the transcript itself the
// instrument: ask for `ja` over English audio and a provider that ENFORCES the
// code gives back Japanese script or nothing, while a provider that treats it as
// a HINT gives back the English sentence.
//
// Verdicts:
//   ACCEPTED   200, transcript looks like the English fixture. The code is
//              either honored or harmlessly ignored — safe to send.
//   REJECTED   4xx/5xx. The code is a hard error — we must map it before it
//              leaves us, or the user gets a failed transcription.
//   FORCED     200, but the transcript is not the English fixture. The vendor
//              obeyed the wrong code and mis-decoded the audio.
//   EMPTY      200 with no transcript. Usually a silent form of FORCED.
//
// Read REJECTED rows as work items. Read FORCED rows as worse work items: they
// fail without an error, so no retry or fallback ever fires.

import { readFile } from 'node:fs/promises';
import { getGoogleAccessToken } from '../src/lib/google-auth';

const FIXTURE = new URL('./fixtures/en-us-sarah.mp3', import.meta.url).pathname;
const FIXTURE_MIME = 'audio/mpeg';
const CALL_TIMEOUT_MS = 90_000;
// Politeness gap between two calls to the same vendor, so a 12-row provider
// does not look like a burst to a rate limiter.
const INTER_CALL_DELAY_MS = 750;

const args = process.argv.slice(2);
const DRY_RUN = args.includes('--dry-run');
const ONLY_PROVIDER = (() => {
  const i = args.indexOf('--provider');
  return i >= 0 ? args[i + 1] : null;
})();

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type Verdict = 'ACCEPTED' | 'REJECTED' | 'FORCED' | 'EMPTY' | 'SKIPPED' | 'ERROR';

interface CallResult {
  status: number;
  transcript: string;
  /** Language the vendor says it detected, when the response carries one. */
  detected?: string;
  /** First 200 characters of an error body. */
  errorPreview?: string;
}

interface ProbeRow {
  provider: string;
  /** What we sent, in the vendor's own parameter name. */
  sent: string;
  /** Why this row exists — printed next to the verdict. */
  question: string;
  /** Missing secret name, when the row cannot run. */
  requires: string[];
  call(audio: Buffer): Promise<CallResult>;
}

interface ProbeOutcome extends ProbeRow {
  verdict: Verdict;
  status: number;
  detected?: string;
  transcript: string;
  detail: string;
  elapsedMs: number;
}

// ---------------------------------------------------------------------------
// Fixture-recognition
// ---------------------------------------------------------------------------

// Content words from the fixture sentence. A provider that decoded the audio as
// English hits most of these; one that forced a wrong language hits none.
const FIXTURE_WORDS = ['quick', 'brown', 'fox', 'jump', 'lazy', 'dog', 'river', 'bank'];

const NON_LATIN = /[Ѐ-ӿ԰-ۿऀ-෿က-႟぀-ヿ㐀-鿿가-힯]/;

/** True when the transcript is recognizably the English fixture sentence. */
function looksLikeFixture(text: string): boolean {
  const lower = text.toLowerCase();
  const hits = FIXTURE_WORDS.filter(w => lower.includes(w)).length;
  return hits >= 3;
}

function classify(result: CallResult): { verdict: Verdict; detail: string } {
  if (result.status >= 400) {
    return { verdict: 'REJECTED', detail: `HTTP ${result.status} · ${result.errorPreview ?? ''}`.trim() };
  }
  const text = result.transcript.trim();
  if (text.length === 0) {
    return { verdict: 'EMPTY', detail: 'HTTP 200 with an empty transcript' };
  }
  if (looksLikeFixture(text)) {
    return { verdict: 'ACCEPTED', detail: 'English fixture came back intact' };
  }
  const script = NON_LATIN.test(text) ? 'non-Latin script' : 'Latin script, wrong words';
  return { verdict: 'FORCED', detail: `mis-decoded (${script})` };
}

// ---------------------------------------------------------------------------
// HTTP helper
// ---------------------------------------------------------------------------

async function call(url: string, init: RequestInit): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), CALL_TIMEOUT_MS);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

async function preview(response: Response): Promise<string> {
  try {
    const body = await response.text();
    return body.replace(/\s+/g, ' ').slice(0, 200);
  } catch {
    return '<unreadable body>';
  }
}

function env(name: string): string | undefined {
  const value = process.env[name];
  return value && value.trim().length > 0 ? value : undefined;
}

// ---------------------------------------------------------------------------
// Per-vendor callers
//
// Each one mirrors the shape our adapter sends, with ONE variable changed: the
// language code. Everything else — model, audio part name, format flags — is
// copied from src/providers/*.ts so a REJECTED row means the code was wrong,
// not the envelope.
// ---------------------------------------------------------------------------

async function callDeepgram(audio: Buffer, model: string, language: string | null): Promise<CallResult> {
  const params = new URLSearchParams({
    model,
    smart_format: 'true',
    utterances: 'true',
    mip_opt_out: 'true',
  });
  if (language) params.set('language', language);
  else params.set('detect_language', 'true');

  const response = await call(`https://api.deepgram.com/v1/listen?${params.toString()}`, {
    method: 'POST',
    headers: { Authorization: `Token ${env('DEEPGRAM_API_KEY')}`, 'Content-Type': FIXTURE_MIME },
    body: new Uint8Array(audio),
  });
  if (!response.ok) return { status: response.status, transcript: '', errorPreview: await preview(response) };

  const data: any = await response.json();
  const alt = data?.results?.channels?.[0]?.alternatives?.[0];
  return {
    status: response.status,
    transcript: alt?.transcript ?? '',
    detected: data?.results?.channels?.[0]?.detected_language,
  };
}

async function callElevenLabs(audio: Buffer, model: string, language: string | null): Promise<CallResult> {
  const form = new FormData();
  form.append('file', new Blob([new Uint8Array(audio)], { type: FIXTURE_MIME }), 'audio.mp3');
  form.append('model_id', model);
  form.append('tag_audio_events', 'false');
  if (language) form.append('language_code', language);

  const response = await call('https://api.elevenlabs.io/v1/speech-to-text', {
    method: 'POST',
    headers: {
      'xi-api-key': env('ELEVENLABS_API_KEY')!,
      'User-Agent': 'hyperwhisper-cloud/1.0',
      Accept: 'application/json',
    },
    body: form,
  });
  if (!response.ok) return { status: response.status, transcript: '', errorPreview: await preview(response) };

  const data: any = await response.json();
  return { status: response.status, transcript: data?.text ?? '', detected: data?.language_code };
}

/**
 * OpenAI, with the language carried in a caller-chosen field. The audit claims
 * `gpt-transcribe` takes a plural `languages` and ignores the singular
 * `language` our adapter sends; `field` is what makes that testable.
 */
async function callOpenAI(
  audio: Buffer,
  model: string,
  field: 'language' | 'languages' | null,
  value: string | null,
): Promise<CallResult> {
  const form = new FormData();
  form.append('file', new Blob([new Uint8Array(audio)], { type: FIXTURE_MIME }), 'audio.mp3');
  form.append('model', model);
  form.append('response_format', model === 'whisper-1' ? 'verbose_json' : 'json');
  if (field && value) form.append(field, value);

  const response = await call('https://api.openai.com/v1/audio/transcriptions', {
    method: 'POST',
    headers: { Authorization: `Bearer ${env('OPENAI_API_KEY')}` },
    body: form,
  });
  if (!response.ok) return { status: response.status, transcript: '', errorPreview: await preview(response) };

  const data: any = await response.json();
  return { status: response.status, transcript: data?.text ?? '', detected: data?.language };
}

async function callAzureMai(audio: Buffer, locale: string | null): Promise<CallResult> {
  const region = env('AZURE_PROBE_REGION') ?? 'eastus';
  const key =
    env(`AZURE_SPEECH_KEY_${region.toUpperCase()}`) ??
    env('AZURE_SPEECH_KEY_EASTUS') ??
    env('AZURE_SPEECH_KEY_NORTHEUROPE') ??
    env('AZURE_SPEECH_KEY_SOUTHEASTASIA');

  const definition: Record<string, unknown> = {
    enhancedMode: { enabled: true, model: 'mai-transcribe-1.5' },
  };
  if (locale) definition.locales = [locale];

  const form = new FormData();
  form.append('audio', new Blob([new Uint8Array(audio)], { type: 'application/octet-stream' }), 'audio.mp3');
  form.append('definition', new Blob([JSON.stringify(definition)], { type: 'application/json' }), 'definition.json');

  const response = await call(
    `https://${region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15`,
    { method: 'POST', headers: { 'Ocp-Apim-Subscription-Key': key! }, body: form },
  );
  if (!response.ok) return { status: response.status, transcript: '', errorPreview: await preview(response) };

  const data: any = await response.json();
  const combined = data?.combinedPhrases?.[0]?.text ?? '';
  return { status: response.status, transcript: combined, detected: data?.phrases?.[0]?.locale };
}

async function callGemini(audio: Buffer, model: string, language: string | null): Promise<CallResult> {
  let prompt =
    'Transcribe the speech in this audio verbatim. Output only the transcript text with no commentary, labels, timestamps, or preamble.';
  if (language) prompt += ` The audio is in language code "${language}"; transcribe it in that language.`;

  const body = {
    contents: [
      {
        role: 'user',
        parts: [
          { text: prompt },
          { inline_data: { mime_type: FIXTURE_MIME, data: audio.toString('base64') } },
        ],
      },
    ],
    generationConfig: { temperature: 0, thinkingConfig: { thinkingBudget: 0 } },
  };

  const response = await call(
    `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(model)}:generateContent`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-goog-api-key': env('GEMINI_API_KEY') ?? env('GOOGLE_GEMINI_API_KEY')!,
      },
      body: JSON.stringify(body),
    },
  );
  if (!response.ok) return { status: response.status, transcript: '', errorPreview: await preview(response) };

  const data: any = await response.json();
  const text = (data?.candidates?.[0]?.content?.parts ?? [])
    .map((p: any) => p?.text ?? '')
    .join('')
    .trim();
  return { status: response.status, transcript: text };
}

async function callGoogleChirp(audio: Buffer, language: string): Promise<CallResult> {
  const projectId = env('GOOGLE_PROJECT_ID')!;
  const region = env('GOOGLE_SPEECH_REGION')?.trim() || 'us';
  const token = await getGoogleAccessToken();

  const response = await call(
    `https://${region}-speech.googleapis.com/v2/projects/${projectId}/locations/${region}/recognizers/_:recognize`,
    {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({
        config: { autoDecodingConfig: {}, languageCodes: [language], model: 'chirp_3' },
        content: audio.toString('base64'),
      }),
    },
  );
  if (!response.ok) return { status: response.status, transcript: '', errorPreview: await preview(response) };

  const data: any = await response.json();
  const results = data?.results ?? [];
  const transcript = results.map((r: any) => r?.alternatives?.[0]?.transcript ?? '').join(' ').trim();
  return { status: response.status, transcript, detected: results[0]?.languageCode };
}

// ---------------------------------------------------------------------------
// The matrix
// ---------------------------------------------------------------------------

const GOOGLE_SECRETS = ['GOOGLE_PROJECT_ID', 'GOOGLE_SERVICE_ACCOUNT_JSON'];

function chirpRow(sent: string, question: string): ProbeRow {
  return {
    provider: 'google-chirp',
    sent: `languageCodes: ["${sent}"]`,
    question,
    requires: GOOGLE_SECRETS,
    call: audio => callGoogleChirp(audio, sent),
  };
}

function deepgramRow(model: string, language: string | null, question: string): ProbeRow {
  return {
    provider: 'deepgram',
    sent: `${model} · ${language ? `language=${language}` : 'detect_language=true'}`,
    question,
    requires: ['DEEPGRAM_API_KEY'],
    call: audio => callDeepgram(audio, model, language),
  };
}

function openaiRow(
  model: string,
  field: 'language' | 'languages' | null,
  value: string | null,
  question: string,
): ProbeRow {
  return {
    provider: 'openai',
    sent: `${model} · ${field && value ? `${field}=${value}` : 'no language field'}`,
    question,
    requires: ['OPENAI_API_KEY'],
    call: audio => callOpenAI(audio, model, field, value),
  };
}

function azureRow(locale: string | null, question: string): ProbeRow {
  return {
    provider: 'azure-mai',
    sent: locale ? `locales: ["${locale}"]` : 'locales omitted',
    question,
    requires: ['AZURE_SPEECH_KEY_EASTUS'],
    call: audio => callAzureMai(audio, locale),
  };
}

function elevenRow(language: string | null, question: string): ProbeRow {
  return {
    provider: 'elevenlabs',
    sent: language ? `language_code=${language}` : 'language_code omitted',
    question,
    requires: ['ELEVENLABS_API_KEY'],
    call: audio => callElevenLabs(audio, 'scribe_v2', language),
  };
}

function geminiRow(language: string | null, question: string): ProbeRow {
  return {
    provider: 'gemini',
    sent: language ? `prompt says code "${language}"` : 'no language in prompt',
    question,
    requires: ['GEMINI_API_KEY'],
    call: audio => callGemini(audio, 'gemini-2.5-flash', language),
  };
}

const ROWS: ProbeRow[] = [
  // --- google-chirp: the provider we already caught 400ing on a bare code. ---
  chirpRow('auto', 'baseline — the sentinel the app sends on Automatic'),
  chirpRow('en', 'the bare code both pickers send today'),
  chirpRow('en-US', 'the locale the catalog holds'),
  chirpRow('sw', 'the one bare code Google lists — does it really pass?'),
  chirpRow('cmn-Hans-CN', 'a script-qualified locale, to size the mapping table'),
  chirpRow('zz', 'well-formed but no such language — hard 400 or ignored?'),
  chirpRow('ja-JP', 'wrong language over English audio — hint or law?'),

  // --- deepgram: per-model language support is the open question. ---
  deepgramRow('nova-3', 'en', 'baseline'),
  deepgramRow('nova-3', null, 'baseline auto-detect'),
  deepgramRow('nova-3', 'en-US', 'does the locale form pass, or must we strip?'),
  deepgramRow('nova-3', 'zz', 'unknown code — hard fail or ignored?'),
  deepgramRow('nova-3', 'ja', 'wrong language over English audio — hint or law?'),
  deepgramRow('nova-2', 'en', 'baseline for the older model'),
  deepgramRow('nova-2', 'sw', 'a code nova-3 has and nova-2 lacks'),
  deepgramRow('nova-3-medical', 'en', 'baseline for the English-only model'),
  deepgramRow('nova-3-medical', 'es', 'non-English on an English-only model'),
  deepgramRow('nova-2-medical', 'es', 'same question, older medical model'),

  // --- openai: singular `language` vs plural `languages` on gpt-transcribe. ---
  openaiRow('gpt-transcribe', 'language', 'en', 'the singular field our adapter sends today'),
  openaiRow('gpt-transcribe', 'languages', 'en', 'the plural field the docs name'),
  openaiRow('gpt-transcribe', null, null, 'baseline with no language at all'),
  openaiRow('gpt-transcribe', 'language', 'ja', 'does the singular field even bite?'),
  openaiRow('gpt-transcribe', 'languages', 'ja', 'does the plural field bite?'),
  openaiRow('whisper-1', 'language', 'en', 'baseline — whisper takes the singular'),
  openaiRow('whisper-1', 'language', 'zz', 'unknown code on whisper'),

  // --- azure-mai: the `no` / `nb` Norwegian split. ---
  azureRow('en', 'baseline — the 2-letter form our adapter sends'),
  azureRow('en-US', 'does the locale form pass too?'),
  azureRow('no', 'the code our picker sends for Norwegian'),
  azureRow('nb', 'the code Azure documents for Norwegian'),
  azureRow('zz', 'unknown code — hard fail or ignored?'),
  azureRow(null, 'baseline auto-detect'),

  // --- elevenlabs: Tagalog naming, plus the 639-3 question. ---
  elevenRow('en', 'baseline'),
  elevenRow(null, 'baseline auto-detect'),
  elevenRow('eng', 'does the 3-letter ISO-639-3 form pass?'),
  elevenRow('tl', 'the code Windows folds Tagalog to'),
  elevenRow('fil', 'the code the catalog holds for Tagalog'),
  elevenRow('en-US', 'does a locale pass, or must we strip?'),
  elevenRow('zz', 'unknown code — hard fail or ignored?'),

  // --- gemini: the code goes into a PROMPT, so ambiguity is the risk. ---
  geminiRow(null, 'baseline with no language instruction'),
  geminiRow('en', 'baseline'),
  geminiRow('no', 'the code for Norwegian is also the English word "no"'),
  geminiRow('is', 'the code for Icelandic is also the English word "is"'),
  geminiRow('ja', 'wrong language over English audio — does the prompt force it?'),
  geminiRow('zz', 'unknown code — does it derail the transcript?'),
];

// ---------------------------------------------------------------------------
// Runner
// ---------------------------------------------------------------------------

const VERDICT_MARK: Record<Verdict, string> = {
  ACCEPTED: '✓',
  REJECTED: '✗',
  FORCED: '!',
  EMPTY: '?',
  SKIPPED: '-',
  ERROR: 'E',
};

function missingSecrets(row: ProbeRow): string[] {
  return row.requires.filter(name => !env(name));
}

async function runRow(row: ProbeRow, audio: Buffer): Promise<ProbeOutcome> {
  const missing = missingSecrets(row);
  if (missing.length > 0) {
    return {
      ...row,
      verdict: 'SKIPPED',
      status: 0,
      transcript: '',
      detail: `missing ${missing.join(', ')}`,
      elapsedMs: 0,
    };
  }

  const startedAt = performance.now();
  try {
    const result = await row.call(audio);
    const { verdict, detail } = classify(result);
    return {
      ...row,
      verdict,
      status: result.status,
      detected: result.detected,
      transcript: result.transcript,
      detail,
      elapsedMs: Math.round(performance.now() - startedAt),
    };
  } catch (error) {
    return {
      ...row,
      verdict: 'ERROR',
      status: 0,
      transcript: '',
      detail: error instanceof Error ? error.message : String(error),
      elapsedMs: Math.round(performance.now() - startedAt),
    };
  }
}

/** Run one provider's rows in order, so we never burst a rate limiter. */
async function runProvider(rows: ProbeRow[], audio: Buffer): Promise<ProbeOutcome[]> {
  const outcomes: ProbeOutcome[] = [];
  for (const row of rows) {
    const outcome = await runRow(row, audio);
    outcomes.push(outcome);
    console.log(
      `  ${VERDICT_MARK[outcome.verdict]} [${outcome.verdict.padEnd(8)}] ${outcome.provider} · ${outcome.sent}` +
        `${outcome.elapsedMs ? ` (${outcome.elapsedMs} ms)` : ''}`,
    );
    if (outcome.verdict !== 'ACCEPTED' && outcome.detail) {
      console.log(`      ${outcome.detail}`);
    }
    await new Promise(resolve => setTimeout(resolve, INTER_CALL_DELAY_MS));
  }
  return outcomes;
}

function groupByProvider(rows: ProbeRow[]): Map<string, ProbeRow[]> {
  const groups = new Map<string, ProbeRow[]>();
  for (const row of rows) {
    const list = groups.get(row.provider) ?? [];
    list.push(row);
    groups.set(row.provider, list);
  }
  return groups;
}

async function main() {
  const rows = ONLY_PROVIDER ? ROWS.filter(r => r.provider === ONLY_PROVIDER) : ROWS;
  if (rows.length === 0) {
    console.error(`No rows for provider "${ONLY_PROVIDER}".`);
    console.error(`Known: ${[...new Set(ROWS.map(r => r.provider))].join(', ')}`);
    process.exit(2);
  }

  const groups = groupByProvider(rows);

  if (DRY_RUN) {
    console.log(`Language-code probe — ${rows.length} rows, no calls made (--dry-run).\n`);
    for (const [provider, providerRows] of groups) {
      console.log(`${provider} (${providerRows.length} rows)`);
      for (const row of providerRows) {
        const missing = missingSecrets(row);
        const flag = missing.length > 0 ? `  [would skip: missing ${missing.join(', ')}]` : '';
        console.log(`  • ${row.sent}${flag}`);
        console.log(`      ${row.question}`);
      }
      console.log('');
    }
    return;
  }

  const audio = await readFile(FIXTURE);
  console.log(`Language-code probe — ${rows.length} paid calls across ${groups.size} providers.`);
  console.log(`Fixture: en-us-sarah.mp3 (${Math.round(audio.byteLength / 1024)} KB English speech)\n`);

  // Providers run in parallel; rows inside a provider run in order.
  const results = await Promise.all(
    [...groups.values()].map(providerRows => runProvider(providerRows, audio)),
  );
  const outcomes = results.flat();

  // --- Report -------------------------------------------------------------
  console.log('\n' + '='.repeat(78));
  console.log('RESULTS');
  console.log('='.repeat(78));

  for (const [provider, providerRows] of groups) {
    const providerOutcomes = outcomes.filter(o => o.provider === provider);
    console.log(`\n${provider}`);
    for (const outcome of providerOutcomes) {
      console.log(`  ${VERDICT_MARK[outcome.verdict]} ${outcome.verdict.padEnd(8)} ${outcome.sent}`);
      console.log(`      asks: ${outcome.question}`);
      console.log(`      got:  ${outcome.detail || `HTTP ${outcome.status}`}`);
      if (outcome.detected) console.log(`      vendor says language: ${outcome.detected}`);
      if (outcome.transcript) console.log(`      transcript: ${outcome.transcript.slice(0, 120)}`);
    }
    void providerRows;
  }

  const counts = outcomes.reduce<Record<string, number>>((acc, o) => {
    acc[o.verdict] = (acc[o.verdict] ?? 0) + 1;
    return acc;
  }, {});

  console.log('\n' + '='.repeat(78));
  console.log(
    'SUMMARY  ' +
      Object.entries(counts)
        .map(([verdict, count]) => `${verdict}=${count}`)
        .join('  '),
  );

  const broken = outcomes.filter(o => o.verdict === 'REJECTED' || o.verdict === 'FORCED' || o.verdict === 'EMPTY');
  if (broken.length > 0) {
    console.log('\nCodes that need a mapping before they leave us:');
    for (const outcome of broken) {
      console.log(`  ${outcome.provider}: ${outcome.sent} → ${outcome.verdict}`);
    }
  }

  const tmpDir = (process.env.TMPDIR ?? '/tmp').replace(/\/$/, '');
  const outPath = `${tmpDir}/language-code-probe.json`;
  await Bun.write(outPath, JSON.stringify(outcomes.map(({ call, ...rest }) => rest), null, 2));
  console.log(`\nFull results: ${outPath}`);

  // A REJECTED or FORCED row is a real finding, not a script failure, so the
  // exit code stays 0. Only a broken probe (ERROR) is worth failing on.
  if (outcomes.some(o => o.verdict === 'ERROR')) process.exit(1);
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
