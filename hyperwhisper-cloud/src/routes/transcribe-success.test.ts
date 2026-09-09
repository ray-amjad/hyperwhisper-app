import { describe, expect, test } from 'bun:test';

import type { TranscriptionResult } from '../providers/types';
import { buildTranscriptionSuccess } from './transcribe-success';

function transcript(overrides: Partial<TranscriptionResult> = {}): TranscriptionResult {
  return {
    text: 'hello world',
    language: 'en',
    durationSeconds: 12,
    costUsd: 0.002,
    source: 'deepgram',
    ...overrides,
  };
}

describe('buildTranscriptionSuccess', () => {
  test('projects a primary transcript with its provider, model, cost, and request id', () => {
    const success = buildTranscriptionSuccess({
      result: transcript(),
      requestId: 'req-primary',
      requestedProvider: 'deepgram',
      requestedModel: 'nova-3-general',
      usedModel: 'nova-3-general',
      servedBy: 'deepgram',
      chosenProviderAttempted: true,
      fallbackFrom: undefined,
    });

    expect(success.noSpeech).toBe(false);
    expect(success.billable).toBe(true);
    expect(success.creditsUsed).toBe(2);
    expect(success.providerName).toBe('deepgram/nova-3-general');
    expect(success.response).toMatchObject({
      text: 'hello world',
      language: 'en',
      duration: 12,
      cost: { usd: 0.002, credits: 2 },
      metadata: {
        request_id: 'req-primary',
        stt_provider: 'deepgram/nova-3-general',
        stt_model: 'nova-3-general',
      },
    });
    expect(success.response).not.toHaveProperty('no_speech_detected');
  });

  test('labels a fallback transcript with the provider that answered and the provider that failed', () => {
    const success = buildTranscriptionSuccess({
      result: transcript({ source: 'deepgram' }),
      requestId: 'req-fallback',
      requestedProvider: 'elevenlabs',
      requestedModel: 'scribe_v2',
      usedModel: 'nova-3-general',
      servedBy: 'deepgram',
      chosenProviderAttempted: true,
      fallbackFrom: 'elevenlabs',
    });

    expect(success.providerName)
      .toBe('deepgram/nova-3-general (fallback from elevenlabs/scribe_v2)');
    expect(success.reportedModel).toBe('nova-3-general');
    expect(success.response.metadata.stt_provider).toBe(success.providerName);
    expect(success.response.metadata.stt_model).toBe('nova-3-general');
  });

  test('reports an upstream model substitution instead of the requested model', () => {
    const success = buildTranscriptionSuccess({
      result: transcript({ source: 'assemblyai', model: 'universal-2' }),
      requestId: 'req-model-fallback',
      requestedProvider: 'assemblyai',
      requestedModel: 'universal-3-5-pro',
      usedModel: 'universal-2',
      servedBy: 'assemblyai',
      chosenProviderAttempted: true,
      fallbackFrom: undefined,
    });

    expect(success.providerName).toBe('assemblyai/universal-2');
    expect(success.reportedModel).toBe('universal-2');
    expect(success.response.metadata.stt_model).toBe('universal-2');
  });

  test('keeps a zero-cost no-speech result on the chosen provider and removes fallback attribution', () => {
    const success = buildTranscriptionSuccess({
      result: transcript({ text: '', source: 'no_speech', costUsd: 0 }),
      requestId: 'req-silence',
      requestedProvider: 'elevenlabs',
      requestedModel: 'scribe_v2',
      usedModel: 'nova-3-general',
      servedBy: 'deepgram',
      chosenProviderAttempted: true,
      fallbackFrom: 'elevenlabs',
    });

    expect(success.noSpeech).toBe(true);
    expect(success.billable).toBe(false);
    expect(success.creditsUsed).toBe(0);
    expect(success.providerName).toBe('elevenlabs/scribe_v2');
    expect(success.reportedModel).toBe('scribe_v2');
    expect(success.response.no_speech_detected).toBe(true);
    expect(success.response.cost).toEqual({ usd: 0, credits: 0 });
  });

  test('attributes no-speech to the provider that ran when geo routing dropped the chosen provider', () => {
    const success = buildTranscriptionSuccess({
      result: transcript({ text: '', source: 'no_speech', costUsd: 0 }),
      requestId: 'req-geo-drop',
      requestedProvider: 'elevenlabs',
      requestedModel: 'scribe_v2',
      usedModel: 'nova-3-general',
      servedBy: 'deepgram',
      chosenProviderAttempted: false,
      fallbackFrom: 'elevenlabs',
    });

    expect(success.providerName).toBe('deepgram/nova-3-general');
    expect(success.reportedModel).toBe('nova-3-general');
    expect(success.response.metadata.stt_provider).not.toContain('fallback from');
  });

  test('bills no-speech when the upstream charged for processed audio', () => {
    const success = buildTranscriptionSuccess({
      result: transcript({ text: '', source: 'no_speech', costUsd: 0.0015 }),
      requestId: 'req-billable-silence',
      requestedProvider: 'gemini-transcribe',
      requestedModel: 'gemini-3.5-transcribe',
      usedModel: 'gemini-3.5-transcribe',
      servedBy: 'gemini-transcribe',
      chosenProviderAttempted: true,
      fallbackFrom: undefined,
    });

    expect(success.noSpeech).toBe(true);
    expect(success.billable).toBe(true);
    expect(success.creditsUsed).toBe(1.5);
    expect(success.response.cost).toEqual({ usd: 0.0015, credits: 1.5 });
    expect(success.response.no_speech_detected).toBe(true);
  });
});
