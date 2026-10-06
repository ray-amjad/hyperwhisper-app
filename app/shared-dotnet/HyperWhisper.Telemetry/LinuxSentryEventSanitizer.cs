using System.Text;
using Sentry;
using Sentry.Protocol;

namespace HyperWhisper.Telemetry;

/// <summary>
/// The Linux head's <c>beforeSend</c>: drops denied extras and rewrites the
/// signed-in user's Linux identifiers (home directory, XDG base directories, bare
/// account name) out of every field of an event that can carry them.
/// </summary>
/// <remarks>
/// <para>
/// A port of what PR #934 did for Windows in <c>app/windows/HyperWhisper/Services/SentryService.cs</c>,
/// with Linux paths. <see cref="LinuxSentryService.Capture"/> already strips the
/// message through <see cref="TelemetryPrivacy.SanitizeException"/>, but the SDK's
/// own integrations (unhandled AppDomain exceptions, unobserved task exceptions)
/// capture the RAW exception and never pass through it, so beforeSend is the only
/// filter every event meets. Only the identifiers go: the exception type, an errno
/// or HRESULT, and the assembly name are the diagnosis.
/// </para>
/// <para>
/// Fields: <c>Extra</c> (key deny-list, then every string value), <c>Tags</c>,
/// <c>SentryExceptions[]</c> (the value, i.e. the issue TITLE, the mechanism's
/// description/help link/source/<c>data</c>, and every stack-frame path and context
/// line), the stack frames of <c>SentryThreads[]</c>, <c>Message.Formatted</c> /
/// <c>.Message</c>, <c>ServerName</c> (a Linux hostname is often <c>bob-laptop</c>),
/// <c>DebugImages[].CodeFile</c> / <c>.DebugFile</c> (a per-user install under
/// <c>~/.local</c> puts the account name in every module path), and
/// <c>Fingerprint</c> (Sentry's <c>{{ default }}</c> directive is left verbatim).
/// Everything is mutated in place; a null field stays null so no key is added.
/// </para>
/// </remarks>
internal static class LinuxSentryEventSanitizer
{
    /// <summary>
    /// The XDG base directories read from the environment, and the token each one
    /// becomes. They normally sit under <c>$HOME</c> (which the home rule already
    /// covers); they matter when a user points one outside it, e.g.
    /// <c>XDG_DATA_HOME=/data/bob/share</c>. <c>XDG_RUNTIME_DIR</c> is left out on
    /// purpose: it is <c>/run/user/&lt;uid&gt;</c>, a number, not a name.
    /// </summary>
    internal static readonly IReadOnlyList<string> XdgDirectoryVariables =
    [
        "XDG_DATA_HOME",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME",
    ];

    private const string HomeToken = "$HOME";
    private const string UserToken = "$USER";
    private const string SentryDefaultFingerprintDirective = "{{ default }}";

    /// <summary>The live beforeSend: identifiers read from this process, once per event.</summary>
    internal static SentryEvent? SanitizeEvent(SentryEvent sentryEvent)
        => SanitizeEventGuarded(sentryEvent, static () => BuildLiveRedactionRules());

    /// <summary>The <see cref="SanitizeEvent(SentryEvent)"/> seam, with the identifiers supplied.</summary>
    internal static SentryEvent? SanitizeEvent(
        SentryEvent sentryEvent,
        string? homeDirectory,
        IReadOnlyDictionary<string, string?>? xdgDirectories,
        string? userName)
        => SanitizeEventGuarded(
            sentryEvent,
            () => BuildRedactionRules(homeDirectory, xdgDirectories, userName));

    /// <summary>
    /// A fault DROPS the event. sentry-dotnet 4.12.1 catches a throw from beforeSend
    /// and sends the ORIGINAL event, so letting one escape would publish the raw path.
    /// </summary>
    private static SentryEvent? SanitizeEventGuarded(
        SentryEvent sentryEvent,
        Func<IReadOnlyList<RedactionRule>> buildRules)
    {
        try
        {
            return SanitizeEvent(sentryEvent, buildRules());
        }
        catch
        {
            return null;
        }
    }

