/**
 * Display label for a blog tag chip (#1065).
 *
 * Tags are SEO keywords: Outrank sends them lowercase ("ai transcription",
 * "hyperwhisper"), sometimes as URL slugs ("wispr-flow"), and the code posts
 * set theirs by hand in Title Case ("Comparison", "Spokenly"). The chips are
 * lowercase keywords by design, so a label is lowercase except for the proper
 * nouns below. The raw tag stays the React key and stays in the database.
 *
 * - A slug-shaped tag (no space, has a "-") reads with spaces.
 * - A word with a capital past its first letter ("AI", "macOS", "FluidVoice")
 *   was cased on purpose and is kept as is.
 * - Any other word is lowercased, then a proper noun gets its own casing.
 *   A word is a run of letters, so "hyperwhisper's" and "ai-powered" count.
 *
 * Not CSS `capitalize`: it draws "Ai" and "Iphone". Not a word that is also a
 * common word ("word", "teams"): it would capitalise the keyword too.
 */

const PROPER_NOUNS = new Map<string, string>(
  Object.entries({
    ai: "AI",
    android: "Android",
    asr: "ASR",
    dragon: "Dragon",
    ehr: "EHR",
    english: "English",
    fluidvoice: "FluidVoice",
    gmail: "Gmail",
    handy: "Handy",
    hipaa: "HIPAA",
    hyperwhisper: "HyperWhisper",
    imessage: "iMessage",
    ios: "iOS",
    iphone: "iPhone",
    mac: "Mac",
    macos: "macOS",
    macwhisper: "MacWhisper",
    openai: "OpenAI",
    pc: "PC",
    phi: "PHI",
    python: "Python",
    siri: "Siri",
    spanish: "Spanish",
    speechify: "Speechify",
    spokenly: "Spokenly",
    superwhisper: "Superwhisper",
    voiceink: "VoiceInk",
    whisper: "Whisper",
    windows: "Windows",
    wispr: "Wispr",
    wpm: "WPM",
    zoom: "Zoom",
  }),
);

/**
 * Two-word product names whose words are common words on their own: "flow",
 * "aqua", "voice" and "willow" change only inside these pairs.
 */
const PROPER_PAIRS = new Map<string, string>(
  Object.entries({
    "aqua voice": "Aqua Voice",
    "willow voice": "Willow Voice",
    "wispr flow": "Wispr Flow",
  }),
);

function isDeliberatelyCased(word: string): boolean {
  return /[A-Z]/.test(word.slice(1));
}

/** One run of letters: "hyperwhisper" in "hyperwhisper's", "ai" in "ai-powered". */
function caseWord(word: string): string {
  if (isDeliberatelyCased(word)) return word;
  const lower = word.toLowerCase();

  return PROPER_NOUNS.get(lower) ?? lower;
}

export function formatTagLabel(tag: string): string {
  const trimmed = tag.trim();
  const spaced =
    !/\s/.test(trimmed) && trimmed.includes("-")
      ? trimmed.replaceAll("-", " ")
      : trimmed;
  const words = spaced
    .split(/\s+/)
    .map((word) => word.replace(/[A-Za-z]+/g, caseWord));

  for (let i = 0; i < words.length - 1; i++) {
    const pair = PROPER_PAIRS.get(`${words[i]} ${words[i + 1]}`.toLowerCase());

    if (pair) [words[i], words[i + 1]] = pair.split(" ");
  }

  return words.join(" ");
}
