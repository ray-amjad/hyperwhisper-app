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
// The `SharedPrompts.*` resources are no longer embedded in this assembly and
// the loader helpers that read them are gone — the shared core embeds the
// templates itself. `shared-prompts/` remains the single source of truth,
// consumed by hw-text at build time.
//
// - <<CLEANED>>...<<END>> output wrapping/extraction moved to the shared core's
//   completion policy (see PostProcessingService.EvaluateLlmResponseJson /
//   EvaluateCompletion) — no longer lives here.
// - WrapTranscript stays native

using System.Globalization;
using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.AppClassification;
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

    /// <summary>
    /// Wraps the transcript text with markers for the LLM to process.
    /// </summary>
    public static string WrapTranscript(string transcript) =>
        $"--TRANSCRIPT--\n{transcript}\n--ENDTRANSCRIPT--";
}