    private static SentryEvent SanitizeEvent(SentryEvent sentryEvent, IReadOnlyList<RedactionRule> rules)
    {
        if (sentryEvent.Extra is not null)
        {
            var sanitizedExtras = new Dictionary<string, object?>();
            foreach (var extra in sentryEvent.Extra)
            {
                sanitizedExtras[extra.Key] = SentryTelemetryBackend.IsSensitiveExtra(extra.Key)
                    ? "[redacted]"
                    : extra.Value is string extraText
                        ? Redact(extraText, rules)
                        : extra.Value;
            }

            // Buffered first: SetExtra writes into the dictionary being enumerated.
            foreach (var extra in sanitizedExtras)
            {
                sentryEvent.SetExtra(extra.Key, extra.Value);
            }
        }

        var sanitizedTags = new Dictionary<string, string>();
        foreach (var tag in sentryEvent.Tags)
        {
            sanitizedTags[tag.Key] = Redact(tag.Value, rules);
        }

        foreach (var tag in sanitizedTags)
        {
            sentryEvent.SetTag(tag.Key, tag.Value);
        }

        // In place: writing the collection back would add an empty
        // "exception":{"values":[]} to an event that had none.
        foreach (var sentryException in sentryEvent.SentryExceptions ?? [])
        {
            SanitizeException(sentryException, rules);
        }

        // The SDK also attaches thread stack traces; their frames carry the same paths.
        foreach (var thread in sentryEvent.SentryThreads ?? [])
        {
            SanitizeStackTrace(thread?.Stacktrace, rules);
        }

        var message = sentryEvent.Message;
        if (message is not null)
        {
            if (message.Formatted is not null)
            {
                message.Formatted = Redact(message.Formatted, rules);
            }

            if (message.Message is not null)
            {
                message.Message = Redact(message.Message, rules);
            }
        }

        if (sentryEvent.ServerName is not null)
        {
            sentryEvent.ServerName = Redact(sentryEvent.ServerName, rules);
        }

        // Null stays null: writing an empty list would add a "debug_meta" key.
        if (sentryEvent.DebugImages is not null)
        {
            foreach (var debugImage in sentryEvent.DebugImages)
            {
                if (debugImage is null)
                {
                    continue;
                }

                if (debugImage.CodeFile is not null)
                {
                    debugImage.CodeFile = Redact(debugImage.CodeFile, rules);
                }

                if (debugImage.DebugFile is not null)
                {
                    debugImage.DebugFile = Redact(debugImage.DebugFile, rules);
                }
            }
        }

        if (sentryEvent.Fingerprint.Count > 0)
        {
            var fingerprint = sentryEvent.Fingerprint;
            var redacted = new string[fingerprint.Count];
            for (var i = 0; i < fingerprint.Count; i++)
            {
                var part = fingerprint[i];
                redacted[i] = part is null || part == SentryDefaultFingerprintDirective
                    ? part!
                    : Redact(part, rules);
            }

            sentryEvent.Fingerprint = redacted;
        }

        return sentryEvent;
    }

    /// <summary>
    /// Every free-text field of one exception entry: the value (the issue title), the
    /// mechanism's description, help link, source and <c>data</c> (sentry-dotnet 4.12.1
    /// copies <c>Exception.Data</c> into it, so <c>Data["file"]="/home/bob/x.wav"</c>
    /// lands there), and every frame of its stack trace. <c>Type</c>, <c>Module</c>
    /// and the mechanism <c>Type</c> are identifiers Sentry groups on, so they stay.
    /// </summary>
    private static void SanitizeException(SentryException sentryException, IReadOnlyList<RedactionRule> rules)
    {
        if (sentryException.Value is not null)
        {
            sentryException.Value = Redact(sentryException.Value, rules);
        }

        var mechanism = sentryException.Mechanism;
        if (mechanism is not null)
        {
            if (mechanism.Description is not null)
            {
                mechanism.Description = Redact(mechanism.Description, rules);
            }

            if (mechanism.HelpLink is not null)
            {
                mechanism.HelpLink = Redact(mechanism.HelpLink, rules);
            }

            if (mechanism.Source is not null)
            {
                mechanism.Source = Redact(mechanism.Source, rules);
            }

            // Getter allocates lazily; read it only when it already holds something.
            if (mechanism.Data.Count > 0)
            {
                RedactValues(mechanism.Data, rules);
            }
        }

        SanitizeStackTrace(sentryException.Stacktrace, rules);
    }

