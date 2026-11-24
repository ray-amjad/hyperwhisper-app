using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sentry;

namespace HyperWhisper.Services;

public static partial class SentryService
{
    // =========================================================================
    // REDACTION
    // =========================================================================

    /// <summary>
    /// The <c>beforeSend</c> body: drops denied extras and rewrites the signed-in
    /// user's Windows identifiers out of every field of an error event that can
    /// carry them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fields, and why each one is here:
    /// <list type="bullet">
    /// <item><c>Extra</c> - the only field the filter covered before #932, and it
    /// matched on the KEY, which is why the exception message went out raw.</item>
    /// <item><c>Tags</c> - <c>selected_input_device_name</c> is a tag as well as an
    /// extra, and a Bluetooth endpoint is routinely named after its owner
    /// ("Bob's AirPods").</item>
    /// <item><c>SentryExceptions[].Value</c> - the Sentry issue TITLE, and the whole
    /// of HYPERWHISPER-Y5 / -YF / -Z1.</item>
    /// <item><c>Message</c> - <c>CaptureMessage</c> events. <c>Capture()</c> puts the
    /// caller's text in the <c>error_message</c> EXTRA instead, so Message is null
    /// on the events in this issue.</item>
    /// <item><c>ServerName</c> - the machine name, which <c>SendDefaultPii</c> adds.
    /// It is NOT deleted: a machine name is not an account name and it is the only
    /// way to tell two devices apart inside one issue. But Windows offers the
    /// account name as the default computer name, so "RAY-DESKTOP-PC" is a real
    /// shape, and the account name comes out of it like anywhere else.</item>
    /// <item><c>DebugImages[].CodeFile</c> / <c>.DebugFile</c> - the second door onto
    /// the very path HYPERWHISPER-Y5 / -YF / -Z1 are about. <c>AttachStacktrace</c>
    /// is on by design, so the SDK's own <c>DebugStackTrace</c> adds one debug image
    /// per managed module with <c>CodeFile = module.FullyQualifiedName</c>, and the
    /// installer is per-user
    /// (<c>setup-x64.iss</c>: <c>%LOCALAPPDATA%\Programs\HyperWhisper</c>). Those
    /// images are merged onto the event BEFORE <c>beforeSend</c> runs, so a clean
    /// title shipped in the same envelope as
    /// <c>"code_file":"C:\Users\bob\AppData\Local\Programs\..."</c>. They are
    /// REDACTED rather than dropped: the module file name is what makes a stack
    /// frame symbolicate, and #932 explicitly keeps the assembly simple name.</item>
    /// <item><c>Fingerprint</c> - <see cref="Capture"/>'s default fingerprint is
    /// <c>["{{ default }}", message, errorType]</c> and the caller's message can hold
    /// a path, so a redacted <c>value</c> rode beside a raw <c>fingerprint</c>. The
    /// literal <c>{{ default }}</c> directive is left alone - it is Sentry's grouping
    /// instruction, not data - and everything else goes through the redactor. That
    /// MERGES what used to be one Sentry group per account name into one group per
    /// fault, which is the grouping the fingerprint was always meant to express; it
    /// cannot split a group, because the redactor is a function of the text alone
    /// and two events that fingerprinted alike still do.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The entries are mutated IN PLACE. <c>SentryExceptions</c> hands back the
    /// event's own <see cref="SentryException"/> objects, so writing the collection
    /// back would be a no-op on an event that has one - and NOT a no-op on an event
    /// that has none, where it replaces a null backing field with an empty one and
    /// adds an <c>"exception":{"values":[]}</c> key the event did not have. Same for
    /// <c>Message</c>, whose write-back was a self-assignment. The smoke suite pushes
    /// a real exception and a real message event through the SDK and asserts both.
    /// </para>
    /// <para>
    /// Every step is total: <see cref="Redact"/> never throws for any input, and
    /// nothing here indexes or parses. The outer <c>try</c> is a backstop for the
    /// step after this one, and it returns <c>null</c> - it DROPS the event - rather
    /// than returning what it was handed. That is not the conservative choice it
    /// looks like. In sentry-dotnet 4.12.1 the <c>beforeSend</c> invocation
    /// (<c>SentryClient.BeforeSendInternal</c>) already sits inside a <c>try</c>: a
    /// throw aborts the assignment that would have taken the sanitized result, the
    /// <c>catch</c> logs, and the method falls through to <c>return @event</c> - the
    /// ORIGINAL, un-sanitized event. So letting a throw escape publishes
    /// <c>C:\Users\bob\...</c>, and with <c>MaxBreadcrumbs = 0</c> even the SDK's own
    /// "BeforeSend callback failed" breadcrumb is dropped, so it would be silent.
    /// Losing one event is the cheaper failure. The identifiers are read ONCE per
    /// event rather than once per field.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static SentryEvent? SanitizeEvent(SentryEvent sentryEvent)
        => SanitizeEventGuarded(sentryEvent, static () => BuildLiveRedactionRules());

