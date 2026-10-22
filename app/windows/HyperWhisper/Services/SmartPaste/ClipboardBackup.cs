using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using static HyperWhisper.Services.SmartPasteDelayTable;
using static HyperWhisper.Services.SmartPasteNativeMethods;

// Alias WPF clipboard types to avoid ambiguity with WinForms
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

namespace HyperWhisper.Services;

/// <summary>
/// CLIPBOARD BACKUP
///
/// The clipboard snapshot SmartPasteService takes at recording start and puts
/// back after the paste, with the state that guards it: the saved formats, the
/// restore timer's CancellationTokenSource, the clipboard lock, and the
/// sequence-number marks that keep chained dictations (#1496), a copy made
/// after the transcript (#1583) and an empty clipboard (#1584) right.
///
/// Split out of SmartPasteService (#862) so that state is reachable only from
/// here; SmartPasteService owns one instance and forwards to it. The lock and
/// the CancellationTokenSource live in this class with every method that uses
/// them. Log lines keep the "SmartPasteService:" prefix they always had.
/// </summary>
internal sealed class ClipboardBackup : IDisposable
{
    // =========================================================================
    // INSTANCE FIELDS
    // =========================================================================

    // Clipboard preservation state
    // We store clipboard data as a dictionary of format -> data
    // because IDataObject instances cannot be reused after Clipboard.Clear()
    private Dictionary<string, object>? _savedClipboardData;
    private CancellationTokenSource? _restorationCts;
    private bool _isInRecordingSession;

    // The clipboard sequence number read right after this service last wrote a
    // transcript to the clipboard, or 0 when none is current (#1496). It is set
    // only once a restore of the snapshot is scheduled for that transcript, so
    // it means "this transcript is on the clipboard and the user's content is
    // due back". While the clipboard still shows this number, a new session must
    // keep the older snapshot instead of capturing the transcript.
    private uint _ownTranscriptClipboardSequence;

    // When the mark above stops counting, in Environment.TickCount64 ms, or
    // long.MaxValue when it lasts until the restore runs. Set only for a
    // transcript pasted over an EMPTY clipboard (#1584): with nothing to restore
    // no restore ever clears the mark, so it lapses when the restore delay would
    // have ended. After that the transcript is just the clipboard's content.
    private long _ownTranscriptMarkExpiresAt = long.MaxValue;

    // When the restore this session scheduled over an empty clipboard would have
    // run (#1584), in TickCountMs ms, or 0 when it scheduled none. A transcript
    // written after that (a late streaming segment) takes over the mark until
    // then, instead of being treated as left on purpose.
    private long _emptyClipboardRestoreEndsAt;

    /// <summary>
    /// SmartPasteService.TickCountMs, called at each read, so a test that swaps
    /// that seam after construction still moves this clock.
    /// </summary>
    private Func<long> TickCountMs { get; }

    // The sequence number of a transcript written in this session for which no
    // restore has been scheduled yet, or 0. ScheduleClipboardRestore moves it to
    // _ownTranscriptClipboardSequence; EndRecordingSession, finding it still set,
    // treats the transcript as left on the clipboard on purpose (secure field,
    // restore off) and drops the mark and the snapshot.
    private uint _unscheduledTranscriptClipboardSequence;

    // The clipboard sequence number a restore expects to find (#1583): the one
    // read right after this service last wrote a transcript, or, before any
    // write, the one read when the snapshot was taken. Any other number means
    // something else wrote to the clipboard since (the user copied something
    // new), so the restore must leave the clipboard alone. 0 = unknown (the
    // number could not be read), which restores as before.
    private uint _restoreExpectedClipboardSequence;
    private readonly object _clipboardLock = new();
    private bool _disposed;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    /// <param name="tickCountMs">Reads SmartPasteService.TickCountMs at call time.</param>
    internal ClipboardBackup(Func<long> tickCountMs)
    {
        TickCountMs = tickCountMs;
    }

    // =========================================================================
    // SNAPSHOT AND RESTORE
    // =========================================================================

