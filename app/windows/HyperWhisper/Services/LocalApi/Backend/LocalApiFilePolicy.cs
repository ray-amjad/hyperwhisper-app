using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// The Windows half of the <c>/transcribe</c> <c>file</c> path policy, plugged
/// into the shared server's hooks (issue #356 item 6).
/// </summary>
/// <remarks>
/// <para>
/// The shared server owns the policy: the path must be fully qualified, lie
/// under one of the allowed roots, and cross no reparse point below that root.
/// It then reads the bytes into memory, so the backend transcribes a private
/// copy and the caller cannot swap the file afterwards (#740's snapshot, by
/// another route).
/// </para>
/// <para>
/// Windows adds two things. The roots are read LIVE, so a recordings folder
/// changed in Settings applies at once, as it did when the old endpoint asked
/// <see cref="HistoryService.IsTrustedAudioPath(string?)"/> per request. And the
/// opened handle is asked where it really landed (<c>GetFinalPathNameByHandle</c>),
/// which closes the window between the shared path check and the open: a
/// component swapped for a junction in between lands outside the roots and is
/// refused (#740).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class LocalApiFilePolicy
{
    private const uint FinalPathNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>The trusted recording roots, re-read on every access.</summary>
    internal static IReadOnlyList<string> AllowedRoots { get; } = new LiveTrustedRoots();

    /// <summary>
    /// The one trusted file outside every root: a <c>%TEMP%\hyperwhisper_*.wav</c>
    /// recording fallback (<see cref="HistoryService.IsTempRecordingFallbackFile"/>).
    /// </summary>
    internal static bool IsAdditionalAllowedFile(string fullPath) =>
        HistoryService.IsTempRecordingFallbackFile(fullPath);

    /// <summary>
    /// Whether the opened file really lives in a trusted location. Any failure
    /// to ask is a refusal.
    /// </summary>
    internal static bool VerifyOpenedFile(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var openedPath = GetFinalDosPath(stream.SafeFileHandle);
            return HistoryService.IsTrustedAudioPath(openedPath, ResolveRealPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            LoggingService.Debug($"LocalAPI /transcribe: could not resolve an opened file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolve <paramref name="canonicalPath"/> (already absolute and lexically
    /// normalized) to its real on-disk target, following every reparse point on
    /// the leaf and on each parent. Used to resolve a trusted root that is itself
    /// reparsed (a Documents folder that is a junction), so its real location
    /// still counts as trusted when the opened handle reports it.
    /// </summary>
    internal static string ResolveRealPath(string canonicalPath)
    {
        // A volume root is where the parent walk always ends, and
        // Directory.ResolveLinkTarget throws for one, so answer for it here.
        if (string.Equals(canonicalPath, Path.GetPathRoot(canonicalPath), StringComparison.OrdinalIgnoreCase))
        {
            return canonicalPath;
        }

        var leafTarget = File.Exists(canonicalPath)
            ? File.ResolveLinkTarget(canonicalPath, returnFinalTarget: true)
            : Directory.ResolveLinkTarget(canonicalPath, returnFinalTarget: true);
        if (leafTarget != null)
        {
            return Path.GetFullPath(leafTarget.FullName);
        }

        var parent = Path.GetDirectoryName(canonicalPath);
        if (string.IsNullOrEmpty(parent))
        {
            return canonicalPath;
        }

        var realParent = ResolveRealPath(parent);
        return Path.GetFullPath(Path.Combine(realParent, Path.GetFileName(canonicalPath)));
    }

    private static string GetFinalDosPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, FinalPathNameNormalized | VolumeNameDos);
        if (length == 0)
        {
            throw new IOException($"GetFinalPathNameByHandle failed with Win32 error {Marshal.GetLastWin32Error()}");
        }

        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, FinalPathNameNormalized | VolumeNameDos);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new IOException($"GetFinalPathNameByHandle failed with Win32 error {Marshal.GetLastWin32Error()}");
            }
        }

        return Path.GetFullPath(StripExtendedPathPrefix(buffer.ToString()));
    }

    private static string StripExtendedPathPrefix(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";

        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    /// <summary>
    /// <see cref="HistoryService.GetTrustedAudioRoots"/> as a list the shared
    /// server can hold for its lifetime, answering from the current settings
    /// each time it is read.
    /// </summary>
    private sealed class LiveTrustedRoots : IReadOnlyList<string>
    {
        private static List<string> Current() =>
            HistoryService.GetTrustedAudioRoots().Where(root => !string.IsNullOrWhiteSpace(root)).ToList();

        public int Count => Current().Count;
        public string this[int index] => Current()[index];
        public IEnumerator<string> GetEnumerator() => Current().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