    /// <summary>
    /// The <see cref="SanitizeEvent(SentryEvent)"/> seam: the same sanitization with
    /// the three identifiers supplied instead of read from the live environment.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static SentryEvent? SanitizeEvent(
        SentryEvent sentryEvent,
        string? userProfileDirectory,
        string? localAppDataDirectory,
        string? userName)
        => SanitizeEventGuarded(
            sentryEvent,
            () => BuildRedactionRules(userProfileDirectory, localAppDataDirectory, userName));

    /// <summary>
    /// <see cref="SanitizeEvent(SentryEvent, IReadOnlyList{RedactionRule})"/>, with a
    /// fault turned into a dropped event instead of an un-sanitized one.
    /// </summary>
    private static SentryEvent? SanitizeEventGuarded(
        SentryEvent sentryEvent,
        Func<IReadOnlyList<RedactionRule>> buildRules)
    {
        try
        {
            return SanitizeEvent(sentryEvent, buildRules());
        }
        catch (Exception ex)
        {
            // The exception's own text can hold the identifier this method exists to
            // remove, and this log line stays on the user's machine - but the type
            // name is all a maintainer needs to find the fault, so only that is
            // written. The log call itself is guarded: nothing in this catch may
            // escape, or the SDK ships the raw event.
            try
            {
                LoggingService.Debug(
                    $"SentryService: beforeSend sanitization failed ({ex.GetType().Name}); dropping the event");
            }
            catch
            {
                // Deliberately empty - see above.
            }

            return null;
        }
    }

    private static SentryEvent SanitizeEvent(SentryEvent sentryEvent, IReadOnlyList<RedactionRule> rules)
    {
        // Redact every extra whose key is not on the allow-list (a Bool or a number
        // survives under any key), and rewrite the user's Windows identifiers out of
        // every allowed String. The redacted branch is never re-examined, which is
        // what makes the "not already [redacted]" rule structural rather than a
        // second check.
        if (sentryEvent.Extra != null)
        {
            var sanitizedExtras = new Dictionary<string, object?>();
            foreach (var kvp in sentryEvent.Extra)
            {
                sanitizedExtras[kvp.Key] = SanitizeExtraValue(kvp.Key, kvp.Value, rules);
            }

            // Buffered first: SetExtra writes into the dictionary being enumerated.
            foreach (var kvp in sanitizedExtras)
            {
                sentryEvent.SetExtra(kvp.Key, kvp.Value);
            }
        }

        var sanitizedTags = new Dictionary<string, string>();
        foreach (var kvp in sentryEvent.Tags)
        {
            sanitizedTags[kvp.Key] = Redact(kvp.Value, rules);
        }

        foreach (var kvp in sanitizedTags)
        {
            sentryEvent.SetTag(kvp.Key, kvp.Value);
        }

        foreach (var sentryException in sentryEvent.SentryExceptions)
        {
            if (sentryException.Value != null)
            {
                sentryException.Value = Redact(sentryException.Value, rules);
            }
        }

        var message = sentryEvent.Message;
        if (message != null)
        {
            // Each property is rewritten only when it is non-null, so a null
            // Formatted stays null rather than becoming "".
            if (message.Formatted != null)
            {
                message.Formatted = Redact(message.Formatted, rules);
            }

            if (message.Message != null)
            {
                message.Message = Redact(message.Message, rules);
            }
        }

        if (sentryEvent.ServerName != null)
        {
            sentryEvent.ServerName = Redact(sentryEvent.ServerName, rules);
        }

        // Mutated in place, like the exception values above: the list and the images
        // in it belong to the event, and the SDK has already merged its own images
        // onto it. Null is left null - reading the property does not create the list,
        // and writing an empty one would add a "debug_meta" key the event never had.
        if (sentryEvent.DebugImages != null)
        {
            foreach (var debugImage in sentryEvent.DebugImages)
            {
                if (debugImage == null)
                {
                    continue;
                }

                if (debugImage.CodeFile != null)
                {
                    debugImage.CodeFile = Redact(debugImage.CodeFile, rules);
                }

                if (debugImage.DebugFile != null)
                {
                    debugImage.DebugFile = Redact(debugImage.DebugFile, rules);
                }
            }
        }

        // Count is read rather than a null check: Fingerprint is never null in 4.12.1.
        // The guard saves an allocation on the events that have no fingerprint, which
        // is most of them; it is NOT load-bearing for behaviour, because the
        // serializer omits an empty array either way. The DebugImages guard above IS
        // load-bearing - reading that property does not create the list, and writing
        // one would add a "debug_meta" key the SDK never wrote.
        if (sentryEvent.Fingerprint.Count > 0)
        {
            sentryEvent.Fingerprint = RedactFingerprint(sentryEvent.Fingerprint, rules);
        }

        return sentryEvent;
    }

    /// <summary>
    /// One fingerprint, redacted part by part, with Sentry's grouping directive left
    /// alone.
    /// </summary>
    /// <remarks>
    /// <c>{{ default }}</c> tells Sentry to fold its own grouping in beside these
    /// parts. It is an instruction, not data, and an account named <c>default</c>
    /// would otherwise turn it into <c>{{ %USER% }}</c> - a string Sentry does not
    /// recognise - and silently regroup every issue for that one user.
    /// </remarks>
    private static string[] RedactFingerprint(
        IReadOnlyList<string> fingerprint,
        IReadOnlyList<RedactionRule> rules)
    {
        var redacted = new string[fingerprint.Count];
        for (var i = 0; i < fingerprint.Count; i++)
        {
            var part = fingerprint[i];
            redacted[i] = part == null || part == SentryDefaultFingerprintDirective
                ? part!
                : Redact(part, rules);
        }

        return redacted;
    }

    /// <summary>
    /// Sentry's own grouping directive, which must survive redaction verbatim.
    /// </summary>
    private const string SentryDefaultFingerprintDirective = "{{ default }}";

    /// <summary>
    /// EVERY extra key whose String value may leave this machine. Matched EXACTLY,
    /// case and all (<see cref="StringComparer.Ordinal"/>). A key that is not here is
    /// redacted: its String, array, null or any other non-Bool, non-number value
    /// becomes <c>"[redacted]"</c>. See <see cref="SanitizeExtraValue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced a substring deny-list (<c>transcript</c> / <c>text</c> /
    /// <c>prompt</c> / <c>path</c>), the same move macOS made in #1887 (#805). That
    /// list over-matched - <c>transcript_id</c>,
    /// <c>transcription_provider_display_name</c> and
    /// <c>backend_empty_transcript_without_flag</c> arrived as <c>"[redacted]"</c> on
    /// every event of HYPERWHISPER-PA/-RM/-XR - and under-matched: a content key whose
    /// name holds none of the four words went out verbatim (#795). With an allow-list
    /// a key nobody reviewed stays hidden, so a new diagnostic fails safe.
    /// </para>
    /// <para>
    /// <b>To add a key</b>, decide from the VALUE it holds, never from its spelling.
    /// It may go here only if no value it can ever hold is user content: nothing
    /// spoken or typed, no prompt or transcript, no path, no user-chosen file name,
    /// no Mode name or vocabulary, no device name a person can rename, no device id,
    /// and no raw exception message or process output (those embed paths and server
    /// text). A key whose value is only ever a Bool or a number does not need to be
    /// here - but a key whose number falls back to a String such as
    /// <c>"unknown"</c> does, or the fallback arrives as <c>"[redacted]"</c>.
    /// </para>
    /// <para>
    /// Sent today but deliberately NOT on the list, so their String values arrive as
    /// <c>"[redacted]"</c>: <c>selected_input_device_name</c> (the no-speech
    /// diagnostic; <c>WaveInCapabilities.ProductName</c>, which a person can rename
    /// and a Bluetooth headset is named after its owner - it stays a TAG, where the
    /// identifier rewrite still applies) and <c>parakeet_stderr_tail</c> (the
    /// Parakeet daemon's own stderr, raw process output; only the account
    /// identifiers are scrubbed out of it). Ray's ruling 2026-10-07 (#1364): a device
    /// name or raw error text that can carry a person's name is personal data. The
    /// slug, exit code and line-count keys beside them still identify the failure.
    /// </para>
    /// <para>
    /// The <c>diagnostic_message</c> and <c>diagnostic_fingerprint</c> data of a
    /// diagnostic TRANSACTION are written by
    /// <see cref="PrepareDiagnosticTransactionData"/> itself, not from the caller's
    /// extras, so they are not looked up here. Tags are not looked up here either:
    /// they go through the identifier rewrite only, on both paths.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static readonly IReadOnlySet<string> AllowedExtraKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // Capture(). The caller's own description of the failure. Every caller
        // passes a fixed literal or a message built from fixed slugs - never an
        // exception's Message - and the identifier rewrite still runs on it.
        "error_message",

        // MainViewModel.ReportBackgroundModelLoadFailure. Trigger and engine are
        // fixed slugs, the model is the catalog model id, the exception is the
        // TYPE name only.
        "model_load_trigger", "model_load_engine", "model_load_model",
        "model_load_exception_type", "runtime_identifier", "is_arm64",

        // LocalLlmGpuLoadCrashStore.ReportRecoveredGpuLoadCrash. model_file is
        // Path.GetFileName of a catalog GGUF the app downloaded itself (there is no
        // import); gpu_name is the DXGI adapter description, a hardware model string.
        "model_file", "gpu_name", "gpu_vram", "crashed_backend", "fallback_backend",

        // LicenseNetworkService "License validation server error". endpoint is the
        // app's own validation URL, status the HTTP status, license_reason the
        // server's own reason slug.
        "endpoint", "status", "license_reason",

        // SmartPasteDiagnostics. paste_target_process is the target's process name,
        // which names an app, not anything typed into it (macOS sends the bundle id
        // the same way, paste_target_bundle_id).
        "paste_outcome", "paste_component", "paste_target_process", "paste_target_app_kind",
        "paste_had_captured_target", "paste_target_window_alive", "paste_target_window_minimized",
        "paste_focus_ready", "paste_focus_wait_ms", "paste_character_count",
        "paste_hide_from_clipboard_history", "paste_elapsed_ms", "paste_reported_once_per_run",

        // RenderThreadFailureRecovery.
        "hresult", "render_recovery", "capture_stage",

        // ApplicationControlDiagnostics block report. blocked_file_name is the
        // sanitized simple name of the app's own DLL or exe, already a tag.
        "blocked_file_name", "blocked_hresult", "blocked_match", "blocked_exception_type",
        "outer_exception_type",

        // ApplicationControlDiagnostics classifier load failure. Exception TYPE names
        // and HRESULTs only, never a message.
        "classifier_assembly_present", "classifier_file_size_bytes",
        "classifier_winverifytrust_hresult", "classifier_trust_probe_exception_type",
        "classifier_trust_probe_exception_hresult", "classifier_zone_id",
        "classifier_load_succeeded", "classifier_outer_exception_type", "classifier_outer_hresult",
        "classifier_innermost_exception_type", "classifier_innermost_hresult",

        // OptionalAssemblyGuard. Exception TYPE names and HRESULTs only.
        "optional_assembly_stage", "optional_assembly_outer_exception_type",
        "optional_assembly_outer_hresult", "optional_assembly_innermost_exception_type",
        "optional_assembly_innermost_hresult",

        // ParakeetTranscriptionService.BuildStartFailureFields (#854). The model id is
        // the catalog folder name, the language hint a language code.
        // Not parakeet_stderr_tail: raw process output, see above.
        "parakeet_failure", "parakeet_exit_code", "parakeet_engine", "parakeet_model_id",
        "parakeet_language_hint", "parakeet_ready_timeout_s", "parakeet_elapsed_ms",
        "parakeet_stderr_line_count",

        // TranscriptionDiagnosticsService.BuildDiagnosticExtras (no-speech /
        // empty-recording transaction). diagnostic_record_id is a GUID the app
        // minted, not the transcript; audio_file_extension is the extension alone;
        // audio_analysis_error is "<exception type> (0x<HRESULT>)" or a fixed
        // sentence; mode_preset is the preset slug, never the user-typed Mode name;
        // provider_display_name and exception_provider are the provider's catalog
        // display name. Several of the numbers fall back to the String "unknown".
        // Not selected_input_device_name: a person can rename the microphone.
        "diagnostic_record_id", "audio_file_exists", "audio_file_extension",
        "audio_file_size_bytes", "audio_duration_seconds", "audio_sample_rate_hz",
        "audio_channels", "audio_peak_dbfs", "audio_rms_dbfs", "audio_non_silent_ratio",
        "audio_decoded_sample_count", "audio_measured_sample_count", "mode_preset",
        "provider_display_name", "capture_device_list_position", "provider_attempt_ms",
        "raw_result_length", "backend_request_id", "backend_stt_provider",
        "backend_http_status", "backend_response_latency_ms", "backend_empty_without_flag",
        "audio_analysis_error", "exception_type", "exception_code", "exception_provider",
        "exception_http_status",
    };

    /// <summary>
    /// Whether the Sentry privacy filter redacts this extra's content: true for
    /// every key that is not EXACTLY on <see cref="AllowedExtraKeys"/>.
    /// </summary>
    /// <remarks>
    /// A redacted key still keeps a Bool or a number value - see
    /// <see cref="SanitizeExtraValue"/>, which is the only place this is applied.
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static bool IsRedactedExtraKey(string key) => !AllowedExtraKeys.Contains(key);

    /// <summary>
    /// The value one extra leaves the machine with. The single rule both the
    /// <c>beforeSend</c> path (<see cref="SanitizeEvent(SentryEvent)"/>) and the
    /// transaction path (<see cref="PrepareDiagnosticTransactionData"/>) apply, so
    /// the two cannot drift.
    /// </summary>
    /// <remarks>
    /// An allowed key keeps its value, with the user's Windows identifiers rewritten
    /// out of a String. A key that is not allowed keeps a Bool or a number - a
    /// measurement cannot hold speech or a name - and anything else (a String, an
    /// array, null, any object) becomes <c>"[redacted]"</c>. The same rule as macOS
    /// <c>SentryService.redactedValue(forKey:value:)</c>; issue #684 is the Bool that
    /// once shipped as <c>"[redacted]"</c>.
    /// </remarks>
    private static object? SanitizeExtraValue(string key, object? value, IReadOnlyList<RedactionRule> rules)
    {
        if (!IsRedactedExtraKey(key))
        {
            return value is string text ? Redact(text, rules) : value;
        }

        return value is bool
            or sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal
            ? value
            : "[redacted]";
    }

    /// <summary>
    /// Replaces the signed-in user's Windows identifiers - profile directory,
    /// local-app-data directory and bare account name - with fixed tokens, and
    /// leaves the rest of the text alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HYPERWHISPER-Y5/-YF/-Z1 put three real Windows account names in the Sentry
    /// issue TITLE. A <c>FileLoadException</c> message embeds the full install path,
    /// that path holds the account name, and the privacy filter in
    /// <c>SetBeforeSend</c> only ever rewrote <c>Extra</c> - matching on the KEY, so
    /// the exception's own message was never examined. The rule was already written
    /// down at <see cref="OptionalAssemblyGuard"/>: never the exception message and
    /// never <c>FileLoadException.FileName</c>, because both carry the installed
    /// path and that path holds the user's account name. This is what keeps it on
    /// the unhandled path, which does not go through that guard.
    /// </para>
    /// <para>
    /// The identifiers are read from the process rather than matched by pattern. The
    /// sentence around the path is localized by Windows - the German form of "an
    /// Application Control policy has blocked this file" is what HYPERWHISPER-Y5
    /// actually carries - but the PATH is not localized, so matching the literal
    /// directory is locale-proof. A <c>C:\Users\[^\\]+</c> regex is not: it misses a
    /// redirected profile and it misses a non-<c>C:</c> drive.
    /// </para>
    /// <para>
    /// This is ONE left-to-right scan over the input, not three passes of
    /// <c>string.Replace</c>. Three passes are wrong in two ways that a fourth pass
    /// cannot fix. Pass N+1 reads what pass N wrote, so an account named <c>User</c>
    /// - Microsoft's own default on a Windows dev VM image - turned the
    /// <c>%USERPROFILE%</c> token that had just removed it back into
    /// <c>%%USER%PROFILE%</c>, re-encoding the identifier the pass before had
    /// removed; <c>App</c> and <c>Data</c> did the same to <c>\AppData\</c>. And a
    /// substring match for the bare name has no idea where a word ends, so account
    /// <c>ed</c> shredded <c>HyperWhisper.Shar%USER%Core.dll</c> and account
    /// <c>c</c> shredded <c>0x800711%USER%7</c> - the two things this method exists
    /// to keep. The scan fixes both by construction: it only ever moves FORWARD over
    /// the input, so no rule can match a token this method itself emitted, and the
    /// bare name is matched only between boundaries (below).
    /// </para>
    /// <para>
    /// At a given position the LONGEST identifier wins, which is why
    /// <c>BuildRedactionRules</c> sorts. Local-app-data normally sits inside the
    /// profile, so a path under it reads <c>%LOCALAPPDATA%\...</c> rather than
    /// <c>%USERPROFILE%\AppData\Local\...</c> - the more specific answer, and the one
    /// that makes the two rules tell each other apart in a test. Neither rule is dead
    /// code: the profile rule is the only one that catches
    /// <c>C:\Users\bob\Documents\...</c>, and the local-app-data rule is the only one
    /// that catches a local-app-data directory redirected to another drive. That
    /// directory is read from the environment rather than from the special-folder
    /// API, because <c>AppPaths</c> owns that special folder (it applies the
    /// isolated-profile override).
    /// </para>
    /// <para>
    /// This deliberately does NOT do what <c>TelemetryPrivacy.SanitizeException</c>
    /// does on Linux. That throws away the message, the inner exception and the
    /// HRESULT. Here <c>0x800711C7</c> and the assembly simple name are the entire
    /// diagnosis, so only the identifiers go. The Linux blanket form is the fallback
    /// if maximum safety is ever wanted over diagnosis.
    /// </para>
    /// <para>
    /// Over-redaction is still possible and still accepted: an account named
    /// <c>System</c> turns a standalone occurrence of that word into <c>%USER%</c>.
    /// That is the safe direction for a privacy filter and it is how
    /// <see cref="IsRedactedExtraKey"/> already errs. The method is total - it never
    /// throws, for any input - because it runs inside <c>beforeSend</c>, and a throw
    /// there does NOT cost the event: sentry-dotnet 4.12.1 catches it and sends the
    /// event it was handed, un-sanitized. See
    /// <see cref="SanitizeEventGuarded"/>, which turns that into a dropped event.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string RedactUserIdentifiers(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Redact(value, BuildLiveRedactionRules());
    }

    /// <summary>
    /// The <see cref="RedactUserIdentifiers(string?)"/> seam: the same redaction with
    /// the three identifiers supplied instead of read from the live environment.
    /// </summary>
    /// <remarks>
    /// The smoke tests need this. The live entry point reads the account of whoever
    /// is running it, so a case built on a fixed <c>C:\Users\testaccount\...</c>
    /// string would pass only on a machine owned by a user named <c>testaccount</c>
    /// and fail on the CI runner, where the profile is <c>C:\Users\runneradmin</c>.
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string RedactUserIdentifiers(
        string? value,
        string? userProfileDirectory,
        string? localAppDataDirectory,
        string? userName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Redact(
            value,
            BuildRedactionRules(userProfileDirectory, localAppDataDirectory, userName));
    }

    /// <summary>
    /// What has to sit around a run of text for it to count as this identifier.
    /// </summary>
    private enum MatchBoundary
    {
        /// <summary>
        /// A directory: the match has to END a path segment, so the character after
        /// it must be a separator or the end of the string. Without that,
        /// <c>C:\Users\bob\AppData\Local</c> matched inside
        /// <c>...\AppData\LocalLow\NVIDIA\x.log</c> (a standard Windows sibling) and
        /// produced <c>%LOCALAPPDATA%Low\NVIDIA\x.log</c>, and profile
        /// <c>C:\Users\bob</c> rewrote a SECOND account's <c>C:\Users\bobby\...</c>
        /// to <c>%USERPROFILE%by\...</c> - a fragment of someone else's name,
        /// attributed to the reporter. Neither is "over-redaction"; both mangle the
        /// diagnosis, and the second one leaks.
        /// </summary>
        DirectorySegment,

        /// <summary>
        /// A bare account name: neither neighbour may be a letter or a digit.
        /// </summary>
        WholeWord
    }

    /// <summary>
    /// One identifier, the token that replaces it, and what has to bound a match.
    /// </summary>
    private readonly record struct RedactionRule(string Identifier, string Token, MatchBoundary Boundary);

    /// <summary>
    /// The three identifiers of the signed-in user, read from the process.
    /// </summary>
    /// <remarks>
    /// Read ONCE per event rather than once per field: <c>SanitizeEvent</c> touches
    /// about eleven strings on a typical diagnostic event, and <c>Environment.UserName</c>
    /// P/Invokes <c>GetUserNameExW</c> with no BCL cache. Not cached ACROSS events -
    /// a static cache would need a memory barrier for a value that costs microseconds.
    /// </remarks>
    private static IReadOnlyList<RedactionRule> BuildLiveRedactionRules()
    {
        return BuildRedactionRules(
            ReadIdentifier(static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ReadIdentifier(static () => Environment.GetEnvironmentVariable("LOCALAPPDATA")),
            ReadIdentifier(static () => Environment.UserName));

        // An environment that refuses to be read must not cost the event, and must
        // not take the other two identifiers down with it either.
        static string? ReadIdentifier(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Turns the three identifiers into the rules <see cref="Redact"/> scans with,
    /// longest identifier first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every guard that decides whether an identifier is usable lives here, once, and
    /// this is the ONLY producer of rules: a blank identifier is dropped - both the
    /// directories and the account name go through <c>IsNullOrWhiteSpace</c> - a
    /// directory loses a trailing separator, and a bare drive root (<c>C:\</c>, three
    /// characters or fewer once trimmed) is dropped because replacing it would mangle
    /// every path in the message for no privacy gain. Because the blank guards are
    /// here, <see cref="MatchRule"/> can never be handed a zero-length identifier and
    /// carries no guard of its own for one.
    /// </para>
    /// <para>
    /// Both kinds of rule are bounded, in different ways - see
    /// <see cref="MatchBoundary"/>. A directory has to end a path segment; a bare
    /// account name has to be a whole word.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RedactionRule> BuildRedactionRules(
        string? userProfileDirectory,
        string? localAppDataDirectory,
        string? userName)
    {
        var rules = new List<RedactionRule>(3);

        AddDirectory(userProfileDirectory, "%USERPROFILE%");
        AddDirectory(localAppDataDirectory, "%LOCALAPPDATA%");

        if (!string.IsNullOrWhiteSpace(userName))
        {
            rules.Add(new RedactionRule(userName, "%USER%", MatchBoundary.WholeWord));
        }

        // OrderByDescending is stable, so two identifiers of the same length keep
        // the order they were added in.
        return rules.OrderByDescending(rule => rule.Identifier.Length).ToList();

        void AddDirectory(string? directory, string token)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var trimmed = directory.TrimEnd('\\', '/');

            // "C:", "C:\", "D:/" - a drive root, and nothing else is this short.
            if (trimmed.Length <= 3 || string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            rules.Add(new RedactionRule(trimmed, token, MatchBoundary.DirectorySegment));
        }
    }

    /// <summary>
    /// The single forward scan. Emitted tokens are never re-read.
    /// </summary>
    private static string Redact(string value, IReadOnlyList<RedactionRule> rules)
    {
        if (string.IsNullOrEmpty(value) || rules.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var matchLength = MatchRule(value, index, rules, out var token);
            if (matchLength > 0)
            {
                // Appended, never re-examined. The cursor only moves forward over
                // the INPUT, so no rule can match text this method emitted - which
                // is what stops an account named "User" from rewriting the
                // "%USERPROFILE%" that had just removed it.
                builder.Append(token);
                index += matchLength;
                continue;
            }

            builder.Append(value[index]);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The rule that matches at <paramref name="index"/>, or a length of 0.
    /// </summary>
    private static int MatchRule(
        string value,
        int index,
        IReadOnlyList<RedactionRule> rules,
        out string token)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var length = rule.Identifier.Length;

            // No zero-length guard here on purpose: BuildRedactionRules is the only
            // producer of rules and it drops every blank identifier, so one cannot
            // reach this loop - a guard for it would be unreachable code that no test
            // could kill. The scan terminates even if one ever did, because Redact
            // advances the cursor whenever the returned length is not positive.
            if (index + length > value.Length)
            {
                continue;
            }

            if (string.Compare(value, index, rule.Identifier, 0, length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }

            if (!IsBoundedMatch(value, index, length, rule.Boundary))
            {
                continue;
            }

            token = rule.Token;
            return length;
        }

        token = string.Empty;
        return 0;
    }

    /// <summary>
    /// Whether the run at <paramref name="index"/> is bounded the way its rule needs.
    /// </summary>
    private static bool IsBoundedMatch(string value, int index, int length, MatchBoundary boundary)
        => boundary == MatchBoundary.DirectorySegment
            ? index + length == value.Length || IsDirectorySeparator(value[index + length])
            : (index == 0 || IsAccountNameBoundary(value[index - 1]))
                && (index + length == value.Length || IsAccountNameBoundary(value[index + length]));

    private static bool IsDirectorySeparator(char character)
        => character == '\\' || character == '/';

    /// <summary>
    /// A character a bare account name cannot run across: anything that is not a
    /// letter and not a digit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rule, and it is the third one tried. A bare substring match
    /// shredded the diagnosis (account <c>ed</c> turned
    /// <c>HyperWhisper.SharedCore.dll</c> into <c>HyperWhisper.Shar%USER%Core.dll</c>).
    /// A delimiter-INCLUSION set - only the characters Windows forbids in an account
    /// name, plus whitespace, the apostrophes, <c>-</c> and <c>_</c> - kept the
    /// diagnosis but left the leak wide open, because it excluded <c>.</c>, <c>@</c>
    /// and the parentheses: account <c>bob</c> survived in
    /// <c>'C:\Users\bob\Documents\bob.docx'</c>, in <c>Bob.AirPods</c>, in
    /// <c>bob@corp.com</c> and in <c>Headset (Bob's AirPods)</c> - and the first of
    /// those is the exact shape of the Sentry issue TITLE that #932 exists to close.
    /// </para>
    /// <para>
    /// Excluding only letters and digits keeps BOTH properties, because the strings
    /// #932 must preserve are dotted tokens whose parts sit between letters and
    /// digits, not at their edges. Traced, for the message in the issue:
    /// <list type="bullet">
    /// <item>account <c>ed</c> in <c>SharedCore</c> - neighbours <c>r</c> and
    /// <c>C</c>, both alphanumeric, so no match; the assembly name survives.</item>
    /// <item>account <c>c</c> in <c>0x800711C7</c> - neighbours <c>1</c> and
    /// <c>7</c>, so no match; the HRESULT survives.</item>
    /// <item>account <c>bob</c> in <c>\bob.docx</c> - neighbours <c>\</c> and
    /// <c>.</c>, so it MATCHES; the leak closes.</item>
    /// <item>account <c>Bob</c> in <c>(Bob's AirPods)</c> - neighbours <c>(</c> and
    /// <c>'</c>, so it MATCHES; the leak closes.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The residual cost is exact, and it is over-redaction rather than a leak: an
    /// account named <c>dll</c>, <c>SharedCore</c> or <c>0x800711C7</c> - all legal
    /// Windows account names - loses that token out of its own crash report, because
    /// <c>.dll'</c> and <c>(0x800711C7)</c> ARE whole words by this rule. That is the
    /// accepted direction for this filter, the same direction
    /// <see cref="IsRedactedExtraKey"/> already errs in, and a lost file extension is
    /// cheaper than a published account name.
    /// </para>
    /// <para>
    /// <see cref="char.IsLetterOrDigit(char)"/> rather than an ASCII range, so a CJK
    /// or Cyrillic account name is bounded by the same rule as a Latin one.
    /// </para>
    /// </remarks>
    private static bool IsAccountNameBoundary(char character)
        => !char.IsLetterOrDigit(character);
}