    /// <summary>
    /// Every path-bearing string of every frame. <c>abs_path</c>/<c>filename</c> come
    /// from the PDB, i.e. the BUILDER's checkout, which is the user's own home when
    /// they build from source. Function and module names stay: they are the diagnosis.
    /// </summary>
    private static void SanitizeStackTrace(SentryStackTrace? stackTrace, IReadOnlyList<RedactionRule> rules)
    {
        if (stackTrace is null)
        {
            return;
        }

        foreach (var frame in stackTrace.Frames)
        {
            if (frame is null)
            {
                continue;
            }

            if (frame.FileName is not null)
            {
                frame.FileName = Redact(frame.FileName, rules);
            }

            if (frame.AbsolutePath is not null)
            {
                frame.AbsolutePath = Redact(frame.AbsolutePath, rules);
            }

            if (frame.Package is not null)
            {
                frame.Package = Redact(frame.Package, rules);
            }

            if (frame.ContextLine is not null)
            {
                frame.ContextLine = Redact(frame.ContextLine, rules);
            }

            RedactLines(frame.PreContext, rules);
            RedactLines(frame.PostContext, rules);

            if (frame.Vars.Count > 0)
            {
                foreach (var variable in frame.Vars.ToList())
                {
                    frame.Vars[variable.Key] = Redact(variable.Value, rules);
                }
            }
        }
    }

