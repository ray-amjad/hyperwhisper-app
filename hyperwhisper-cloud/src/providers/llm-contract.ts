export type ChatMessage = {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content: string;
};

export type CorrectionRequestPayload = {
  messages: ChatMessage[];
  temperature: number;
};

export function buildCorrectionRequest(systemPrompt: string, userContent: string): CorrectionRequestPayload {
  return {
    messages: [
      { role: 'system', content: systemPrompt },
      { role: 'user', content: userContent },
    ],
    temperature: 0,
  };
}
