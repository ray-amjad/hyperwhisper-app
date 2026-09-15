// OPTIONAL ASSEMBLY GUARD (HYPERWHISPER-Y5 / HYPERWHISPER-YF)
//
// WHAT BREAKS WITHOUT THIS
// Windows Application Control (WDAC / Smart App Control) blocks an INDIVIDUAL DLL
// inside an installed application. Two of ours are reported blocked in production
// with HRESULT 0x800711C7:
//
//   HyperWhisper.AppClassification.dll — app-aware prompt classification
//   HyperWhisper.Statistics.dll        — the HomePage stats strip
//
// Both back an OPTIONAL feature. Neither is needed to record, transcribe or paste.
// Yet a blocked load took the whole flow down, because the CLR resolves an
// assembly reference while it PREPARES a method — before that method's own `try`
// block is entered. A `catch` inside the method therefore never runs, the
// FileLoadException escapes the `async void` handler, and the global
// unhandled-UI-exception path reports it (HYPERWHISPER-Y5, HYPERWHISPER-YF).
//
// THE RULE THIS FILE ENFORCES
// A call site must not name a type from an optional assembly in the SAME method
// that decides whether to use it. Put every such reference in a separate
// [MethodImpl(MethodImplOptions.NoInlining)] method, and call that method only
// after `IsAvailable` says the assembly loads. A method that is never called is
// never prepared, so a blocked assembly can no longer fault the caller.
//
// `NoInlining` is load-bearing, not decoration: an inlined body is prepared with
// its caller, which puts the reference straight back into the deciding method.
//
// The `catch` at each call site stays as a second line of defence. A failure that
// surfaces while preparing the CALLEE is thrown at the call instruction, which IS
// inside the caller's `try`, so it is catchable there.
//
// PRIVACY
// A FileLoadException message and its FileName both carry the installed path, and
// that path holds the user's Windows account name. Neither is ever logged or sent.
// This file records only the assembly's simple name (a constant in this file), the
// exception type name, the HRESULT, and a fixed stage slug.

using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace HyperWhisper.Services;

/// <summary>
/// Decides whether an optional satellite assembly can be loaded on this machine,
/// and remembers the answer for the life of the process.
/// </summary>
internal static class OptionalAssemblyGuard
{
    /// <summary>Backs app-aware classification for post-processing prompts.</summary>
    internal const string AppClassificationAssembly = "HyperWhisper.AppClassification";

    /// <summary>Backs the HomePage statistics strip.</summary>
    internal const string StatisticsAssembly = "HyperWhisper.Statistics";

    private static readonly ConcurrentDictionary<string, bool> Availability =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> Reported =
        new(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="simpleName"/> loads on this machine. The probe
    /// runs once per assembly; every later call reads the remembered answer.
    /// </summary>
    /// <remarks>
    /// The probe loads by NAME, through a string. It names no type from the
    /// assembly, so this method itself is always safe to prepare.
    /// </remarks>
    internal static bool IsAvailable(string simpleName) =>
        Availability.GetOrAdd(simpleName, Probe);

    /// <summary>
    /// Records that <paramref name="simpleName"/> failed at a real call site, so
    /// the next caller takes the degraded path without a second failure.
    /// </summary>
    internal static void MarkUnavailable(string simpleName, Exception exception, string stage)
    {
        Availability[simpleName] = false;
        Report(simpleName, exception, stage);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is an assembly or type load failure —
    /// the shape Application Control produces — rather than an ordinary fault.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A call site catches only this shape and lets every
    /// other exception keep its existing handling, so this guard cannot swallow a
    /// genuine bug in the feature it protects.
    /// </remarks>
    internal static bool IsLoadFailure(Exception exception) => exception switch
    {
        FileLoadException or FileNotFoundException or BadImageFormatException or TypeLoadException => true,
        // A static constructor that trips the load reaches the caller wrapped.
        TypeInitializationException initialization =>
            initialization.InnerException is { } inner && IsLoadFailure(inner),
        _ => false,
    };

    /// <summary>The 8-digit hex HRESULT, or <c>unknown</c> when there is none.</summary>
    internal static string DescribeHResult(int? value) => value.HasValue
        ? $"0x{unchecked((uint)value.Value):X8}"
        : "unknown";

    private static bool Probe(string simpleName)
    {
        try
        {
            _ = Assembly.Load(new AssemblyName(simpleName));
            return true;
        }
        catch (Exception exception) when (IsLoadFailure(exception))
        {
            Report(simpleName, exception, "probe");
            return false;
        }
        catch (Exception exception)
        {
            // An unexpected shape is not evidence that the assembly is blocked.
            // Report it and treat the assembly as available, so a wrong guess here
            // cannot disable a working feature.
            LoggingService.Error(
                $"OptionalAssemblyGuard: Probe failed for an unexpected reason " +
                $"(assembly={simpleName}, exception_type={exception.GetType().FullName}, " +
                $"hresult={DescribeHResult(exception.HResult)})");
            return true;
        }
    }

    private static void Report(string simpleName, Exception exception, string stage)
    {
        var innermost = exception;
        while (innermost.InnerException != null)
        {
            innermost = innermost.InnerException;
        }

        // Never `exception.Message` and never `FileLoadException.FileName`: both
        // carry the installed path, and that path holds the user's account name.
        LoggingService.Error(
            $"OptionalAssemblyGuard: Optional assembly unavailable " +
            $"(assembly={simpleName}, stage={stage}, " +
            $"outer_exception_type={exception.GetType().FullName}, " +
            $"outer_hresult={DescribeHResult(exception.HResult)}, " +
            $"innermost_exception_type={innermost.GetType().FullName}, " +
            $"innermost_hresult={DescribeHResult(innermost.HResult)})");

        // One Sentry event per assembly per process: a blocked DLL fails on every
        // attempt, and the second report says nothing the first did not.
        if (!Reported.TryAdd(simpleName, 0))
        {
            return;
        }

        // A degraded-mode notice must never become the next crash, so the report
        // carries its own guard.
        try
        {
            if (!SettingsService.Instance.EnableErrorLogging)
            {
                return;
            }

            SentryService.CaptureDiagnosticEvent(
                message: "Optional assembly unavailable",
                extras: new(StringComparer.Ordinal)
                {
                    ["optional_assembly_stage"] = stage,
                    ["optional_assembly_outer_exception_type"] = exception.GetType().FullName ?? "unknown",
                    ["optional_assembly_outer_hresult"] = DescribeHResult(exception.HResult),
                    ["optional_assembly_innermost_exception_type"] = innermost.GetType().FullName ?? "unknown",
                    ["optional_assembly_innermost_hresult"] = DescribeHResult(innermost.HResult),
                },
                tags: new(StringComparer.Ordinal)
                {
                    ["component"] = "optional_assembly",
                    ["diagnostic_name"] = "optional_assembly_unavailable",
                    ["optional_assembly_name"] = simpleName,
                    ["optional_assembly_stage"] = stage,
                },
                fingerprint: ["optional-assembly", "unavailable", simpleName],
                dedupeKey: $"optional-assembly:unavailable:{simpleName}");
        }
        catch (Exception reportException)
        {
            LoggingService.Error(
                $"OptionalAssemblyGuard: Failure report failed " +
                $"(assembly={simpleName}, exception_type={reportException.GetType().FullName}, " +
                $"hresult={DescribeHResult(reportException.HResult)})");
        }
    }
}