    private static void RedactLines(IList<string> lines, IReadOnlyList<RedactionRule> rules)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] is not null)
            {
                lines[i] = Redact(lines[i], rules);
            }
        }
    }

    /// <summary>
    /// Strings are redacted; numbers, booleans, enums and dates are kept (they are
    /// <see cref="IConvertible"/> and cannot hold a path). Any other object is
    /// replaced by its redacted <c>ToString()</c>, because the serializer would
    /// otherwise write its members (a <c>FileInfo</c>'s full path) unseen. Keys are
    /// redacted too: <c>Exception.Data</c> keys are free text.
    /// </summary>
    private static void RedactValues(IDictionary<string, object> data, IReadOnlyList<RedactionRule> rules)
    {
        var entries = data.ToList();
        data.Clear();
        foreach (var entry in entries)
        {
            data[Redact(entry.Key, rules)] = entry.Value switch
            {
                null => null!,
                string text => Redact(text, rules),
                IConvertible convertible => convertible,
                var other => Redact(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, rules),
            };
        }
    }

    /// <summary>
    /// Replaces the signed-in user's Linux identifiers with fixed tokens
    /// (<c>$HOME</c>, <c>$XDG_*</c>, <c>$USER</c>) and leaves the rest of the text alone.
    /// </summary>
    internal static string RedactUserIdentifiers(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Redact(value, BuildLiveRedactionRules());

    /// <summary>
    /// The <see cref="RedactUserIdentifiers(string?)"/> seam: the identifiers supplied
    /// instead of read from the live environment, so a test does not depend on who
    /// runs it. <paramref name="xdgDirectories"/> is keyed by variable name
    /// (<c>XDG_DATA_HOME</c>), and that name becomes the token.
    /// </summary>
    internal static string RedactUserIdentifiers(
        string? value,
        string? homeDirectory,
        IReadOnlyDictionary<string, string?>? xdgDirectories,
        string? userName)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : Redact(value, BuildRedactionRules(homeDirectory, xdgDirectories, userName));

    private enum MatchBoundary
    {
        /// <summary>The match must END a path segment: next char is '/' or end of text.
        /// Otherwise home <c>/home/bob</c> would rewrite a second account's
        /// <c>/home/bobby</c> into <c>$HOMEby</c>.</summary>
        DirectorySegment,

        /// <summary>A bare account name: neither neighbour may be a letter or digit.</summary>
        WholeWord,
    }

    private readonly record struct RedactionRule(string Identifier, string Token, MatchBoundary Boundary);

    private static IReadOnlyList<RedactionRule> BuildLiveRedactionRules()
    {
        var xdg = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variable in XdgDirectoryVariables)
        {
            xdg[variable] = ReadIdentifier(() => Environment.GetEnvironmentVariable(variable));
        }

        return BuildRedactionRules(
            ReadIdentifier(static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            xdg,
            ReadIdentifier(static () => Environment.UserName));

        // One unreadable identifier must not cost the event or the other identifiers.
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
    /// The only producer of rules. Directories first, longest first, so a path under
    /// an XDG directory inside <c>$HOME</c> reads <c>$XDG_DATA_HOME/...</c>; the bare
    /// account name LAST, because it is a substring of <c>/home/&lt;name&gt;</c> and
    /// would otherwise split the directory match. Blank identifiers and the root
    /// <c>/</c> are dropped, so no rule is ever zero-length.
    /// </summary>
    private static IReadOnlyList<RedactionRule> BuildRedactionRules(
        string? homeDirectory,
        IReadOnlyDictionary<string, string?>? xdgDirectories,
        string? userName)
    {
        var directories = new List<RedactionRule>();
        AddDirectory(homeDirectory, HomeToken);
        if (xdgDirectories is not null)
        {
            foreach (var xdg in xdgDirectories)
            {
                AddDirectory(xdg.Value, "$" + xdg.Key);
            }
        }

        // OrderByDescending is stable: two directories of one length keep their order.
        var rules = directories.OrderByDescending(rule => rule.Identifier.Length).ToList();

        if (!string.IsNullOrWhiteSpace(userName))
        {
            rules.Add(new RedactionRule(userName, UserToken, MatchBoundary.WholeWord));
        }

        return rules;

        void AddDirectory(string? directory, string token)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var trimmed = directory.TrimEnd('/');

            // "/" (a service account's home) trims to empty; replacing it would
            // mangle every absolute path for no privacy gain.
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            directories.Add(new RedactionRule(trimmed, token, MatchBoundary.DirectorySegment));
        }
    }

    /// <summary>
    /// One forward scan over the input. Emitted tokens are never re-read, so an
    /// account named <c>HOME</c> cannot rewrite the <c>$HOME</c> that just replaced it.
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
                builder.Append(token);
                index += matchLength;
                continue;
            }

            builder.Append(value[index]);
            index++;
        }

        return builder.ToString();
    }

    private static int MatchRule(string value, int index, IReadOnlyList<RedactionRule> rules, out string token)
    {
        foreach (var rule in rules)
        {
            var length = rule.Identifier.Length;
            if (index + length > value.Length)
            {
                continue;
            }

            // Case-insensitive: "Bob's AirPods" carries account "bob". Over-matching
            // a differently-cased path is the safe direction for a privacy filter.
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

    private static bool IsBoundedMatch(string value, int index, int length, MatchBoundary boundary)
        => boundary == MatchBoundary.DirectorySegment
            ? index + length == value.Length || value[index + length] == '/'
            : (index == 0 || IsAccountNameBoundary(value[index - 1]))
                && (index + length == value.Length || IsAccountNameBoundary(value[index + length]));

    /// <summary>
    /// The rule #934 measured as the only one that keeps both properties: a
    /// delimiter-inclusion set left <c>bob</c> in <c>bob.wav</c>, <c>bob@corp.com</c>
    /// and <c>(Bob's AirPods)</c>; a bare substring match shredded
    /// <c>HyperWhisper.SharedCore.dll</c> for account <c>ed</c>. The accepted cost is
    /// over-redaction for an account literally named like a token (<c>dll</c>).
    /// </summary>
    private static bool IsAccountNameBoundary(char character) => !char.IsLetterOrDigit(character);
}
