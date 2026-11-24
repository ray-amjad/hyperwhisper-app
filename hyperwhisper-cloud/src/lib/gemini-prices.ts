// GEMINI PRICES SHARED BY BOTH REGISTRIES
// Google prices a Gemini model once, but two registries bill it: the STT rows
// in stt-models.ts (audio sent to :generateContent) and the /post-process rows
// in llm-models.ts (text chat). Each price both of them bill lives here, once,
// so a price change is one edit and the two cannot disagree. A leaf module:
// it imports nothing, so either registry can read it.
//
// USD per token, Standard paid tier, per-1M ÷ 1e6.
// Ref: https://ai.google.dev/gemini-api/docs/pricing

const M = 1_000_000;

export interface GeminiTokenPrice {
  /** Text input (the STT instruction + vocabulary prompt; the whole chat prompt). */
  textInputPerToken: number;
  /** Audio input. Flash 2.5 models bill it above text; 3.x lists one input price. */
  audioInputPerToken: number;
  outputPerToken: number;
}

// #1019. Google lists ONE input price for 3.8 Flash (audio included), and it is
// INTRODUCTORY: $0.75/$3.75 through 2026-12-31, $1.50/$7.50 from 2027-01-01.
// Re-confirm on the first model watch of 2027 and update this price AND the
// pricePerMInput / pricePerMOutput + priceNote on the gemini-3.8-flash rows in
// cloud-pp-catalog.json (and the STT reservation in stt-models.ts) together.
export const GEMINI_3_8_FLASH_PRICE: GeminiTokenPrice = {
  textInputPerToken: 0.75 / M, audioInputPerToken: 0.75 / M, outputPerToken: 3.75 / M,
};

export const GEMINI_2_5_FLASH_PRICE: GeminiTokenPrice = {
  textInputPerToken: 0.30 / M, audioInputPerToken: 1.00 / M, outputPerToken: 2.50 / M,
};

export const GEMINI_2_5_FLASH_LITE_PRICE: GeminiTokenPrice = {
  textInputPerToken: 0.10 / M, audioInputPerToken: 0.30 / M, outputPerToken: 0.40 / M,
};
