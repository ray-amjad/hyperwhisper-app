using System;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // STREAMING TRANSCRIPTION SETTINGS
    // =========================================================================

    /// <summary>
    /// Whether the streaming transcription hotkey is active.
    /// Default: false; users explicitly opt in before the streaming shortcut does anything.
    /// </summary>
    public bool StreamingEnabled
    {
        get => _settings.StreamingEnabled ?? false;
        set
        {
            if ((_settings.StreamingEnabled ?? false) != value)
            {
                _settings.StreamingEnabled = value;
                Save();
                LoggingService.Debug($"SettingsService: StreamingEnabled set to: {value}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Selected streaming provider. Valid values map to StreamingTranscriptionProvider storage values.
    /// </summary>
    public string StreamingProvider
    {
        get => string.IsNullOrWhiteSpace(_settings.StreamingProvider)
            ? Models.StreamingTranscriptionProvider.HyperWhisperCloud.StorageValue()
            : _settings.StreamingProvider!;
        set
        {
            var normalized = Models.StreamingTranscriptionProviderExtensions.IsValidStorageValue(value)
                ? value
                : Models.StreamingTranscriptionProvider.HyperWhisperCloud.StorageValue();

            if (_settings.StreamingProvider != normalized)
            {
                _settings.StreamingProvider = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingProvider set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Which vendor HyperWhisper Cloud's live route uses, as a
    /// cloud-stt-catalog.json entry id. Meaningful only while
    /// <see cref="StreamingProvider"/> is "hyperwhisperCloud"; every other
    /// provider ignores it.
    ///
    /// Reuses <c>CloudAccuracyTier</c>'s value space on purpose, so the picker
    /// reuses that tier's existing localized labels and there is no EF migration:
    /// this is the settings JSON, not the Mode table.
    ///
    /// Unset (the state of every install that predates this setting) reads as
    /// <c>deepgramNova3</c>, which derives the exact route those clients already
    /// use. A value outside the live-eligible set is rejected back to that default
    /// rather than persisted, because a tier with no backend WebSocket route would
    /// 404 at dictation time.
    /// </summary>
    public string StreamingCloudTier
    {
        // Validated on READ as well as on write. The setter clamps everything the
        // app itself stores, but settings.json is a plain file a user can edit and
        // a catalog edit can retire a tier that was legitimately persisted months
        // ago. An unclamped read would bind the settings ComboBox to a value with
        // no matching item — WPF renders that as an EMPTY row — while the session
        // silently ran on the fallback tier.
        get => NormalizeStreamingCloudTier(_settings.StreamingCloudTier);
        set
        {
            var normalized = NormalizeStreamingCloudTier(value);

            if (_settings.StreamingCloudTier != normalized)
            {
                _settings.StreamingCloudTier = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingCloudTier set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Clamps a stored tier id to the live-eligible set, returning the CATALOG's
    /// own casing rather than the caller's.
    /// </summary>
    /// <remarks>
    /// The casing matters: the settings ComboBox uses <c>SelectedValuePath="Id"</c>,
    /// and WPF matches <c>SelectedValue</c> with <c>Equals</c> — case-sensitively.
    /// Returning a case-insensitive match verbatim would satisfy the validity
    /// check and still leave the row blank. The macOS counterpart is
    /// <c>HyperWhisperCloudStrategy.normalizedCloudTier</c>.
    /// </remarks>
    internal static string NormalizeStreamingCloudTier(string? value)
    {
        var fallback = Services.Streaming.LiveProtocolStreamingStrategy.DefaultCloudTier;
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var candidate = value.Trim();
        foreach (var entry in AppClassification.CloudSttCatalog.Shared.StreamingCloudTierEntries())
            if (string.Equals(entry.Id, candidate, StringComparison.OrdinalIgnoreCase))
                return entry.Id;
        return fallback;
    }

    /// <summary>
    /// Language code used for streaming transcription. "en" by default.
    /// </summary>
    public string StreamingLanguage
    {
        get => string.IsNullOrWhiteSpace(_settings.StreamingLanguage) ? "en" : _settings.StreamingLanguage!;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "en" : value;
            if (_settings.StreamingLanguage != normalized)
            {
                _settings.StreamingLanguage = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingLanguage set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Deepgram streaming model. Nova-3 general is the default.
    /// Legacy IDs from the 2026-05 catalog cleanup are migrated to
    /// nova-3-general at construction in <see cref="ApplyDefaults"/>.
    /// </summary>
    public string StreamingDeepgramModel
    {
        get => string.IsNullOrWhiteSpace(_settings.StreamingDeepgramModel) ? "nova-3-general" : _settings.StreamingDeepgramModel!;
        set
        {
            var normalized = value is "nova-3-medical" ? "nova-3-medical" : "nova-3-general";
            if (_settings.StreamingDeepgramModel != normalized)
            {
                _settings.StreamingDeepgramModel = normalized;
                Save();
                LoggingService.Debug($"SettingsService: StreamingDeepgramModel set to: {normalized}");
                NotifySettingsChanged();
            }
        }
    }

    /// <summary>
    /// Deepgram no-delay smart formatting. Enabled by default for lower latency.
    /// </summary>
    public bool StreamingFastFormatting
    {
        get => _settings.StreamingFastFormatting ?? true;
        set
        {
            if ((_settings.StreamingFastFormatting ?? true) != value)
            {
                _settings.StreamingFastFormatting = value;
                Save();
                LoggingService.Debug($"SettingsService: StreamingFastFormatting set to: {value}");
                NotifySettingsChanged();
            }
        }
    }
}
