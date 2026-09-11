using System.Diagnostics;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using HyperWhisper.SpeechOutput;
using HyperWhisper.TranscriptionRouting;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

public sealed partial class ApplicationLocalApiBackend
{
    public async ValueTask<IReadOnlyList<JsonElement>> GetModesAsync(CancellationToken cancellationToken)
        => (await _modes.ListAsync(cancellationToken).ConfigureAwait(false)).Select(ToModeJson).ToList();

    public async ValueTask<JsonElement?> GetModeAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var modeId)) return null;
        var mode = (await _modes.ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == modeId);
        return mode is null ? null : ToModeJson(mode);
    }

    public async ValueTask<JsonElement> CreateModeAsync(JsonElement document, CancellationToken cancellationToken)
    {
        var existing = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        var mode = new Mode { Id = Guid.NewGuid(), IsDefault = existing.Count == 0, CreatedDate = DateTime.UtcNow, ModifiedDate = DateTime.UtcNow };
        var facts = ApplyModeDocument(mode, document, allowIdentity: false);
        if (existing.Count == 0) mode.IsDefault = true;
        ApplyInferredAccuracyTier(mode, facts, HwLocalApiModeOperation.Create);
        NormalizeMode(mode);
        ValidateMode(mode, facts, HwLocalApiModeOperation.Create);
        EnsureUniqueName(mode, existing, HwLocalApiModeOperation.Create);
        // The clear-others pass this branch hand-rolled is one of the four
        // separate answers issue #536 replaced with a single owner. Keep the
        // shared one.
        await ApplyDefaultModeInvariantAsync(existing, mode, cancellationToken).ConfigureAwait(false);
        await _modes.UpsertAsync(mode, cancellationToken).ConfigureAwait(false);
        return ToModeJson(mode);
    }

    public async ValueTask<JsonElement?> PatchModeAsync(string id, JsonElement patch, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var modeId)) return null;
        var mode = (await _modes.ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == modeId);
        if (mode is null) return null;
        var existing = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        // Both halves of the invariant are read off the mode as it stands BEFORE
        // the patch is applied (issue #536).
        var wasDefault = mode.IsDefault;
        var storedName = mode.Name;
        var facts = ApplyModeDocument(mode, patch, allowIdentity: false);
        ApplyInferredAccuracyTier(mode, facts, HwLocalApiModeOperation.Patch);
        NormalizeMode(mode);
        mode.ModifiedDate = DateTime.UtcNow;
        ValidateMode(mode, facts, HwLocalApiModeOperation.Patch);
        EnsureUniqueName(mode, existing, HwLocalApiModeOperation.Patch);
        if (SharedCoreBridge.CheckModeNameChange(wasDefault, storedName, mode.Name)
            == PortableModeNameChange.RejectedDefaultIsFixed)
            throw new ArgumentException("The default mode's name cannot be changed.");
        if (DefaultModePolicy.CheckDefaultFlag(existing, mode.Id, mode.IsDefault)
            == PortableDefaultFlagChange.RejectedLastDefault)
            throw new ArgumentException("At least one mode must remain the default.");
        await ApplyDefaultModeInvariantAsync(existing, mode, cancellationToken).ConfigureAwait(false);
        await _modes.UpsertAsync(mode, cancellationToken).ConfigureAwait(false);
        return ToModeJson(mode);
    }

    public async ValueTask<bool> DeleteModeAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var modeId)) return false;
        var existing = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        var mode = existing.SingleOrDefault(item => item.Id == modeId);
        if (mode is null) return false;
        if (existing.Count == 1) throw new ArgumentException("Cannot delete the last remaining mode.");
        if (!await _modes.DeleteAsync(modeId, cancellationToken).ConfigureAwait(false)) return false;
        // Deleting the default moves the flag rather than leaving none (#536).
        var remaining = existing.Where(item => item.Id != modeId).ToList();
        await SaveDefaultModeRepairAsync(remaining, null, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Make exactly one mode the default across <paramref name="existing"/> plus
    /// the row about to be written, and persist every OTHER row the decision
    /// changed. The caller upserts <paramref name="pending"/> itself, so its own
    /// flag lands with the rest of its fields in one write.
    /// </summary>
    /// <remarks>
    /// The decision — including which mode is promoted when a restore left none
    /// flagged — comes from the shared core (issue #536), so this head, the
    /// Windows head and macOS all choose the same one.
    /// </remarks>
    private async Task ApplyDefaultModeInvariantAsync(
        IReadOnlyList<Mode> existing,
        Mode pending,
        CancellationToken cancellationToken)
    {
        var all = existing.Where(item => item.Id != pending.Id).Append(pending).ToList();
        await SaveDefaultModeRepairAsync(
            all,
            pending.IsDefault ? pending.Id : null,
            pending.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveDefaultModeRepairAsync(
        IReadOnlyList<Mode> all,
        Guid? preferred,
        Guid? skipUpsert,
        CancellationToken cancellationToken)
    {
        var ordered = all.OrderBy(item => item.SortOrder).ToList();
        var moved = DefaultModePolicy.ApplyAndReport(ordered, preferred);
        if (moved.Count == 0) return;
        foreach (var row in ordered)
        {
            if (row.Id == skipUpsert || !moved.Contains(row.Id)) continue;
            row.ModifiedDate = DateTime.UtcNow;
            await _modes.UpsertAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureUniqueName(Mode mode, IReadOnlyList<Mode> existing, HwLocalApiModeOperation operation)
    {
        // ONLY WHEN THE NAME IS ACTUALLY CHANGING (issue #356, review round 1).
        // `existing` is a separate `ListAsync` materialisation, so its copy of
        // this record still carries the STORED name; `mode` has already been
        // patched. macOS has always had this guard (`newName != mode.name`) and
        // Windows has it again — this head never did, and #356 widened the
        // comparison key, which enlarges the set of already-stored pairs that
        // collide. Duplicate names are producible: nothing outside these two
        // endpoints checks, and backup import does not. Without the guard a mode
        // that shares a name with another is patchable only by a body that never
        // mentions `name` — and `ApplyModeDocument` leaves the stored name in
        // place for exactly those bodies, so it would fail on all of them.
        //
        // A name whose comparison key is unchanged cannot introduce a NEW
        // collision: the multiset of keys in storage is the same after the write
        // as before it.
        var storedName = existing.FirstOrDefault(item => item.Id == mode.Id)?.Name;
        if (storedName is not null && HyperwhisperCoreMethods.LocalApiModeNameConflict(mode.Name, [storedName]))
            return;
        var others = existing.Where(item => item.Id != mode.Id).Select(item => item.Name).ToList();
        if (HyperwhisperCoreMethods.LocalApiModeNameConflict(mode.Name, others))
            throw LocalApiFailureException.From(
                HyperwhisperCoreMethods.LocalApiModeNameTakenFailure(mode.Name, operation));
    }


    private static void NormalizeMode(Mode mode)
    {
        mode.LocalEngine = string.IsNullOrWhiteSpace(mode.LocalEngine) ? "whisper" : mode.LocalEngine.Trim().ToLowerInvariant();
        if (string.Equals(mode.Model, "cloud", StringComparison.OrdinalIgnoreCase)) mode.ProviderType = "cloud";
        mode.ProviderType = string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
        if (mode.ProviderType == "cloud") mode.Model = "cloud";
        else if (mode.LocalEngine == "parakeet") mode.Model = mode.LocalParakeetModel ?? mode.Model ?? "parakeet-v3";
        else { mode.Model = string.IsNullOrWhiteSpace(mode.Model) ? "base" : mode.Model; mode.ModelType = mode.Model; }
        mode.CloudAccuracyTier = string.IsNullOrWhiteSpace(mode.CloudAccuracyTier) ? "elevenLabsScribeV2" : mode.CloudAccuracyTier;
        mode.CloudPostProcessingModel = string.IsNullOrWhiteSpace(mode.CloudPostProcessingModel) ? "anthropic:claude-haiku-4-5" : mode.CloudPostProcessingModel;
    }

    /// <summary>
    /// The wire-shape half comes from <c>hw-localapi</c>; the capability half
    /// stays here (issue #356 items 2 and 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>validate_mode</c> owns the required-key set, every length bound and
    /// both numeric ranges, so this head, macOS and Windows now refuse the same
    /// bodies with the same messages. Two of those are new here and are a
    /// client-visible tightening: a create body must carry the seven keys
    /// <c>openapi.yaml</c> marks <c>required</c> (this head required none), and
    /// <c>sortOrder</c> is bounded to the <c>Int16</c> range (this head had no
    /// bound and crashed outside <c>Int32</c>).
    /// </para>
    /// <para>
    /// Lengths are now counted in Unicode scalar values rather than UTF-16 code
    /// units, which is the only count all three heads can compute identically.
    /// A 60-emoji mode name was 120 units here and is 60 scalars now.
    /// </para>
    /// <para>
    /// NOT shared, deliberately: the cross-field "an enabled
    /// <c>postProcessingMode</c> requires a provider" rule and the catalog
    /// membership checks below. Windows's version of the first reaches into
    /// <c>CustomEndpointManager</c>, <c>LanguageModelInfo</c> and
    /// <c>PlatformHelper</c>, and macOS has none — that is platform capability,
    /// which is exactly what the crate keeps out.
    /// </para>
    /// <para>
    /// <c>sortOrder</c> is the one bound that is validated from the REQUEST and
    /// not from the merged entity, and the asymmetry is deliberate. Every other
    /// bound here — <c>name</c>, <c>language</c>, <c>preset</c>,
    /// <c>postProcessingMode</c>, the prompts, the vocabulary — was already
    /// applied to the merged entity before issue #356, so a stored value that
    /// fails one has always failed. The <c>Int16</c> range is NEW, and this head
    /// (plus backup import) could store an out-of-range <c>sortOrder</c> before
    /// it existed: applying it to the merged entity would make an unrelated
    /// <c>PATCH {"isDefault":true}</c> fail forever, naming a field the client
    /// never sent. macOS (<c>ModesEndpoint.swift</c>) and Windows
    /// (<c>ModesEndpoints.cs</c>) both bound only the patch's own value, so
    /// reading the stored one here would re-open the divergence this issue
    /// closes.
    /// </para>
    /// </remarks>
    private void ValidateMode(Mode mode, ModeDocumentFacts facts, HwLocalApiModeOperation operation)
    {
        mode.Name = mode.Name.Trim();
        var failure = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
            operation,
            facts.PresentKeys,
            mode.Name,
            mode.Language,
            mode.Preset,
            facts.PostProcessingMode ?? mode.PostProcessingMode,
            facts.SortOrder,
            mode.UserSystemPrompt,
            mode.GeminiCustomPrompt,
            // STORED terms, so `StringArray`'s guard is not the whole answer:
            // backup import and the GUI write this column too, and a null term
            // already in the database would throw inside
            // `FfiConverterSequenceString.AllocationSize` on an unrelated PATCH.
            // Dropping it is right rather than refusing: a null is not a term,
            // it is not something the caller sent, and refusing would be the
            // same fault as bounding a stored `sortOrder` (see above).
            mode.CustomVocabulary?.Where(term => term is not null).ToList()));
        if (failure is not null) throw LocalApiFailureException.From(failure);
        if (mode.PostProcessingMode != 0 && string.IsNullOrWhiteSpace(mode.PostProcessingProvider)) throw new ArgumentException("An enabled post-processing mode requires a provider.");
        if (mode.ProviderType == "cloud")
        {
            if (string.IsNullOrWhiteSpace(mode.CloudProvider) || !CloudProviders.Contains(mode.CloudProvider))
                throw new ArgumentException("Cloud provider is invalid.");
        }
        else
        {
            if (mode.LocalEngine is not ("whisper" or "parakeet"))
                throw new ArgumentException("Local transcription engine is invalid.");
            var model = mode.LocalEngine == "parakeet" ? mode.LocalParakeetModel ?? mode.Model : mode.ModelType ?? mode.Model;
            var known = mode.LocalEngine == "parakeet" ? ParakeetModels : WhisperModels;
            if (string.IsNullOrWhiteSpace(model) || !known.Contains(model))
                throw new ArgumentException("Local transcription model is invalid.");
            var advertisedVoiceModels = _catalog.Models.Where(item => string.Equals(item.Kind, "voice", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (advertisedVoiceModels.Length != 0 && !advertisedVoiceModels.Any(item => string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Local transcription model is not present in the capability catalog.");
        }
    }

    /// <summary>
    /// What the walk over a mode body observed and could not put on the entity:
    /// the top-level key names, and the two numeric fields as written (issue
    /// #356).
    /// </summary>
    /// <remarks>
    /// The numbers are <c>long</c>, not <c>int</c>, so an out-of-range value
    /// survives the crossing into <c>hw-localapi</c> instead of being
    /// pre-truncated or throwing during the parse. That is what turns
    /// <c>{"sortOrder": 99999999999}</c> — an unhandled
    /// <see cref="FormatException"/> and a bare HTTP 500 before this change —
    /// into an ordinary <c>INVALID_REQUEST</c> naming the bound.
    /// </remarks>
    /// <param name="InferredAccuracyTier">
    /// The tier the document's <c>cloudProvider</c> fold produced, or null when
    /// the key was absent or was not a legacy alias (issue #575). Held here
    /// rather than assigned during the walk because its precedence against a
    /// <c>cloudAccuracyTier</c> in the same document depends on the operation —
    /// see <see cref="ApplyInferredAccuracyTier"/>.
    /// </param>
    private readonly record struct ModeDocumentFacts(
        List<string> PresentKeys,
        long? SortOrder,
        long? PostProcessingMode,
        string? InferredAccuracyTier);

    private static ModeDocumentFacts ApplyModeDocument(Mode mode, JsonElement document, bool allowIdentity)
    {
        if (document.ValueKind != JsonValueKind.Object) throw new ArgumentException("Mode body must be a JSON object.");
        var presentKeys = new List<string>();
        long? sortOrder = null;
        long? postProcessingMode = null;
        string? inferredAccuracyTier = null;
        foreach (var property in document.EnumerateObject())
        {
            presentKeys.Add(property.Name);
            switch (property.Name)
            {
                case "id" when allowIdentity: mode.Id = GuidValue(property); break;
                case "id" or "createdDate" or "modifiedDate" or "isSystemProvided": break;
                case "name": mode.Name = RequiredString(property); break;
                case "preset": mode.Preset = RequiredString(property); break;
                case "language": mode.Language = RequiredString(property); break;
                case "model": mode.Model = OptionalString(property); mode.ModelType = mode.Model; break;
                case "localEngine": mode.LocalEngine = RequiredString(property); break;
                case "localParakeetModel": mode.LocalParakeetModel = OptionalString(property); break;
                // FOLDED, LIKE THE `engine` FIELD (issue #575). Windows
                // (`ModesEndpoints.cs:114`, `:485`) and macOS
                // (`ModesEndpoint.swift:129`, `:516`) both run a caller-supplied
                // `cloudProvider` through `normalizeCloudProvider` on create and
                // on patch; this head stored the raw string. So
                // `POST /modes {"cloudProvider": "googlespeech"}` saved a mode
                // that dictates on a retired standalone tier here and on
                // HyperWhisper Cloud there, from the same request body — and on
                // this head that mode could not transcribe at all, for the
                // base-URL reason `ApplyTranscriptionOverrides` records.
                // The inferred tier is held rather than assigned, because its
                // precedence against a `cloudAccuracyTier` in the SAME document
                // depends on the operation — see `ApplyInferredAccuracyTier`.
                //
                // The value is handed over RAW, not trimmed, because that is
                // what both native heads hand their own normalizer
                // (`NormalizeCloudProvider(dto.CloudProvider)`,
                // `normalizeCloudProvider(dto.cloudProvider)`) — and this is a
                // parity change, so it must not invent a third input rule. One
                // consequence is worth knowing: the core TRIMS its legacy-alias
                // needle but only lowercases the pass-through, so a padded
                // `"  googlespeech  "` folds and is then accepted, while a
                // padded `"  deepgram  "` still fails `ValidateMode`'s untrimmed
                // membership test below. That asymmetry lives in the shared
                // function and is now identical on all three heads; widening or
                // narrowing it is a change to `normalize_cloud_provider`, not to
                // this call site. `engine` is a different field with a different
                // published rule — `openapi.yaml` says it is trimmed — and keeps
                // its own `Trim()` above.
                case "cloudProvider":
                    var suppliedProvider = OptionalString(property);
                    var foldedProvider = HyperwhisperCoreMethods.CloudSttNormalizeCloudProvider(suppliedProvider);
                    mode.CloudProvider = suppliedProvider is null ? null : foldedProvider.@provider;
                    inferredAccuracyTier = foldedProvider.@accuracyTier;
                    break;
                case "cloudTranscriptionModel": mode.CloudTranscriptionModel = OptionalString(property); break;
                case "cloudTranscriptionDomain": mode.CloudTranscriptionDomain = OptionalString(property); break;
                case "providerType": mode.ProviderType = OptionalString(property); break;
                case "cloudAccuracyTier": mode.CloudAccuracyTier = RequiredString(property); break;
                case "geminiCustomPrompt": mode.GeminiCustomPrompt = OptionalString(property); break;
                case "punctuation": mode.Punctuation = BooleanValue(property); break;
                case "capitalization": mode.Capitalization = BooleanValue(property); break;
                case "profanityFilter": mode.ProfanityFilter = BooleanValue(property); break;
                case "removeTrailingPeriod": mode.RemoveTrailingPeriod = BooleanValue(property); break;
                case "englishSpelling": mode.EnglishSpelling = OptionalString(property); break;
                // Held as written and handed to the shared bound below. Storing
                // it here would either truncate or throw before `validate_mode`
                // ever sees the number the caller sent.
                case "postProcessingMode":
                    postProcessingMode = IntegerValue(property);
                    if (postProcessingMode is >= int.MinValue and <= int.MaxValue)
                        mode.PostProcessingMode = (int)postProcessingMode.Value;
                    break;
                case "postProcessingProvider": mode.PostProcessingProvider = OptionalString(property); break;
                case "languageModel": mode.LanguageModel = OptionalString(property); break;
                case "localPostProcessingModel": mode.LocalPostProcessingModel = OptionalString(property); break;
                case "userSystemPrompt": mode.UserSystemPrompt = OptionalString(property); break;
                case "customInstructions": mode.CustomInstructions = OptionalString(property); break;
                case "enableScreenOCR": mode.EnableScreenOCR = BooleanValue(property); break;
                case "cloudPostProcessingModel": mode.CloudPostProcessingModel = RequiredString(property); break;
                case "customVocabulary": mode.CustomVocabulary = StringArray(property); break;
                case "isDefault": mode.IsDefault = BooleanValue(property); break;
                case "sortOrder":
                    sortOrder = IntegerValue(property);
                    if (sortOrder is >= int.MinValue and <= int.MaxValue)
                        mode.SortOrder = (int)sortOrder.Value;
                    break;
                case "useStreamingTranscription": break; // Legacy wire-only field; no EF storage exists.
                // AN UNRECOGNISED KEY IS IGNORED, NOT REJECTED (issue #356
                // item 2). `openapi.yaml` documents five keys as "Windows only.
                // macOS ignores this key", so the published contract actively
                // invites a cross-platform client to send keys a given head does
                // not implement — and macOS and Windows both drop an unmapped
                // key inside their JSON decoders. This head was the only one
                // that threw. `mode_key_classification` is the authoritative
                // union, and it is consulted rather than assumed so that a key
                // this switch has not caught up with is distinguishable, in the
                // log, from a client's typo.
                default:
                    LogIgnoredModeKey(property.Name);
                    break;
            }
        }
        return new ModeDocumentFacts(presentKeys, sortOrder, postProcessingMode, inferredAccuracyTier);
    }

    /// <summary>
    /// Write the tier a folded <c>cloudProvider</c> implies, with the precedence
    /// the two native heads use (issue #575).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves are asymmetric on Windows and macOS alike, and this head
    /// mirrors them rather than picking one:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Create</b> — the inferred tier WINS over one in the same body.
    /// Windows: <c>normalized.AccuracyTier ?? dto.CloudAccuracyTier ?? "elevenLabsScribeV2"</c>
    /// (<c>ModesEndpoints.cs:141</c>); macOS: <c>normalized.accuracyTier ?? dto.cloudAccuracyTier</c>
    /// (<c>ModesEndpoint.swift:148</c>).
    /// </description></item>
    /// <item><description>
    /// <b>Patch</b> — an explicit <c>cloudAccuracyTier</c> in the same PATCH
    /// wins, so a client that sends the pair lands as it wrote it. Windows:
    /// <c>patch.CloudAccuracyTier ?? inferredAccuracyTier</c>
    /// (<c>ModesEndpoints.cs:497</c>); macOS: the <c>.omitted</c> arm of the
    /// switch on <c>patch.$cloudAccuracyTier</c> (<c>ModesEndpoint.swift:531</c>).
    /// </description></item>
    /// </list>
    /// <para>
    /// The key SET is what makes the patch half work here. macOS really is
    /// tri-state (<c>patch.$cloudAccuracyTier</c> is a property wrapper whose
    /// <c>.value(nil)</c> case clears the column); WINDOWS IS NOT — its
    /// <c>ModePatchDto.CloudAccuracyTier</c> is a plain <c>string?</c> and
    /// <c>??</c> cannot tell an explicit JSON null from an omitted key. So
    /// <c>PATCH {"cloudProvider": "googlespeech", "cloudAccuracyTier": null}</c>
    /// is the one body where the three heads still disagree: Windows infers the
    /// tier, macOS clears it, and this head refuses the request because
    /// <c>cloudAccuracyTier</c> is parsed with <c>RequiredString</c>. Reading the
    /// PRESENT-KEY set rather than the value is what makes this head agree with
    /// both of them on every body where they agree with each other; the null
    /// case is pre-existing and is not this issue's to reconcile.
    /// </para>
    /// <para>
    /// The tier is written without asking whether the merged mode is a cloud
    /// mode, which is what both native heads do:
    /// <c>{"providerType": "local", "cloudProvider": "googlespeech",
    /// "cloudAccuracyTier": "deepgramNova3"}</c> keeps the local engine and
    /// stores the inferred tier over the caller's. That body is incoherent
    /// either way, and gating on <c>ProviderType</c> here would be a fourth
    /// answer to a question the other two heads already answer the same way.
    /// </para>
    /// <para>
    /// It runs BEFORE <see cref="NormalizeMode"/>, so the tier is already set
    /// when the blank-tier default (<c>elevenLabsScribeV2</c>) is considered and
    /// a folded create cannot be overwritten by it.
    /// </para>
    /// </remarks>
    private static void ApplyInferredAccuracyTier(
        Mode mode, ModeDocumentFacts facts, HwLocalApiModeOperation operation)
    {
        if (string.IsNullOrEmpty(facts.InferredAccuracyTier)) return;
        if (operation == HwLocalApiModeOperation.Patch
            && facts.PresentKeys.Contains("cloudAccuracyTier", StringComparer.Ordinal)) return;
        mode.CloudAccuracyTier = facts.InferredAccuracyTier;
    }

    private static void LogIgnoredModeKey(string key)
    {
        var classification = HyperwhisperCoreMethods.LocalApiModeKeyClassification(key);
        if (classification == HwLocalApiModeKeyClass.Unknown)
            Debug.WriteLine($"Local API: ignoring unrecognised mode field '{key}'.");
        else
            Debug.WriteLine($"Local API: ignoring documented mode field '{key}' ({classification}) this head does not store.");
    }

    // A WRONG-TYPED VALUE IS `INVALID_REQUEST`, NOT A MISSING CAPABILITY
    // (issue #356). `JsonElement.GetBoolean`/`GetString`/`GetGuid` throw
    // `InvalidOperationException` when the value is of another JSON kind, and
    // this head's middleware answers that with HTTP 200 `ENGINE_UNAVAILABLE` —
    // so `{"punctuation":"yes"}` was reported as an absent app capability.
    // `GetInt32` was worse: a number outside `Int32` raises `FormatException`,
    // which NO catch in that middleware handles, so `{"sortOrder":99999999999}`
    // was an unhandled HTTP 500 with no envelope at all. Every accessor here
    // tests the kind first and raises `ArgumentException`, which is the body
    // error the middleware already knows how to answer.
    private static string RequiredString(JsonProperty property) => property.Value.ValueKind == JsonValueKind.String
        ? property.Value.GetString() ?? throw new ArgumentException($"'{property.Name}' must be a string.")
        : throw new ArgumentException($"'{property.Name}' must be a string.");

    private static string? OptionalString(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => property.Value.GetString(),
        _ => throw new ArgumentException($"'{property.Name}' must be a string or null."),
    };

    private static bool BooleanValue(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException($"'{property.Name}' must be true or false."),
    };

    private static Guid GuidValue(JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var value)
            ? value
            : throw new ArgumentException($"'{property.Name}' must be a UUID string.");

    /// <summary>
    /// A JSON integer, as written. Out-of-range is the shared bound's answer,
    /// not a parse error — see <see cref="ModeDocumentFacts"/>.
    /// </summary>
    private static long IntegerValue(JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value)
            ? value
            : throw new ArgumentException($"'{property.Name}' must be a whole number.");

    /// <summary>
    /// A JSON array of strings, in which <c>null</c> is not a string.
    /// </summary>
    /// <remarks>
    /// <c>Deserialize&lt;List&lt;string&gt;&gt;</c> accepted <c>["ok", null]</c>
    /// and produced a list with a null element — System.Text.Json erases
    /// nullable reference types unless <c>RespectNullableAnnotations</c> is set,
    /// which it is nowhere in this repo. That element cannot cross the FFI: the
    /// generated <c>FfiConverterSequenceString.AllocationSize</c> sums
    /// <c>Encoding.UTF8.GetByteCount(item)</c> with no per-element guard, so the
    /// first null throws before Rust sees the call (issue #356, review round 1).
    /// The guard is HERE, at the one place this head parses a string array,
    /// rather than at each call site that hands a list to <c>hw-localapi</c>: a
    /// value that cannot cross the boundary should never be built.
    /// </remarks>
    private static List<string>? StringArray(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Null) return null;
        if (property.Value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"'{property.Name}' must be an array of strings.");
        var items = new List<string>();
        foreach (var element in property.Value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } term)
                throw new ArgumentException($"'{property.Name}' must be an array of strings.");
            items.Add(term);
        }
        return items;
    }

    private static JsonElement ToModeJson(Mode mode) => JsonSerializer.SerializeToElement(new
    {
        id = mode.Id.ToString("D"), mode.Name, mode.Preset, mode.Language, model = mode.ProviderType == "cloud" ? "cloud" : mode.ModelType ?? mode.Model ?? "base",
        mode.Punctuation, mode.Capitalization, mode.ProfanityFilter, mode.CustomInstructions,
        mode.UserSystemPrompt, mode.IsDefault, mode.IsSystemProvided, mode.SortOrder,
        mode.CreatedDate, mode.ModifiedDate, mode.LanguageModel, mode.CloudTranscriptionModel,
        mode.CloudTranscriptionDomain, mode.CloudProvider, mode.PostProcessingMode,
        mode.PostProcessingProvider, mode.EnglishSpelling, useStreamingTranscription = false,
        mode.CloudAccuracyTier, mode.RemoveTrailingPeriod, mode.EnableScreenOCR,
        mode.GeminiCustomPrompt, mode.CloudPostProcessingModel, mode.LocalEngine,
        mode.LocalParakeetModel, mode.LocalPostProcessingModel, mode.CustomVocabulary, mode.ProviderType,
    }, WebJson);

}
