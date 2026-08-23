using HyperWhisper.SpeechOutput;
using HyperWhisper.SharedCore;

var passed = 0;

Verify(
    Process("Um, hello new line Hyper whisper.", "en", PortablePostProcessingMode.Off,
        global: [new("Hyper whisper", "HyperWhisper")]),
    "Hello \n\n HyperWhisper. ",
    result => result.FillerWordsChanged && result.VoiceCommandsChanged && result.VocabularyRulesChanged == 1,
    "ordered off-mode processing");

Verify(
    Process("Um, hello new line Hyper whisper.", "en", PortablePostProcessingMode.Cloud,
        global: [new("Hyper whisper", "HyperWhisper")]),
    "Um, hello new line HyperWhisper. ",
    result => !result.FillerWordsChanged && !result.VoiceCommandsChanged && result.VocabularyRulesChanged == 1,
    "post-processing branch skips lightweight cleanup");

Verify(Process("um er bleibt", "de", PortablePostProcessingMode.Off), "um er bleibt ",
    result => !result.FillerWordsChanged, "non-English filler safety");
Verify(Process("um this works", "auto", PortablePostProcessingMode.Off), "um this works ",
    result => !result.FillerWordsChanged, "auto-language filler safety");
Verify(Process("newlines are useful; new paragraphs are too", "en", PortablePostProcessingMode.Off),
    "newlines are useful; new paragraphs are too ", result => !result.VoiceCommandsChanged,
    "voice-command word-boundary safety");
Verify(Process("first new paragraph second", "en", PortablePostProcessingMode.Off),
    "first \n\n second ", result => result.VoiceCommandsChanged,
    "dictated paragraph command");

Verify(
    Process("ray uses hyper whisper", "en", PortablePostProcessingMode.Off,
        global: [new("ray", "Ray"), new("hyper whisper", "$1 HyperWhisper")],
        mode: [new("uses", "USES")]),
    "Ray USES $1 HyperWhisper ",
    result => result.VocabularyRulesChanged == 3,
    "global then mode literal vocabulary replacements");

Verify(
    Process("Cafe\u0301 and cat concatenate", "en", PortablePostProcessingMode.Off,
        global: [new("Café", "CAFÉ"), new("cat", "dog")]),
    "CAFÉ and dog concatenate ",
    result => result.VocabularyRulesChanged == 2,
    "NFC and word-boundary vocabulary behavior");

Verify(Process("Hello.", "en", PortablePostProcessingMode.Off,
        options: new(RemoveFillerWords: false, RemoveTrailingPeriod: true)),
    "Hello ", result => result.TranscriptText == "Hello."
        && result.TrailingPeriodChanged && result.TrailingSpacingChanged,
    "trailing period before spacing");
Verify(Process("Wait...", "en", PortablePostProcessingMode.Off,
        options: new(RemoveFillerWords: false, RemoveTrailingPeriod: true)),
    "Wait... ", result => !result.TrailingPeriodChanged,
    "ellipsis preservation");
Verify(Process("今日は晴れです。", "auto", PortablePostProcessingMode.Off), "今日は晴れです。",
    result => !result.TrailingSpacingChanged, "auto-detected CJK spacing");
Verify(Process("Hello", "ja", PortablePostProcessingMode.Off), "Hello",
    result => !result.TrailingSpacingChanged, "explicit no-space language");

Verify(Process("Hello world", "en", PortablePostProcessingMode.Off,
        options: new(RemoveFillerWords: false, AutocapitalizeInsert: true),
        cursor: PortableCursorContext.MidSentence),
    "hello world ", result => result.AutocapitalizationChanged,
    "mid-sentence autocapitalization");
Verify(Process("API response", "en", PortablePostProcessingMode.Off,
        options: new(RemoveFillerWords: false, AutocapitalizeInsert: true),
        cursor: PortableCursorContext.MidSentence),
    "API response ", result => !result.AutocapitalizationChanged,
    "acronym autocapitalization guard");
foreach (var pronoun in new[] { "I think", "I'm ready", "I’ll go" })
{
    Verify(Process(pronoun, "en", PortablePostProcessingMode.Off,
            options: new(RemoveFillerWords: false, AutocapitalizeInsert: true),
            cursor: PortableCursorContext.MidSentence),
        pronoun + " ", result => !result.AutocapitalizationChanged,
        $"first-person pronoun guard ({pronoun})");
}
Verify(Process("Hello world", "en", PortablePostProcessingMode.Off,
        options: new(RemoveFillerWords: false, AutocapitalizeInsert: true),
        cursor: PortableCursorContext.Unknown),
    "Hello world ", result => !result.AutocapitalizationChanged,
    "unknown cursor context pass-through");

var unsafeFlags = Process("damn unpunctuated text", "en", PortablePostProcessingMode.Off,
    options: new(RemoveFillerWords: false, AppendTrailingSpace: false, Punctuation: false,
        Capitalization: false, ProfanityFilter: true));
Verify(unsafeFlags, "damn unpunctuated text",
    result => !result.PunctuationRequested && !result.CapitalizationRequested && result.ProfanityFilterRequested,
    "provider formatting intent is non-destructive");

Verify(Process("  hello  ", "en", PortablePostProcessingMode.Local,
        global: [new(" ", "ignored"), new("hello", "")]),
    "hello ", result => result.VocabularyRulesChanged == 0,
    "empty vocabulary guards and final trim");

Console.WriteLine($"Speech output verification passed ({passed}/20 scenarios). Rust-owned cleanup/replacement/spacing and safe context hooks match Windows ordering.");

SpeechOutputProcessingResult Process(
    string text,
    string language,
    PortablePostProcessingMode postProcessingMode,
    IReadOnlyList<PortableVocabularyReplacement>? global = null,
    IReadOnlyList<PortableVocabularyReplacement>? mode = null,
    SpeechOutputProcessingOptions? options = null,
    PortableCursorContext cursor = PortableCursorContext.Unknown)
    => SpeechOutputProcessor.Process(new(
        text,
        language,
        postProcessingMode,
        global ?? [],
        mode ?? [],
        options ?? new SpeechOutputProcessingOptions(),
        cursor));

void Verify(
    SpeechOutputProcessingResult result,
    string expected,
    Func<SpeechOutputProcessingResult, bool> metadata,
    string scenario)
{
    if (!string.Equals(result.Text, expected, StringComparison.Ordinal) || !metadata(result))
        throw new InvalidOperationException($"{scenario} failed: expected '{Escape(expected)}', got '{Escape(result.Text)}'.");
    passed++;
}

static string Escape(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal);