    /// <summary>
    /// Starts a recording session.
    /// Captures the current clipboard content for later restoration.
    /// Must be called at the beginning of recording, before any clipboard modifications.
    ///
    /// CLIPBOARD CAPTURE FLOW:
    /// 1. Cancel any pending restoration from a previous recording
    /// 2. Extract data from ALL clipboard formats (not just text)
    /// 3. Store raw data for later restoration
    ///
    /// CHAINED DICTATIONS (#1496):
    /// When the last transcript this service wrote had a restore scheduled, and
    /// nothing has written to the clipboard since, the clipboard holds that
    /// transcript, not the user's content. Capturing it would make this session's
    /// restore write the old transcript back, and lose the user's clipboard for
    /// good. So the earlier snapshot is kept instead. This covers a dictation
    /// started inside the previous one's restore window, and one after a
    /// dictation in that chain that wrote nothing (no speech, cancel). If the
    /// user (or anything else) copied in between, the sequence number has moved
    /// and a fresh snapshot is taken. A transcript left on the clipboard with no
    /// restore (secure field, restore off) is not marked, so the next session
    /// snapshots it as the clipboard, as before. A transcript pasted over an
    /// EMPTY clipboard is marked only until its restore delay ends (#1584), so a
    /// quick second dictation leaves its own transcript, not the first one.
    ///
    /// WHY EXTRACT DATA INSTEAD OF STORING IDATAOBJECT?
    /// Windows IDataObject instances are tied to the clipboard they came from.
    /// After Clipboard.SetDataObject() is called, the old IDataObject becomes invalid.
    /// We must extract the raw data (bytes, strings, file lists) to restore later.
    /// </summary>
    public void StartRecordingSession()
    {
        LoggingService.Info("SmartPasteService: Starting recording session");

        // Cancel any pending restoration from a previous recording
        CancelPendingRestore();

        lock (_clipboardLock)
        {
            _unscheduledTranscriptClipboardSequence = 0;
            _emptyClipboardRestoreEndsAt = 0;

            if (ClipboardHoldsOwnTranscript())
            {
                // Keep _savedClipboardData as it is, null included: an empty
                // clipboard before the first dictation stays "nothing to restore",
                // exactly as after a single dictation. The restore's expected
                // sequence number stays the previous transcript's (#1583), so a
                // copy made before this session writes anything still counts.
                LoggingService.Info(
                    "SmartPasteService: Clipboard still holds the previous transcript; keeping the earlier clipboard snapshot");
                _isInRecordingSession = true;
                return;
            }

            _savedClipboardData = null;
            _ownTranscriptClipboardSequence = 0;

            // Read BEFORE the capture (#1583): a write that lands while the
            // formats are being read moves the number, and the restore then
            // leaves that newer content alone instead of trusting a snapshot
            // that may be half old, half new.
            _restoreExpectedClipboardSequence = GetClipboardSequenceNumber();

            try
            {
                // Extract data from clipboard for each supported format.
                // An empty (or fully-unsupported) clipboard simply yields an empty
                // dataDict and is handled by the Count check below. We intentionally
                // do NOT pre-screen with a hand-maintained format list here, because
                // it can drift from SupportedFormats and silently skip capture for
                // clipboards that hold only Html/Csv/Text (no Unicode-text fallback),
                // which would destroy that content on paste with nothing to restore.
                var dataDict = new Dictionary<string, object>();
                var capturedFormats = new List<string>();

                foreach (var format in SupportedFormats)
                {
                    try
                    {
                        if (Clipboard.ContainsData(format))
                        {
                            var data = Clipboard.GetData(format);
                            if (data != null)
                            {
                                // Special handling for different data types
                                // We need to make copies of mutable data
                                if (format == DataFormats.FileDrop && data is string[] files)
                                {
                                    // Copy file path array
                                    dataDict[format] = (string[])files.Clone();
                                    capturedFormats.Add("FileDrop");
                                }
                                else if (format == DataFormats.Bitmap && data is BitmapSource bitmap)
                                {
                                    // Convert to PNG bytes for reliable restoration
                                    using var stream = new MemoryStream();
                                    var encoder = new PngBitmapEncoder();
                                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                    encoder.Save(stream);
                                    dataDict[format] = stream.ToArray();
                                    capturedFormats.Add("Bitmap");
                                }
                                else if (data is string strData)
                                {
                                    // String data can be stored directly
                                    dataDict[format] = strData;
                                    capturedFormats.Add(format);
                                }
                                else if (data is MemoryStream ms)
                                {
                                    // Memory stream: copy the bytes
                                    dataDict[format] = ms.ToArray();
                                    capturedFormats.Add(format);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue - some formats may fail due to app-specific issues
                        LoggingService.Debug($"SmartPasteService: Failed to capture format {format}: {ex.Message}");
                    }
                }

                if (dataDict.Count > 0)
                {
                    _savedClipboardData = dataDict;
                    LoggingService.Info($"SmartPasteService: Saved clipboard with {dataDict.Count} format(s): {string.Join(", ", capturedFormats)}");
                }
                else
                {
                    LoggingService.Info("SmartPasteService: No supported clipboard formats found");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"SmartPasteService: Failed to capture clipboard: {ex.Message}");
                _savedClipboardData = null;
            }

            _isInRecordingSession = true;
        }
    }

    /// <summary>
    /// Whether the clipboard still holds the transcript this service last wrote,
    /// i.e. nothing has written to it since (#1496). Callers hold _clipboardLock.
    /// </summary>
    private bool ClipboardHoldsOwnTranscript()
    {
        if (_ownTranscriptClipboardSequence == 0)
            return false;

        if (TickCountMs() >= _ownTranscriptMarkExpiresAt)
            return false;

        var current = GetClipboardSequenceNumber();
        return current != 0 && current == _ownTranscriptClipboardSequence;
    }

    /// <summary>
    /// Records that the clipboard now holds a transcript this service wrote.
    /// Call right after the write returns. The mark is armed only when a restore
    /// is scheduled for it (ScheduleClipboardRestore or EndRecordingSession).
    /// </summary>
    internal void MarkClipboardHoldsOwnTranscript()
    {
        lock (_clipboardLock)
        {
            var sequence = GetClipboardSequenceNumber();
            _ownTranscriptClipboardSequence = 0;
            _unscheduledTranscriptClipboardSequence = sequence;
            // Whatever restore runs next must find this transcript (#1583).
            _restoreExpectedClipboardSequence = sequence;
        }
    }

    /// <summary>
    /// Whether something other than this service has written to the clipboard
    /// since its last transcript write (or since the snapshot, when nothing was
    /// written), so the clipboard no longer holds what the restore would replace
    /// (#1583). False when either number cannot be read: then the restore runs
    /// as it did before. Callers hold _clipboardLock.
    /// </summary>
    private bool ClipboardChangedSinceOwnWrite()
    {
        if (_restoreExpectedClipboardSequence == 0)
            return false;

        var current = GetClipboardSequenceNumber();
        return current != 0 && current != _restoreExpectedClipboardSequence;
    }

    /// <summary>
    /// Ends the recording session.
    /// Call this after transcription and paste are complete.
    /// </summary>
    public void EndRecordingSession()
    {
        lock (_clipboardLock)
        {
            if (_unscheduledTranscriptClipboardSequence != 0)
            {
                if (_restorationCts != null && _savedClipboardData is { Count: > 0 })
                {
                    // A transcript written after this session's restore was
                    // scheduled (a late streaming segment): that restore still
                    // brings the user's content back, so it covers this one too.
                    _ownTranscriptClipboardSequence = _unscheduledTranscriptClipboardSequence;
                    _ownTranscriptMarkExpiresAt = long.MaxValue;
                }
                else if (_emptyClipboardRestoreEndsAt != 0)
                {
                    // The same after a restore scheduled over an empty clipboard
                    // (#1584): the late segment is the app's own text until that
                    // window ends, like the transcript before it.
                    _ownTranscriptClipboardSequence = _unscheduledTranscriptClipboardSequence;
                    _ownTranscriptMarkExpiresAt = _emptyClipboardRestoreEndsAt;
                }
                else
                {
                    // The transcript stays on the clipboard on purpose (secure
                    // field, restore off): it is now the clipboard's content. The
                    // next session snapshots it afresh, as before #1496, and no
                    // stale snapshot lingers to be restored later.
                    _ownTranscriptClipboardSequence = 0;
                    _savedClipboardData = null;
                    LoggingService.Debug(
                        "SmartPasteService: Transcript left on the clipboard with no restore; dropped the clipboard snapshot");
                }

                _unscheduledTranscriptClipboardSequence = 0;
            }

            _isInRecordingSession = false;
        }

        LoggingService.Debug("SmartPasteService: Ended recording session");
    }

    /// <summary>
    /// Cancels any pending clipboard restoration.
    /// Called when a new recording starts to prevent stale restorations.
    /// </summary>
    public void CancelPendingRestore()
    {
        if (_restorationCts != null)
        {
            _restorationCts.Cancel();
            _restorationCts.Dispose();
            _restorationCts = null;
            LoggingService.Debug("SmartPasteService: Cancelled pending clipboard restoration");
        }
    }

    public bool HasPendingClipboardRestore
    {
        get
        {
            lock (_clipboardLock)
            {
                return _restorationCts != null && _savedClipboardData is { Count: > 0 };
            }
        }
    }

    /// <summary>
    /// Schedules restoration of the original clipboard content.
    /// Called after successful paste to restore user's previous clipboard.
    ///
    /// RESTORATION FLOW:
    /// 1. Wait for configurable delay (allows user to paste transcription again if needed)
    /// 2. Clear current clipboard
    /// 3. Restore original clipboard content with ALL formats
    ///
    /// The restoration is cancellable - if a new recording starts before
    /// the delay expires, the restoration is cancelled.
    /// </summary>
    internal void ScheduleClipboardRestore(TimeSpan delay)
    {
        // Check settings - is restoration enabled?
        if (!SettingsService.Instance.RestoreClipboardAfterPaste)
        {
            LoggingService.Debug("SmartPasteService: Clipboard restoration disabled in settings");
            return;
        }

        // Check if we have content to restore
        lock (_clipboardLock)
        {
            if (_savedClipboardData == null || _savedClipboardData.Count == 0)
            {
                // #1584: the clipboard was empty, so the transcript stays on it.
                // For the restore delay it still counts as the app's own text:
                // a dictation started in that window keeps "nothing to restore"
                // instead of snapshotting this transcript and restoring it over
                // its own. After the delay it is the clipboard's content.
                _emptyClipboardRestoreEndsAt = TickCountMs() + (long)delay.TotalMilliseconds;
                if (_unscheduledTranscriptClipboardSequence != 0)
                {
                    _ownTranscriptClipboardSequence = _unscheduledTranscriptClipboardSequence;
                    _unscheduledTranscriptClipboardSequence = 0;
                    _ownTranscriptMarkExpiresAt = _emptyClipboardRestoreEndsAt;
                }

                LoggingService.Debug("SmartPasteService: No clipboard content to restore");
                return;
            }
        }

        LoggingService.Info(
            $"SmartPasteService: Scheduling clipboard restoration in {delay.TotalSeconds:F1} seconds");

        // Cancel any existing restoration timer
        CancelPendingRestore();

        // Create new cancellation token for this restoration
        _restorationCts = new CancellationTokenSource();
        var token = _restorationCts.Token;

        // #1496: the transcript this session wrote is now one whose restore is
        // due, so a session started before that restore runs keeps the snapshot.
        // With no write this session (nothing pasted), the earlier mark stands.
        lock (_clipboardLock)
        {
            if (_unscheduledTranscriptClipboardSequence != 0)
            {
                _ownTranscriptClipboardSequence = _unscheduledTranscriptClipboardSequence;
                _unscheduledTranscriptClipboardSequence = 0;
                _ownTranscriptMarkExpiresAt = long.MaxValue;
            }
        }

        // Schedule the restoration without blocking the caller
        _ = RestoreClipboardAfterDelayAsync(delay, token);
    }

    private async Task RestoreClipboardAfterDelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                return;
            }

            // Restore must happen on STA thread (UI thread)
            // Use Application.Current.Dispatcher to marshal to UI thread
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                RestoreClipboard();
            });
        }
        catch (TaskCanceledException)
        {
            // Expected when restoration is cancelled
            LoggingService.Debug("SmartPasteService: Clipboard restoration was cancelled");
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"SmartPasteService: Clipboard restoration failed: {ex.Message}");
        }
    }

    internal bool TryRestoreClipboardImmediately()
    {
        if (!SettingsService.Instance.RestoreClipboardAfterPaste)
        {
            LoggingService.Debug("SmartPasteService: Clipboard restoration disabled in settings");
            return true;
        }

        CancelPendingRestore();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(RestoreClipboard);
        }

        return RestoreClipboard();
    }

    /// <summary>
    /// Restores the saved clipboard content.
    /// Must be called on the UI thread (STA thread for clipboard access).
    ///
    /// RACE CONDITION PROTECTION:
    /// The entire restoration logic is protected by _clipboardLock to prevent
    /// race conditions between:
    /// 1. Scheduled restoration callback (after delay)
    /// 2. New recording session starting (CancelPendingRestore + StartRecordingSession)
    /// 3. Disposal of the service
    ///
    /// Without this lock, a restoration could run while a new recording is capturing
    /// clipboard, leading to corrupted state or restoring the wrong content.
    /// </summary>
    private bool RestoreClipboard()
    {
        lock (_clipboardLock)
        {
            // Check if disposed - don't restore after cleanup
            if (_disposed)
            {
                LoggingService.Debug("SmartPasteService: Cannot restore clipboard - service disposed");
                return false;
            }

            // Check if we have data to restore
            if (_savedClipboardData == null || _savedClipboardData.Count == 0)
            {
                LoggingService.Debug("SmartPasteService: No clipboard content to restore");
                return true;
            }

            // #1583: put the snapshot back only over the app's own transcript.
            // If the user (or anything) copied something after it, that copy is
            // now the user's clipboard: keep it, and drop the stale snapshot so
            // no later restore writes it back either. This is not a failure.
            if (ClipboardChangedSinceOwnWrite())
            {
                _savedClipboardData = null;
                _ownTranscriptClipboardSequence = 0;
                _restoreExpectedClipboardSequence = 0;
                LoggingService.Info(
                    "SmartPasteService: Clipboard changed since the transcript was written; skipped the restore and dropped the clipboard snapshot");
                return true;
            }

            // Make a local copy of the data to restore
            var dataToRestore = _savedClipboardData;

            // Clear saved data if not in a recording session
            // This prevents double-restoration and allows new recordings to capture fresh clipboard
            if (!_isInRecordingSession)
            {
                _savedClipboardData = null;
            }

            try
            {
                // Create new DataObject with all saved formats
                var dataObject = new DataObject();
                var restoredFormats = new List<string>();

                foreach (var kvp in dataToRestore)
                {
                    try
                    {
                        var format = kvp.Key;
                        var data = kvp.Value;

                        if (format == DataFormats.Bitmap && data is byte[] imageBytes)
                        {
                            // Restore image from PNG bytes
                            using var stream = new MemoryStream(imageBytes);
                            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0)
                            {
                                dataObject.SetData(format, decoder.Frames[0]);
                                restoredFormats.Add("Bitmap");
                            }
                        }
                        else if (data is byte[] streamBytes)
                        {
                            // Restore memory stream data
                            dataObject.SetData(format, new MemoryStream(streamBytes));
                            restoredFormats.Add(format);
                        }
                        else
                        {
                            // Direct data (strings, file arrays)
                            dataObject.SetData(format, data);
                            restoredFormats.Add(format);
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"SmartPasteService: Failed to restore format {kvp.Key}: {ex.Message}");
                    }
                }

                if (restoredFormats.Count > 0)
                {
                    Clipboard.SetDataObject(dataObject, true);
                    // The clipboard holds the user's content again, not a transcript.
                    _ownTranscriptClipboardSequence = 0;
                    // A snapshot kept for the rest of a recording session may be
                    // restored again; that is fine only over this very write.
                    _restoreExpectedClipboardSequence = GetClipboardSequenceNumber();
                    LoggingService.Info($"SmartPasteService: Restored clipboard with {restoredFormats.Count} format(s): {string.Join(", ", restoredFormats)}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"SmartPasteService: Failed to restore clipboard: {ex.Message}");
                return false;
            }
        }
    }

    // =========================================================================
    // TRANSCRIPT WRITE
    // =========================================================================

    /// <summary>
    /// Sets text on the clipboard, optionally excluding it from Windows clipboard
    /// history (Win+V) and third-party clipboard managers.
    ///
    /// Uses the "ExcludeClipboardContentFromMonitorProcessing" clipboard format,
    /// which is the standard Windows mechanism for hiding sensitive clipboard data.
    /// This is the same approach used by password managers (1Password, KeePass, etc.)
    /// and is the Windows equivalent of macOS's org.nspasteboard.ConcealedType.
    /// </summary>
    internal static void SetClipboardText(string text)
    {
        if (SettingsService.Instance.HideFromClipboardHistory)
        {
            var dataObject = new DataObject();
            dataObject.SetData(DataFormats.UnicodeText, text);
            dataObject.SetData("ExcludeClipboardContentFromMonitorProcessing", "");
            Clipboard.SetDataObject(dataObject, true);
        }
        else
        {
            Clipboard.SetText(text);
        }
    }

    // =========================================================================
    // IDISPOSABLE IMPLEMENTATION
    // =========================================================================

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Disposes of the service and cleans up resources.
    ///
    /// CLEANUP RESPONSIBILITIES:
    /// 1. Cancel any pending clipboard restoration
    /// 2. Dispose of the CancellationTokenSource
    /// 3. Clear saved clipboard data
    /// 4. Set disposed flag to prevent further operations
    ///
    /// This ensures proper cleanup when the app closes and prevents
    /// restoration callbacks from running after disposal.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_clipboardLock)
        {
            if (_disposed)
                return;

            _disposed = true;

            // Cancel any pending clipboard restoration
            CancelPendingRestore();

            // Clear saved clipboard data
            _savedClipboardData = null;
            _ownTranscriptClipboardSequence = 0;
            _unscheduledTranscriptClipboardSequence = 0;
            _restoreExpectedClipboardSequence = 0;
            _emptyClipboardRestoreEndsAt = 0;

            LoggingService.Debug("SmartPasteService: Disposed");
        }
    }
}
