// PROMPT BUILDER
// Centralized prompt templates and assembly for AI post-processing.
// Mirrors macOS PromptBuilder.swift for cross-platform consistency.
//
// Prompt ASSEMBLY now lives in the shared Rust core (hw-text). SystemPrompt /
// SystemInfo are thin shims: they map the native (Mode, ApplicationContext,
// vocabulary) inputs into the UniFFI-generated `PromptContext` record and
// delegate to BuildSystemPrompt(ctx) / BuildSystemInfo(ctx). Public signatures
// are unchanged so call sites are untouched.
//
// Host-dependent fields (clock, timezone, locale, computer name, resolved
// language display name) are resolved NATIVELY here — the Rust core has no clock
// or language catalog. focusedElement / focusedContent are pre-processed here
// (mirrors macOS PromptBuilder.swift) so byte-for-byte behaviour is preserved.
//
// The embedded `SharedPrompts.*` resources are now DEAD (the core embeds the
// templates) but are intentionally LEFT in the bundle — deleting them is a
// separate cleanup. The loader helpers below are unused but retained for the
// same reason.
//
// - <<CLEANED>>...<<END>> output wrapping (ExtractCleanedText) stays native
// - WrapTranscript stays native

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Services.AppClassification;
using ApplicationContext = HyperWhisper.Services.ApplicationContext;
// Rust shared-core binding. `Preset`/`HwAppType`/`HwEnglishSpelling`/`PromptContext`
// live here; collisions with HyperWhisper.* types (PresetType, AppType) are avoided
// because those native types use different names — only fully-qualify when ambiguous.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Utilities;

