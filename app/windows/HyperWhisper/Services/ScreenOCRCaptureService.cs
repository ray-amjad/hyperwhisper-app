// SCREEN OCR CAPTURE SERVICE
// Captures a screenshot of the monitor containing the foreground window and
// extracts visible text via Windows.Media.Ocr (WinRT) for LLM post-processing
// context enrichment.
//
// DESIGN:
// - Singleton, stateless — each call captures fresh
// - Captures the monitor containing the foreground window at 50% resolution
// - Self-capture guard: returns null if HyperWhisper is foreground
// - 3-second timeout on the entire capture+OCR pipeline
// - Never throws: all failure paths return null (graceful degradation)
// - Logs metadata only (character count), never OCR content
//
// USAGE:
//   var text = await ScreenOCRCaptureService.Instance.CaptureAndOcrAsync();
//   // text is null on any failure, self-capture, timeout, or empty OCR result

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace HyperWhisper.Services;

public class ScreenOCRCaptureService
{
    // =========================================================================
    // SINGLETON
    // =========================================================================

    private static readonly Lazy<ScreenOCRCaptureService> _instance = new(() => new ScreenOCRCaptureService());
    public static ScreenOCRCaptureService Instance => _instance.Value;

    private ScreenOCRCaptureService() { }

    // =========================================================================
    // WIN32 P/INVOKE
    // =========================================================================

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // =========================================================================
    // PUBLIC API
    // =========================================================================

    /// <summary>
    /// Capture the monitor containing the foreground window and extract text via OCR.
    /// Returns null on failure (graceful degradation).
    /// </summary>
    /// <param name="maxCharacters">Maximum characters to return (default 2000).</param>
    public async Task<string?> CaptureAndOcrAsync(int maxCharacters = 2000)
    {
        try
        {
            // Wrap entire pipeline in 3-second timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await Task.Run(() => CaptureAndOcrCore(maxCharacters, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            LoggingService.Warn("ScreenOCRCaptureService: Timeout (3s)");
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ScreenOCRCaptureService: CaptureAndOcrAsync failed: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // CORE PIPELINE
    // =========================================================================

    private async Task<string?> CaptureAndOcrCore(int maxCharacters, CancellationToken ct)
    {
        // Step 1: Get foreground window
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            LoggingService.Debug("ScreenOCRCaptureService: No foreground window");
            return null;
        }

        // Step 2: Self-capture guard
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName.Equals("HyperWhisper", StringComparison.OrdinalIgnoreCase))
                {
                    LoggingService.Debug("ScreenOCRCaptureService: Foreground is HyperWhisper, skipping");
                    return null;
                }
            }
            catch
            {
                // Process may have exited — continue with capture
            }
        }

        ct.ThrowIfCancellationRequested();

        // Step 3: Find the monitor containing the foreground window
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            LoggingService.Warn("ScreenOCRCaptureService: GetMonitorInfo failed");
            return null;
        }

        var monitorRect = monitorInfo.rcMonitor;
        var captureWidth = monitorRect.Width / 2;
        var captureHeight = monitorRect.Height / 2;

        if (captureWidth <= 0 || captureHeight <= 0)
        {
            LoggingService.Warn("ScreenOCRCaptureService: Invalid monitor dimensions");
            return null;
        }

        ct.ThrowIfCancellationRequested();

        // Step 4: Capture the full monitor, then downscale to 50% resolution for OCR.
        // CopyFromScreen is a 1:1 BitBlt with no scaling: the block region must be
        // copied into a same-size bitmap, otherwise it is clipped to the destination
        // bounds (which previously captured only the top-left quadrant). Downscale
        // separately via DrawImage to honor the 50%-resolution intent.
        SoftwareBitmap softwareBitmap;
        using (var fullBitmap = new Bitmap(monitorRect.Width, monitorRect.Height, PixelFormat.Format32bppRgb))
        {
            using (var graphics = Graphics.FromImage(fullBitmap))
            {
                graphics.CopyFromScreen(
                    monitorRect.Left, monitorRect.Top,
                    0, 0,
                    new Size(monitorRect.Width, monitorRect.Height));
            }

            ct.ThrowIfCancellationRequested();

            using (var scaledBitmap = new Bitmap(captureWidth, captureHeight, PixelFormat.Format32bppRgb))
            {
                using (var scaleGraphics = Graphics.FromImage(scaledBitmap))
                {
                    scaleGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    scaleGraphics.DrawImage(fullBitmap, 0, 0, captureWidth, captureHeight);
                }

                ct.ThrowIfCancellationRequested();

                // Step 5: Convert to SoftwareBitmap for WinRT OCR
                softwareBitmap = await ConvertToSoftwareBitmapAsync(scaledBitmap);
            }
        }

        ct.ThrowIfCancellationRequested();

        // Step 6: Run OCR
        OcrResult ocrResult;
        try
        {
            var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null)
            {
                LoggingService.Warn("ScreenOCRCaptureService: No OCR engine available");
                return null;
            }

            ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
        }
        finally
        {
            softwareBitmap.Dispose();
        }

        ct.ThrowIfCancellationRequested();

        // Step 7: Extract and truncate text
        var text = string.Join(" ", ocrResult.Lines.Select(l => l.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            LoggingService.Debug("ScreenOCRCaptureService: No text detected");
            return null;
        }

        if (text.Length > maxCharacters)
            text = text[..maxCharacters];

        LoggingService.Info($"ScreenOCRCaptureService: Captured {text.Length} characters");
        return text;
    }

    // =========================================================================
    // BITMAP CONVERSION
    // =========================================================================

    /// <summary>
    /// Converts a System.Drawing.Bitmap to a Windows.Graphics.Imaging.SoftwareBitmap
    /// for use with the WinRT OCR engine.
    /// </summary>
    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Bmp);
        memoryStream.Position = 0;

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var dataWriter = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
        {
            dataWriter.WriteBytes(memoryStream.ToArray());
            await dataWriter.StoreAsync();
            dataWriter.DetachStream();
        }
        randomAccessStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
    }
}
