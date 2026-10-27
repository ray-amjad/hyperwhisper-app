using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

// The HyperWhisper Cloud company / tier / model pickers, read from the shared cloud STT catalog.
public sealed partial class ModesViewModel
{
    /// <summary>
    /// Every model the selected COMPANY offers, across all of its tiers — so the merged
    /// Google row lists Gemini 3.5 Transcribe beside the Gemini family, exactly as macOS
    /// and Windows list them. The list used to hold one tier's models only.
    ///
    /// Cached per company, and the cache is what keeps the SELECTION: a getter that built a
    /// fresh list on every read handed the picker a new ItemsSource mid-commit, and the
    /// selection it was committing was no longer an item of it.
    /// </summary>
    public IReadOnlyList<string> CloudTierModels =>
        GroupForTier(_cloudAccuracyTier) is { } group ? VendorModelIds[group.VendorKey] : [];

    /// <summary>
    /// The model the Model row shows, and the tier it moves the mode onto.
    ///
    /// A company row can span tiers, so the MODEL decides the tier — picking a Gemini
    /// family model under "Google" moves the mode off <c>geminiTranscribe</c> and onto
    /// <c>gemini</c>, which is a different upstream route and different credits. Windows
    /// resolves it the same way, from the item's own tier id (ModeEditorWindow.xaml.cs:253).
    ///
    /// The getter falls back to the tier default rather than to null. A HyperWhisper Cloud
    /// mode that never chose a model stores an empty id, and the old getter drew an empty
    /// row for it — the user saw no model at all and could not tell which one would run.
    /// The fallback is display only: Save still writes null for an unchosen model, and the
    /// send path still resolves the tier default from the same catalog.
    /// </summary>
    public string? CloudTierModel
    {
        get
        {
            if (!IsHwCloudSource) return null;
            // The stored model shows only when the CURRENT tier owns it. The list holds the
            // whole company, so a mode saved with tier `geminiTranscribe` and model
            // `gemini-2.5-flash` would draw Gemini 2.5 Flash while the router ran the tier
            // default. Show what will run instead.
            if (TierOffers(_cloudAccuracyTier, _transcriptionModel)) return _transcriptionModel;
            return DefaultModelForTier(_cloudAccuracyTier);
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // A ComboBox clears its selection when the ItemsSource swaps to a list that
                // does not hold it, and writes that null back through the two-way binding.
                // Changing company does exactly that, and the write lands AFTER the change
                // notification. Dropping it silently was not enough: the model stayed set and
                // the row still drew EMPTY, measured on the Linux head 2026-09-20 with Google
                // selected. Re-assert, so the picker reads the value back.
                Notify(nameof(CloudTierModel));
                return;
            }
            if (!TierOffers(_cloudAccuracyTier, _transcriptionModel)
                && string.Equals(value, DefaultModelForTier(_cloudAccuracyTier), StringComparison.Ordinal))
            {
                // The getter's display fallback, echoed back. The dialog re-applies the
                // selection after a list change, and the two-way binding writes that value
                // here. The stored model is one this tier does not run — blank, the on-device
                // placeholder "base", or another tier's id — so the tier default runs either
                // way. Storing it would save a model the user never picked.
                return;
            }
            var owner = (GroupForTier(_cloudAccuracyTier)?.Models ?? [])
                .FirstOrDefault(m => string.Equals(m.ModelId, value, StringComparison.Ordinal));
            var tierMoved = false;
            if (owner is not null && !string.Equals(owner.TierId, _cloudAccuracyTier, StringComparison.Ordinal))
            {
                // Assign the field, not the property: the property's setter notifies
                // CloudTierModels, and a list change mid-write makes the picker re-read a
                // selection that is not written yet.
                _cloudAccuracyTier = owner.TierId;
                NormalizeDictationDomain();
                Notify(nameof(CloudAccuracyTier));
                tierMoved = true;
            }
            var modelChanged = !string.Equals(_transcriptionModel, value, StringComparison.Ordinal);
            TranscriptionModel = value;
            // The model setter notifies the reveals only when the id CHANGES. A mode that
            // already stored this id under another tier moves tier here with the same id,
            // and the credits, vocabulary and dictation rows must still follow the tier.
            if (tierMoved && !modelChanged) NotifyEditorReveals();
        }
    }

    // =====================================================================================
    // HYPERWHISPER CLOUD — the Provider row is a COMPANY, read from the catalog (#837).
    //
    // This used to be a hand-kept array of 12 tier ids, and the picker labelled each one
    // from `modes.cloudAccuracy.<tierId>.label` in 80 translation files. So a company name
    // was written 81 times, and Linux showed "Grok STT" where macOS and Windows showed
    // "SpaceXAI". The same call the other 2 heads already make answers both questions.
    // =====================================================================================

    /// <summary>
    /// The company rows, in catalog order. Read once: the catalog is compiled into the
    /// shared core, so it cannot change while the app runs.
    /// </summary>
    private static readonly IReadOnlyList<SharedCoreBridge.CloudSttVendorGroup> VendorGroups =
        LoadVendorGroups();

    /// <summary>
    /// Reads the company rows, and answers an empty list when the native core cannot.
    ///
    /// This runs from a static initializer. An exception here becomes a cached
    /// <c>TypeInitializationException</c>, and every later touch of this type throws it, so
    /// the whole Modes page dies, not only the cloud pickers. Windows guards the same call
    /// the same way (CloudSttCatalog.Load). With no rows the page still loads and the Provider
    /// picker is empty. Actions that need the core still fail, one at a time, and report it.
    /// </summary>
    private static IReadOnlyList<SharedCoreBridge.CloudSttVendorGroup> LoadVendorGroups()
    {
        try
        {
            return SharedCoreBridge.CloudSttVendorGroups();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"Cloud STT vendor groups failed to load from the shared core: {exception}");
            return [];
        }
    }

    /// <summary>
    /// One model-id list per company, built once. Backs <see cref="CloudTierModels"/>; see
    /// that property for why the list has to be the SAME instance on every read.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> VendorModelIds =
        VendorGroups.ToDictionary(
            g => g.VendorKey,
            g => (IReadOnlyList<string>)[.. g.Models.Select(m => m.ModelId)],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The Provider picker's rows: one <c>vendor</c> key per company.</summary>
    public IReadOnlyList<string> CloudVendors { get; } = [.. VendorGroups.Select(g => g.VendorKey)];

    /// <summary>
    /// Every cloud accuracy tier the catalog offers — the allow-list Save checks a
    /// canonicalised tier against. A company can own several (Google holds
    /// <c>geminiTranscribe</c> and <c>gemini</c>), so this is the flattened set, not one
    /// entry per company.
    /// </summary>
    public IReadOnlyList<string> CloudAccuracyTiers { get; } = [.. VendorGroups.SelectMany(g => g.TierIds)];

    /// <summary>True when <paramref name="tierId"/> itself offers <paramref name="modelId"/>.</summary>
    private static bool TierOffers(string? tierId, string? modelId) =>
        !string.IsNullOrEmpty(modelId)
        && (GroupForTier(tierId)?.Models ?? []).Any(m =>
            string.Equals(m.TierId, tierId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ModelId, modelId, StringComparison.Ordinal));

    /// <summary>The company that owns <paramref name="tierId"/>, or null.</summary>
    private static SharedCoreBridge.CloudSttVendorGroup? GroupForTier(string? tierId) =>
        string.IsNullOrEmpty(tierId)
            ? null
            : VendorGroups.FirstOrDefault(
                g => g.TierIds.Contains(tierId, StringComparer.OrdinalIgnoreCase));

    /// <summary>The company with this <c>vendor</c> key, or null.</summary>
    private static SharedCoreBridge.CloudSttVendorGroup? GroupForVendor(string? vendorKey) =>
        string.IsNullOrEmpty(vendorKey)
            ? null
            : VendorGroups.FirstOrDefault(
                g => string.Equals(g.VendorKey, vendorKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The company the Provider row shows. Derived from the saved TIER, because the tier is
    /// what the mode stores and what the send path puts in <c>X-STT-Provider</c>.
    ///
    /// Setting it moves the mode onto that company's default tier and default model, which
    /// is what Windows does on the same change (ModeEditorWindow.xaml.cs:749-758). Writing
    /// the model here is also what stops the Model row going blank: it used to show nothing
    /// at all whenever the mode's stored model was not one this tier offers, which is every
    /// HyperWhisper Cloud mode that never chose a model.
    /// </summary>
    public string CloudVendor
    {
        get => GroupForTier(_cloudAccuracyTier)?.VendorKey ?? string.Empty;
        set
        {
            var group = GroupForVendor(value);
            if (group is null || string.Equals(group.VendorKey, CloudVendor, StringComparison.OrdinalIgnoreCase))
                return;
            CloudAccuracyTier = group.DefaultTierId;
            TranscriptionModel = DefaultModelForTier(group.DefaultTierId) ?? string.Empty;
            Notify(nameof(CloudVendor));
        }
    }

    /// <summary>
    /// The tier's own default model id, when the tier still offers it. Null when the
    /// catalog names no default — the caller then falls back to the group's first model.
    /// </summary>
    private static string? DefaultModelForTier(string tierId)
    {
        var models = GroupForTier(tierId)?.Models ?? [];
        // No rows means no native core. Do not ask it for a default.
        if (models.Count == 0) return null;
        var preferred = SharedCoreBridge.CloudSttDefaultModel(tierId);
        return models.FirstOrDefault(
                   m => string.Equals(m.ModelId, preferred, StringComparison.Ordinal))?.ModelId
               ?? models.FirstOrDefault(m => string.Equals(m.TierId, tierId, StringComparison.Ordinal))?.ModelId;
    }
    public IReadOnlyList<string> CloudDomains { get; } = ["", "medical"];
}