/// <summary>
/// Builds system prompts for AI post-processing based on mode settings.
/// Each preset type has a tailored prompt that guides the LLM on how to enhance transcriptions.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Load a shared preset template from embedded resources.
    /// </summary>
    private static string LoadTemplate(string name)
    {
        var resourceName = $"HyperWhisper.SharedPrompts.presets.{name}.txt";
        return LoadEmbeddedResource(resourceName);
    }

    /// <summary>
    /// Load a shared prompt fragment from embedded resources.
    /// </summary>
    private static string LoadFragment(string name)
    {
        var resourceName = $"HyperWhisper.SharedPrompts.fragments.{name}.txt";
        return LoadEmbeddedResource(resourceName);
    }

    /// <summary>
    /// Load a shared app-aware formatting block from embedded resources.
    /// </summary>
    private static string LoadContextualBlock(string name)
    {
        var resourceName = $"HyperWhisper.SharedPrompts.contextual.{name}.txt";
        string content;
        try
        {
            content = LoadEmbeddedResource(resourceName);
        }
        catch (InvalidOperationException ex)
        {
            LoggingService.Warn($"PromptBuilder: Missing contextual prompt block '{name}': {ex.Message}");
            return "";
        }
        return name == "email"
            ? content.Replace("{{EMAIL_FORMATTING_RULES}}", LoadFragment("email-formatting-rules"))
            : content;
    }

    /// <summary>
    /// Load a shared mode flag from embedded resources.
    /// </summary>
    private static string LoadFlag(string name)
    {
        var resourceName = $"HyperWhisper.SharedPrompts.flags.{name}.txt";
        return LoadEmbeddedResource(resourceName);
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Builds the complete static system prompt for the provided mode.
    /// Dynamic context (time, app context, vocabulary) is NOT included —
    /// use SystemInfo() to get that separately for prompt caching.
    /// </summary>
    /// <param name="mode">The transcription mode containing preset and processing settings.</param>
    /// <param name="applicationContext">Optional pre-captured application context.</param>
    /// <returns>The complete static system prompt string.</returns>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public static string SystemPrompt(
        Mode mode,
        ApplicationContext? applicationContext = null)
    {
        var ctx = MakeContext(mode, applicationContext, vocabulary: null);
        return HyperwhisperCoreMethods.BuildSystemPrompt(ctx);
    }

    /// <summary>
    /// Builds the dynamic system info string (time, timezone, locale, app context, vocabulary, etc.).
    /// This content changes per-request and should be prepended to the user message
    /// so the static system prompt benefits from provider prompt caching.
    /// </summary>
    /// <param name="mode">The transcription mode (used for spelling/language settings).</param>
    /// <param name="vocabulary">Array of custom vocabulary words.</param>
    /// <param name="applicationContext">Optional pre-captured application context.</param>
    /// <returns>The dynamic system info string.</returns>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public static string SystemInfo(
        Mode mode,
        List<string>? vocabulary = null,
        ApplicationContext? applicationContext = null)
    {
        var ctx = MakeContext(mode, applicationContext, vocabulary);
        return HyperwhisperCoreMethods.BuildSystemInfo(ctx);
    }

    // =========================================================================
    // SHARED-CORE PROMPT CONTEXT
    //
    // Builds the UniFFI `PromptContext` from native inputs. Host-dependent fields
    // (time/timezone/locale/computer name + the resolved language display name)
    // are filled here; the Rust core fills the rest of the template. focusedElement
    // / focusedContent are PRE-PROCESSED to match the prior native formatting.
    // Mirrors macOS PromptBuilder.makeContext(...).
    // =========================================================================

    // TODO-verify (Windows/CI): Rust shared-core swap.
    private static uniffi.hyperwhisper_core.PromptContext MakeContext(
        Mode mode,
        ApplicationContext? applicationContext,
        List<string>? vocabulary)
    {
        // Use the passed context if available, otherwise gather fresh. Both real
        // call sites pass a context, so this gather-when-null branch is defensive;
        // it intentionally unifies Windows with macOS PromptBuilder.makeContext,
        // which also gathers when nil. (Windows HEAD passed null through, but the
        // `@hasApplicationContext` gate makes the assembled output equivalent.)
        var appContext = applicationContext
            ?? ApplicationContextService.Instance.GatherContext();

        var preset = PresetFromNative(PresetTypeExtensions.FromString(mode.Preset));
        var customInstructions = preset == uniffi.hyperwhisper_core.Preset.Custom
            ? (mode.CustomInstructions ?? "")
            : "";

        // Resolve the language display name natively (Rust has no language catalog).
        var resolvedLanguage = "";
        if (!string.IsNullOrEmpty(mode.Language) && mode.Language != "auto")
        {
            resolvedLanguage = GetLanguageDisplayName(mode.Language);
        }

        // Focused element: Windows captures a single simplified element-type string
        // (no AX role/title split like macOS), so pass it through verbatim.
        var focusedElement = appContext?.FocusedElementType ?? "";

        // Focused content: pre-truncate to 100 source chars (the core does NOT
        // truncate — keeping this native prevents full field content leaking).
        // Windows already truncates at capture, but re-apply for safety/parity.
        var focusedContent = "";
        var rawFocused = appContext?.FocusedContent;
        if (!string.IsNullOrEmpty(rawFocused))
        {
            focusedContent = rawFocused.Length > 100
                ? rawFocused.Substring(0, 100) + "..."
                : rawFocused;
        }

        // RAW vocabulary words (core's build_system_info sanitizes/joins — do NOT
        // pre-sanitize here). Drop empties to mirror macOS's compactMap.
        var vocabularyWords = (vocabulary ?? new List<string>())
            .Where(w => !string.IsNullOrEmpty(w))
            .ToList();

        var appName = appContext?.ProcessName ?? "";

        return new uniffi.hyperwhisper_core.PromptContext(
            @preset: preset,
            @customInstructions: customInstructions,
            @englishSpelling: HyperwhisperCoreMethods.EnglishSpellingFromRaw(mode.EnglishSpelling ?? ""),
            @language: resolvedLanguage,
            @userSystemPrompt: mode.UserSystemPrompt ?? "",
            @appType: HwAppTypeFromNative(appContext?.AppType ?? AppType.Other),
            @appName: appName,
            @category: appContext?.Category ?? "",
            // Windows ApplicationContext carries no free-text "description" field.
            @description: "",
            @textFormat: appContext?.TextFormat ?? "",
            @browserHost: appContext?.BrowserHost ?? "",
            @browserTabTitle: appContext?.BrowserTabTitle ?? "",
            @focusedElement: focusedElement,
            @focusedContent: focusedContent,
            @screenOcrText: appContext?.ScreenOCRText ?? "",
            @appTypeConfidence: appContext?.AppTypeConfidence ?? "unknown",
            @appTypeSource: appContext?.AppTypeSource ?? "default",
            @hasApplicationContext: !string.IsNullOrEmpty(appName),
            @vocabularyWords: vocabularyWords,
            // Host-resolved fields. Preserve the existing Windows on-wire values:
            // short time, TimeZoneInfo.Local.StandardName, CurrentCulture.Name,
            // Environment.MachineName (these differ from macOS's chosen formats by
            // platform convention — flagged for the parity reviewer).
            @time: DateTime.Now.ToString("t", CultureInfo.CurrentCulture),
            @timezone: TimeZoneInfo.Local.StandardName,
            @locale: CultureInfo.CurrentCulture.Name,
            @computerName: Environment.MachineName,
            @punctuation: mode.Punctuation,
            @capitalization: mode.Capitalization,
            @profanityFilter: mode.ProfanityFilter
        );
    }

    /// <summary>Map the native <see cref="PresetType"/> to the shared-core <c>Preset</c>.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    private static uniffi.hyperwhisper_core.Preset PresetFromNative(PresetType preset) => preset switch
    {
        PresetType.Hyper => uniffi.hyperwhisper_core.Preset.Hyper,
        PresetType.Message => uniffi.hyperwhisper_core.Preset.Message,
        PresetType.Mail => uniffi.hyperwhisper_core.Preset.Mail,
        PresetType.Note => uniffi.hyperwhisper_core.Preset.Note,
        PresetType.Meeting => uniffi.hyperwhisper_core.Preset.Meeting,
        PresetType.Code => uniffi.hyperwhisper_core.Preset.Code,
        PresetType.Custom => uniffi.hyperwhisper_core.Preset.Custom,
        _ => uniffi.hyperwhisper_core.Preset.Hyper
    };

    /// <summary>Map the native <see cref="AppType"/> to the shared-core <c>HwAppType</c>.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    private static HwAppType HwAppTypeFromNative(AppType appType) => appType switch
    {
        AppType.Email => HwAppType.Email,
        AppType.Ai => HwAppType.Ai,
        AppType.WorkMessaging => HwAppType.WorkMessaging,
        AppType.PersonalMessaging => HwAppType.PersonalMessaging,
        AppType.Document => HwAppType.Document,
        AppType.Code => HwAppType.Code,
        AppType.Terminal => HwAppType.Terminal,
        AppType.Sensitive => HwAppType.Sensitive,
        AppType.Other => HwAppType.Other,
        _ => HwAppType.Other
    };

    /// <summary>
    /// Neutralizes a vocabulary word for safe interpolation into the prompt.
    /// Delegates to the shared Rust core so macOS/Windows sanitize identically.
    /// </summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public static string SanitizeVocabularyWord(string word)
        => HyperwhisperCoreMethods.SanitizeVocabularyWord(word);

    /// <summary>
    /// Generate the static system prompt for a specific preset.
    /// Dynamic context is NOT included here — use SystemInfo() separately.
    /// </summary>
    private static string PromptForPreset(
        PresetType preset,
        string? customInstructions = null,
        ApplicationContext? applicationContext = null)
    {
        return preset switch
        {
            PresetType.Hyper => BuildHyperPrompt(applicationContext),
            PresetType.Message => ApplyContextualFormatting(
                LoadTemplate("message"),
                ContextualFormattingBlock(PresetType.Message, applicationContext)),
            PresetType.Mail => BuildMailPrompt(),
            PresetType.Note => ApplyContextualFormatting(
                LoadTemplate("note"),
                ContextualFormattingBlock(PresetType.Note, applicationContext)),
            PresetType.Meeting => ApplyContextualFormatting(
                LoadTemplate("meeting"),
                ContextualFormattingBlock(PresetType.Meeting, applicationContext)),
            PresetType.Code => ApplyContextualFormatting(
                LoadTemplate("code"),
                ContextualFormattingBlock(PresetType.Code, applicationContext)),
            PresetType.Custom => ApplyContextualFormatting(
                BuildCustomPrompt(customInstructions),
                ContextualFormattingBlock(PresetType.Custom, applicationContext)),
            _ => BuildHyperPrompt(applicationContext)
        };
    }

    private static string GetSpellingInstructions(string? englishSpelling)
    {
        if (string.IsNullOrEmpty(englishSpelling)) return "";

        return englishSpelling.ToLowerInvariant() switch
        {
            "british" => """

            <SPELLING>British English (e.g., colour, realise, organisation, centre, travelled)</SPELLING>
            <DATE_FORMAT>Use British date format: DD/MM/YYYY (e.g., 25/12/2025) or "25 December 2025" / "25th December 2025"</DATE_FORMAT>
            """,
            "australian" => """

            <SPELLING>Australian English (e.g., colour, realise, organisation, centre, travelled)</SPELLING>
            <DATE_FORMAT>Use Australian date format: DD/MM/YYYY (e.g., 25/12/2025) or "25 December 2025" / "25th December 2025"</DATE_FORMAT>
            """,
            "canadian" => """

            <SPELLING>Canadian English (e.g., colour, realize, organization, centre, travelled)</SPELLING>
            <DATE_FORMAT>Use Canadian date format: DD/MM/YYYY or YYYY-MM-DD (e.g., 25/12/2025 or 2025-12-25) or "25 December 2025"</DATE_FORMAT>
            """,
            _ => """

            <SPELLING>American English (e.g., color, realize, organization, center, traveled)</SPELLING>
            <DATE_FORMAT>Use American date format: MM/DD/YYYY (e.g., 12/25/2025) or "December 25, 2025"</DATE_FORMAT>
            """
        };
    }

    private static string GetLanguageRequirements(string? modeLanguage)
    {
        if (!string.IsNullOrEmpty(modeLanguage) && modeLanguage != "auto")
        {
            // Explicit language set - output must be in that language
            var displayName = GetLanguageDisplayName(modeLanguage);
            return $"""
                <LANGUAGE_REQUIREMENTS>
                - Output ALL text in {displayName}, including headings, labels, and content
                - Do NOT mix languages (e.g., English headings with {displayName} content)
                - Preserve names, technical terms, and proper nouns as spoken
                </LANGUAGE_REQUIREMENTS>
                """;
        }
        else
        {
            // Auto-detect - match the transcript's language
            return """
                <LANGUAGE_REQUIREMENTS>
                - Output in the SAME language as the transcript
                - If multiple languages are used, keep each section in its original language
                - Preserve names, technical terms, and proper nouns as spoken
                </LANGUAGE_REQUIREMENTS>
                """;
        }
    }

    private static string GetLanguageDisplayName(string languageCode)
    {
        try
        {
            var culture = new CultureInfo(languageCode);
            return culture.DisplayName;
        }
        catch
        {
            return languageCode;
        }
    }

    private static string BuildSystemInfo(
        string spellingInstructions,
        string languageRequirements,
        List<string>? vocabulary,
        ApplicationContext? applicationContext = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"""
            <SYSTEM_INFO>
            <TIME>{DateTime.Now:t}</TIME>
            <TIMEZONE>{TimeZoneInfo.Local.StandardName}</TIMEZONE>
            <LOCALE>{CultureInfo.CurrentCulture.Name}</LOCALE>
            <COMPUTER>{Environment.MachineName}</COMPUTER>
            {spellingInstructions}
            </SYSTEM_INFO>

            {languageRequirements}
            """);

        // Application context section
        if (applicationContext != null)
        {
            sb.AppendLine("\n<APPLICATION_CONTEXT>");
            sb.AppendLine($"<APP>{applicationContext.ProcessName}</APP>");

            if (!string.IsNullOrEmpty(applicationContext.BrowserTabTitle))
                sb.AppendLine($"<TAB>{applicationContext.BrowserTabTitle}</TAB>");

            if (!string.IsNullOrEmpty(applicationContext.BrowserHost))
                sb.AppendLine($"<BROWSER_HOST>{applicationContext.BrowserHost}</BROWSER_HOST>");

            if (!string.IsNullOrEmpty(applicationContext.Category))
                sb.AppendLine($"<CATEGORY>{applicationContext.Category}</CATEGORY>");

            sb.AppendLine($"<APP_TYPE>{applicationContext.AppType.ToPromptValue()}</APP_TYPE>");
            sb.AppendLine($"<APP_TYPE_CONFIDENCE>{applicationContext.AppTypeConfidence}</APP_TYPE_CONFIDENCE>");
            sb.AppendLine($"<APP_TYPE_SOURCE>{applicationContext.AppTypeSource}</APP_TYPE_SOURCE>");

            if (!string.IsNullOrEmpty(applicationContext.TextFormat))
                sb.AppendLine($"<TEXT_FORMAT>{applicationContext.TextFormat}</TEXT_FORMAT>");

            if (!string.IsNullOrEmpty(applicationContext.FocusedElementType))
                sb.AppendLine($"<FOCUSED_ELEMENT>{applicationContext.FocusedElementType}</FOCUSED_ELEMENT>");

            // Strip FOCUSED_CONTENT when screen OCR text is present (avoids duplication)
            if (applicationContext.AppType != AppType.Sensitive
                && string.IsNullOrEmpty(applicationContext.ScreenOCRText))
            {
                if (!string.IsNullOrEmpty(applicationContext.FocusedContent))
                    sb.AppendLine($"<FOCUSED_CONTENT>{applicationContext.FocusedContent}</FOCUSED_CONTENT>");
            }

            sb.AppendLine("</APPLICATION_CONTEXT>");

            // Screen OCR context — visible text captured from the active monitor
            if (applicationContext.AppType != AppType.Sensitive
                && !string.IsNullOrEmpty(applicationContext.ScreenOCRText))
            {
                sb.AppendLine($"\n<SCREEN_CONTEXT>\n{applicationContext.ScreenOCRText}\n</SCREEN_CONTEXT>");
            }
        }

        // Custom vocabulary section
        if (vocabulary != null && vocabulary.Count > 0)
        {
            var words = vocabulary.Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (words.Count > 0)
            {
                sb.AppendLine($"\n<CUSTOM_VOCABULARY>\n{string.Join(", ", words)}\n</CUSTOM_VOCABULARY>");
            }
        }

        return sb.ToString();
    }

    private static string FinalizePrompt(string basePrompt, Mode mode)
    {
        var overrideDirective = LoadFragment("override-directive");
        var antiReplyDirective = LoadFragment("anti-reply-directive");
        var numberFormatting = LoadFragment("number-formatting");

        var sb = new StringBuilder();
        sb.AppendLine(overrideDirective);

        var userPrompt = mode.UserSystemPrompt?.Trim();
        if (!string.IsNullOrEmpty(userPrompt))
        {
            sb.AppendLine($"\n<USER_SYSTEM_PROMPT>\n{userPrompt}\n</USER_SYSTEM_PROMPT>");
        }

        sb.AppendLine(antiReplyDirective);
        sb.AppendLine(numberFormatting);
        sb.AppendLine(basePrompt);
        sb.AppendLine(AddModeFlags(mode));

        return sb.ToString();
    }

    /// <summary>
    /// Builds the processing settings block based on mode flags.
    /// </summary>
    private static string AddModeFlags(Mode mode)
    {
        var sb = new StringBuilder("\n<MODE_FLAGS>");

        if (mode.Punctuation)
        {
            sb.AppendLine($"\n{LoadFlag("punctuation")}");
        }
        if (mode.Capitalization)
        {
            sb.AppendLine($"\n{LoadFlag("capitalization")}");
        }
        if (mode.ProfanityFilter)
        {
            sb.AppendLine($"\n{LoadFlag("profanity-filter")}");
        }

        sb.Append("\n</MODE_FLAGS>");
        return sb.ToString();
    }

    // =========================================================================
    // PRESET-SPECIFIC PROMPTS
    // Each preset loads a shared template from embedded resources and
    // substitutes placeholders. These match the macOS PromptBuilder.swift.
    // =========================================================================

    private static string BuildHyperPrompt(ApplicationContext? applicationContext = null)
    {
        return ApplyContextualFormatting(
            LoadTemplate("hyper"),
            ContextualFormattingBlock(PresetType.Hyper, applicationContext));
    }

    private static string BuildMailPrompt() =>
        LoadTemplate("mail")
            .Replace("{{EMAIL_FORMATTING_RULES}}", LoadFragment("email-formatting-rules"));

    private static string BuildCustomPrompt(string? customInstructions)
    {
        var customPrompt = customInstructions ?? "Process the text according to your best judgment.";
        return LoadTemplate("custom")
            .Replace("{{CUSTOM_INSTRUCTIONS}}", customPrompt);
    }

    private static string ApplyContextualFormatting(string template, string block) =>
        template
            .Replace("{{CONTEXTUAL_FORMATTING_BLOCK}}", block)
            .Replace("{{EMAIL_BLOCK}}", block);

    private static string ContextualFormattingBlock(PresetType preset, ApplicationContext? applicationContext)
    {
        var appType = applicationContext?.AppType ?? AppType.Other;

        return preset switch
        {
            PresetType.Hyper => BlockFor(appType),
            PresetType.Message => appType == AppType.WorkMessaging
                ? LoadContextualBlock("work-message")
                : LoadContextualBlock("personal-message"),
            PresetType.Note or PresetType.Meeting => appType switch
            {
                AppType.Code => LoadContextualBlock("code"),
                AppType.Terminal => LoadContextualBlock("terminal"),
                _ => LoadContextualBlock("document")
            },
            PresetType.Code => appType == AppType.Terminal
                ? LoadContextualBlock("terminal")
                : LoadContextualBlock("code"),
            PresetType.Custom => BlockFor(appType),
            _ => ""
        };
    }

    private static string BlockFor(AppType appType) => appType switch
    {
        AppType.Email => LoadContextualBlock("email"),
        AppType.WorkMessaging => LoadContextualBlock("work-message"),
        AppType.PersonalMessaging => LoadContextualBlock("personal-message"),
        AppType.Document => LoadContextualBlock("document"),
        AppType.Code or AppType.Ai => LoadContextualBlock("code"),
        AppType.Terminal => LoadContextualBlock("terminal"),
        _ => ""
    };

    /// <summary>
    /// Wraps the transcript text with markers for the LLM to process.
    /// </summary>
    public static string WrapTranscript(string transcript) =>
        $"--TRANSCRIPT--\n{transcript}\n--ENDTRANSCRIPT--";

    /// <summary>
    /// Extracts the cleaned text from the LLM response.
    /// Looks for text between <<CLEANED>> and <<END>> markers.
    /// </summary>
    private static readonly string[] StartVariants = ["<<CLEANED>>", "<<CLEANED>", "<CLEANED>>", "<CLEANED>", "<</CLEANED>>"];
    private static readonly string[] EndVariants = ["<<END>>", "<<END>", "<END>>", "<END>", "<</END>>"];

    public static string ExtractCleanedText(string response)
    {
        var trimmed = response;

        // Find earliest start variant
        int bestStart = -1;
        int bestStartLen = 0;
        foreach (var tag in StartVariants)
        {
            var idx = trimmed.IndexOf(tag, StringComparison.Ordinal);
            if (idx >= 0 && (bestStart < 0 || idx < bestStart))
            {
                bestStart = idx;
                bestStartLen = tag.Length;
            }
        }

        if (bestStart < 0)
        {
            // No <<CLEANED>> start marker => the model did not follow the wrapping contract.
            // Returning the raw response here would leak the system prompt / app-context /
            // screen-OCR text as the user's transcription. Return empty so the caller treats it
            // as a failed extraction and keeps the original transcription.
            return string.Empty;
        }

        // Find earliest end variant after start
        var afterStart = bestStart + bestStartLen;
        int bestEnd = -1;
        foreach (var tag in EndVariants)
        {
            var idx = trimmed.IndexOf(tag, afterStart, StringComparison.Ordinal);
            if (idx >= 0 && (bestEnd < 0 || idx < bestEnd))
            {
                bestEnd = idx;
            }
        }

        string inner;
        if (bestEnd >= 0)
        {
            inner = trimmed[afterStart..bestEnd];
        }
        else
        {
            inner = trimmed[afterStart..];
        }

        // Final cleanup pass for residual markers
        var result = inner;
        foreach (var tag in StartVariants)
            result = result.Replace(tag, "");
        foreach (var tag in EndVariants)
            result = result.Replace(tag, "");

        return result.Trim();
    }

    /// <summary>
    /// Extract the cleaned text, falling back to a lenient marker-strip when the
    /// model omitted the strict &lt;&lt;CLEANED&gt;&gt; wrapper. Returns empty only
    /// when even the lenient strip yields nothing — so a model that ignores the
    /// wrapping contract no longer silently loses ALL post-processing (it would
    /// otherwise fall back to the raw transcript). Mirrors the macOS
    /// AIPostProcessor lenient fallback via the core's strip_wrapper_markers.
    /// </summary>
    public static string ExtractCleanedTextLenient(string response)
    {
        var strict = ExtractCleanedText(response);
        if (!string.IsNullOrWhiteSpace(strict))
        {
            return strict;
        }
        return HyperwhisperCoreMethods.StripWrapperMarkers(response).Trim();
    }
}
