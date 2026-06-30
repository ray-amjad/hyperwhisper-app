using System.Management;
using System.Runtime.InteropServices;

namespace HyperWhisper.Services;

/// <summary>
/// GPU INFORMATION SERVICE
///
/// Purpose:
/// Detects GPU hardware information including VRAM (dedicated video memory).
/// Used to warn users when they select a Whisper model that requires more
/// VRAM than their GPU has available.
///
/// MEMORY DETECTION STRATEGY:
/// 1. PRIMARY: DXGI (DirectX Graphics Infrastructure) API
///    - Uses IDXGIFactory/IDXGIAdapter to query DXGI_ADAPTER_DESC
///    - Provides 64-bit memory values (no 4GB cap)
///    - Reports DedicatedVideoMemory, DedicatedSystemMemory, and SharedSystemMemory
///    - Works correctly for APUs where SharedSystemMemory can be 64-96GB+
///
/// 2. FALLBACK: WMI (Win32_VideoController)
///    - AdapterRAM is a 32-bit uint, capped at ~4GB
///    - Used when DXGI fails (older systems, virtual machines)
///    - Includes heuristics to estimate VRAM from GPU name
///
/// GPU SELECTION:
/// When multiple GPUs are present (e.g., integrated Intel + discrete NVIDIA),
/// this service returns information about the "best" GPU for ML workloads:
/// 1. NVIDIA GPUs (GeForce, RTX, Quadro) - best for ML
/// 2. AMD discrete GPUs (Radeon RX, Vega)
/// 3. Intel Arc GPUs
/// 4. Intel integrated GPUs (Iris, UHD) - slowest but functional
///
/// APU DETECTION:
/// APUs (Accelerated Processing Units) like AMD Ryzen with integrated graphics
/// have small dedicated VRAM but can access large amounts of shared system memory.
/// This service detects APUs and reports their effective VRAM for model selection.
/// </summary>
public class GpuInfoService
{
    /// <summary>
    /// Information about a detected GPU.
    /// </summary>
    public class GpuInfo
    {
        /// <summary>
        /// GPU adapter name (e.g., "NVIDIA GeForce RTX 4060").
        /// </summary>
        public string Name { get; set; } = "Unknown";

        /// <summary>
        /// Dedicated video memory (VRAM) in bytes.
        /// For discrete GPUs, this is the GPU's onboard memory.
        /// For APUs, this is typically a small carve-out from system RAM (1-4GB).
        /// </summary>
        public long DedicatedVramBytes { get; set; }

        /// <summary>
        /// Shared system memory available to the GPU in bytes.
        /// For APUs, this can be very large (64-96GB+ depending on system RAM).
        /// For discrete GPUs, this is typically 0 or a small amount.
        /// </summary>
        public long SharedMemoryBytes { get; set; }

        /// <summary>
        /// Whether this GPU is an APU (Accelerated Processing Unit).
        /// APUs have integrated graphics that share system RAM.
        /// Detected when SharedMemoryBytes significantly exceeds DedicatedVramBytes.
        ///
        /// Gated on !IsDiscrete because DXGI reports SharedSystemMemory (host RAM
        /// reachable over PCIe) for discrete adapters too. Without this gate the
        /// size heuristic alone misflags small discrete cards (e.g. RTX 2060 6 GB)
        /// as APUs and treats shared system memory as usable VRAM.
        /// </summary>
        public bool IsAPU => !IsDiscrete
                             && DedicatedVramBytes < 8L * 1024 * 1024 * 1024
                             && SharedMemoryBytes > DedicatedVramBytes;

        /// <summary>
        /// Effective VRAM for model selection purposes.
        /// For discrete GPUs: uses dedicated VRAM.
        /// For APUs: uses the larger of dedicated VRAM or 50% of shared memory.
        ///
        /// The 50% factor is conservative because:
        /// - Shared memory is slower than dedicated VRAM
        /// - System needs some RAM for other processes
        /// - ML workloads may have memory access patterns that don't benefit from shared memory
        /// </summary>
        public long EffectiveVramBytes => IsAPU
            ? Math.Max(DedicatedVramBytes, SharedMemoryBytes / 2)
            : DedicatedVramBytes;

        /// <summary>
        /// Backwards-compatible property: returns EffectiveVramBytes.
        /// This ensures existing code continues to work.
        /// </summary>
        public long VramBytes
        {
            get => EffectiveVramBytes;
            set => DedicatedVramBytes = value;  // For backwards compatibility with WMI fallback
        }

        /// <summary>
        /// Dedicated video memory (VRAM) in gigabytes.
        /// </summary>
        public double VramGB => EffectiveVramBytes / (1024.0 * 1024.0 * 1024.0);

        /// <summary>
        /// Human-readable VRAM string (e.g., "8 GB").
        /// For APUs, indicates shared memory is being used.
        /// </summary>
        public string VramDisplay
        {
            get
            {
                if (EffectiveVramBytes >= 1024L * 1024 * 1024)
                {
                    string suffix = IsAPU ? " (shared)" : "";
                    return $"{VramGB:F1} GB{suffix}";
                }
                return $"{EffectiveVramBytes / (1024.0 * 1024.0):F0} MB";
            }
        }

        /// <summary>
        /// Priority score for GPU selection (lower = better for ML workloads).
        /// </summary>
        public int PriorityScore { get; set; }

        /// <summary>
        /// Whether this is a discrete GPU (vs integrated).
        /// </summary>
        public bool IsDiscrete { get; set; }

        /// <summary>
        /// DXGI adapter index (0-based). Used to tell Whisper.net which GPU to use
        /// via WhisperFactoryOptions.GpuDevice, since Vulkan/CUDA may default to
        /// the integrated GPU (adapter 0) on multi-GPU systems.
        /// </summary>
        public int AdapterIndex { get; set; }

        public override string ToString() => $"{Name} ({VramDisplay})";
    }

    // =========================================================================
    // DXGI P/INVOKE DECLARATIONS
    // =========================================================================

    /// <summary>
    /// DXGI_ADAPTER_DESC structure containing GPU information.
    /// This provides accurate 64-bit memory values (unlike WMI's 32-bit AdapterRAM).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;    // SIZE_T - GPU's dedicated VRAM
        public nuint DedicatedSystemMemory;   // SIZE_T - System memory reserved for GPU
        public nuint SharedSystemMemory;      // SIZE_T - System memory available to GPU (for APUs)
        public long AdapterLuid;
    }

    // DXGI COM interfaces
    private static readonly Guid IID_IDXGIFactory = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");

    [ComImport]
    [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory
    {
        // IDXGIObject methods (vtable slots 3-6, after IUnknown)
        // Must be declared to maintain correct vtable offsets since
        // IDXGIFactory inherits IDXGIObject, not IUnknown directly.
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);

        // IDXGIFactory methods (vtable slot 7+)
        int EnumAdapters(uint Adapter, out IDXGIAdapter ppAdapter);
    }

    [ComImport]
    [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        // IDXGIObject methods (vtable slots 3-6, after IUnknown)
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);

        // IDXGIAdapter methods (vtable slot 7+)
        int EnumOutputs(uint Output, out IntPtr ppOutput);
        int GetDesc(out DXGI_ADAPTER_DESC pDesc);
    }

    [DllImport("dxgi.dll", SetLastError = true)]
    private static extern int CreateDXGIFactory(ref Guid riid, out IDXGIFactory ppFactory);

    // =========================================================================
    // CACHED GPU INFO
    // =========================================================================

    /// <summary>
    /// Cached GPU information (detected once at startup).
    /// Volatile so the lock-free fast path in GetBestGpu reads a fully published value.
    /// </summary>
    private static volatile GpuInfo? _cachedGpuInfo;

    /// <summary>
    /// Guards lazy initialization of <see cref="_cachedGpuInfo"/>.
    /// GetBestGpu is hit concurrently at startup (TranscriptionService, PlatformHelper,
    /// LocalLlmGpuHelper); without serialization, multiple threads enumerate DXGI in
    /// parallel which can throw intermittent COMExceptions and tear the cache.
    /// </summary>
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Gets information about the best GPU for ML workloads.
    ///
    /// CACHING:
    /// GPU info is cached after first detection since hardware doesn't change at runtime.
    /// Detection is serialized via _cacheLock (double-checked locking) so only one
    /// thread ever runs DXGI/WMI enumeration.
    /// </summary>
    /// <returns>GpuInfo for the best available GPU, or null if no GPU detected.</returns>
    public static GpuInfo? GetBestGpu()
    {
        if (_cachedGpuInfo != null)
        {
            return _cachedGpuInfo;
        }

        lock (_cacheLock)
        {
            if (_cachedGpuInfo != null)
            {
                return _cachedGpuInfo;
            }

            try
            {
                var gpus = DetectAllGpus();
                if (gpus.Count == 0)
                {
                    LoggingService.Warn("GpuInfoService: No GPUs detected");
                    return null;
                }

                // Select the GPU with the lowest priority score (best for ML)
                _cachedGpuInfo = gpus.OrderBy(g => g.PriorityScore).First();

                LoggingService.Info($"GpuInfoService: Best GPU detected - {_cachedGpuInfo.Name}");
                LoggingService.Info($"  Dedicated VRAM: {_cachedGpuInfo.DedicatedVramBytes / (1024.0 * 1024 * 1024):F1} GB");
                LoggingService.Info($"  Shared Memory: {_cachedGpuInfo.SharedMemoryBytes / (1024.0 * 1024 * 1024):F1} GB");
                LoggingService.Info($"  Effective VRAM: {_cachedGpuInfo.VramDisplay} ({_cachedGpuInfo.EffectiveVramBytes:N0} bytes)");
                LoggingService.Info($"  Is APU: {_cachedGpuInfo.IsAPU}");
                LoggingService.Info($"  Is Discrete: {_cachedGpuInfo.IsDiscrete}");
                LoggingService.Info($"  Adapter Index: {_cachedGpuInfo.AdapterIndex}");

                return _cachedGpuInfo;
            }
            catch (Exception ex)
            {
                LoggingService.Error("GpuInfoService: Failed to detect GPU", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Gets the VRAM of the best GPU in bytes.
    /// Returns 0 if no GPU is detected.
    /// </summary>
    public static long GetBestGpuVramBytes()
    {
        return GetBestGpu()?.VramBytes ?? 0;
    }

    /// <summary>
    /// Gets the VRAM of the best GPU in gigabytes.
    /// Returns 0 if no GPU is detected.
    /// </summary>
    public static double GetBestGpuVramGB()
    {
        return GetBestGpu()?.VramGB ?? 0;
    }

    /// <summary>
    /// Detects all GPUs in the system.
    /// Tries DXGI first (accurate 64-bit memory values), falls back to WMI.
    /// On ARM64, skips DXGI due to COM interop issues and goes straight to WMI.
    /// </summary>
    private static List<GpuInfo> DetectAllGpus()
    {
        // Skip DXGI on ARM64 - COM interop causes access violations
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        if (arch == System.Runtime.InteropServices.Architecture.Arm64)
        {
            LoggingService.Info("GpuInfoService: ARM64 detected, using WMI for GPU detection");
            return DetectGpusViaWmi();
        }

        // Try DXGI first - it provides accurate memory values including shared memory for APUs
        List<GpuInfo> gpus;
        try
        {
            gpus = DetectGpusViaDxgi();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"GpuInfoService: DXGI detection threw exception: {ex.Message}");
            gpus = new List<GpuInfo>();
        }

        if (gpus.Count > 0)
        {
            LoggingService.Info($"GpuInfoService: Detected {gpus.Count} GPU(s) via DXGI");
            return gpus;
        }

        // Fall back to WMI if DXGI fails
        LoggingService.Warn("GpuInfoService: DXGI detection failed, falling back to WMI");
        return DetectGpusViaWmi();
    }

    /// <summary>
    /// Detects all GPUs using DXGI (DirectX Graphics Infrastructure).
    /// This is the preferred method as it provides accurate 64-bit memory values
    /// and correctly reports shared memory for APUs.
    /// </summary>
    private static List<GpuInfo> DetectGpusViaDxgi()
    {
        var gpus = new List<GpuInfo>();

        LoggingService.Debug("GpuInfoService: Detecting GPUs via DXGI...");

        IDXGIFactory? factory = null;
        try
        {
            var iid = IID_IDXGIFactory;
            int hr = CreateDXGIFactory(ref iid, out factory);
            if (hr != 0 || factory == null)
            {
                LoggingService.Warn($"GpuInfoService: CreateDXGIFactory failed with HRESULT 0x{hr:X8}");
                return gpus;
            }

            uint adapterIndex = 0;
            while (true)
            {
                IDXGIAdapter? adapter = null;
                try
                {
                    hr = factory.EnumAdapters(adapterIndex, out adapter);
                    if (hr != 0 || adapter == null)
                    {
                        // DXGI_ERROR_NOT_FOUND (0x887A0002) means no more adapters
                        break;
                    }

                    hr = adapter.GetDesc(out var desc);
                    if (hr != 0)
                    {
                        LoggingService.Warn($"GpuInfoService: GetDesc failed for adapter {adapterIndex}");
                        adapterIndex++;
                        continue;
                    }

                    // Skip software adapters (Microsoft Basic Render Driver)
                    if (desc.Description.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                        desc.Description.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                    {
                        LoggingService.Debug($"  Skipping software adapter: {desc.Description}");
                        adapterIndex++;
                        continue;
                    }

                    var gpu = new GpuInfo
                    {
                        Name = desc.Description,
                        DedicatedVramBytes = (long)(ulong)desc.DedicatedVideoMemory,
                        SharedMemoryBytes = (long)(ulong)desc.SharedSystemMemory,
                        PriorityScore = GetGpuPriorityScore(desc.Description),
                        IsDiscrete = IsDiscreteGpu(desc.Description),
                        AdapterIndex = (int)adapterIndex
                    };

                    LoggingService.Debug($"  Found GPU via DXGI: {gpu.Name}");
                    LoggingService.Debug($"    Dedicated VRAM: {gpu.DedicatedVramBytes / (1024.0 * 1024 * 1024):F2} GB");
                    LoggingService.Debug($"    Shared Memory: {gpu.SharedMemoryBytes / (1024.0 * 1024 * 1024):F2} GB");
                    LoggingService.Debug($"    Effective VRAM: {gpu.EffectiveVramBytes / (1024.0 * 1024 * 1024):F2} GB");
                    LoggingService.Debug($"    Is APU: {gpu.IsAPU}");
                    LoggingService.Debug($"    Priority: {gpu.PriorityScore}");
                    LoggingService.Debug($"    Discrete: {gpu.IsDiscrete}");

                    gpus.Add(gpu);
                    adapterIndex++;
                }
                finally
                {
                    if (adapter != null)
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
        }
        catch (COMException comEx) when (comEx.HResult == unchecked((int)0x887A0002))
        {
            // DXGI_ERROR_NOT_FOUND — normal termination of adapter enumeration
            // (COM can throw instead of returning the HRESULT depending on interop)
        }
        catch (Exception ex)
        {
            LoggingService.Error("GpuInfoService: DXGI query failed", ex);
        }
        finally
        {
            if (factory != null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }

        return gpus;
    }

    /// <summary>
    /// Detects all GPUs using WMI (Windows Management Instrumentation).
    /// This is the fallback method when DXGI is not available.
    ///
    /// LIMITATION:
    /// AdapterRAM is a 32-bit uint, capped at ~4GB.
    /// For GPUs with more VRAM, we use heuristics based on the GPU name.
    /// This method cannot detect shared memory for APUs.
    /// </summary>
    private static List<GpuInfo> DetectGpusViaWmi()
    {
        var gpus = new List<GpuInfo>();

        LoggingService.Debug("GpuInfoService: Detecting GPUs via WMI (fallback)...");

        try
        {
            // Query WMI for video controllers
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                var name = obj["Name"]?.ToString() ?? "Unknown GPU";

                // Skip software adapters
                if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                {
                    LoggingService.Debug($"  Skipping software adapter: {name}");
                    continue;
                }

                // AdapterRAM is a uint32 in WMI, so it maxes out at ~4GB
                long vramBytes = 0;
                var adapterRamObj = obj["AdapterRAM"];
                if (adapterRamObj != null)
                {
                    vramBytes = Convert.ToInt64(adapterRamObj);
                }

                // Check if VRAM is capped at 4GB (WMI limitation)
                // If so, try to estimate actual VRAM from GPU name
                if (vramBytes >= 4_000_000_000 && vramBytes <= 4_294_967_295)
                {
                    var estimatedVram = EstimateVramFromGpuName(name);
                    if (estimatedVram > vramBytes)
                    {
                        LoggingService.Debug($"  {name}: WMI reports {vramBytes:N0} bytes, estimated {estimatedVram:N0} bytes");
                        vramBytes = estimatedVram;
                    }
                }

                var gpu = new GpuInfo
                {
                    Name = name,
                    DedicatedVramBytes = vramBytes,
                    SharedMemoryBytes = 0,  // WMI cannot detect shared memory
                    PriorityScore = GetGpuPriorityScore(name),
                    IsDiscrete = IsDiscreteGpu(name)
                };

                LoggingService.Debug($"  Found GPU via WMI: {gpu.Name}");
                LoggingService.Debug($"    VRAM: {gpu.VramDisplay}");
                LoggingService.Debug($"    Priority: {gpu.PriorityScore}");
                LoggingService.Debug($"    Discrete: {gpu.IsDiscrete}");

                gpus.Add(gpu);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("GpuInfoService: WMI query failed", ex);
        }

        return gpus;
    }

    /// <summary>
    /// Estimates VRAM from GPU name when WMI reports incorrect values.
    ///
    /// WMI LIMITATION:
    /// Win32_VideoController.AdapterRAM is a 32-bit uint, which means it maxes out
    /// at 4,294,967,295 bytes (~4GB). For GPUs with more VRAM, we estimate based
    /// on known GPU specifications.
    ///
    /// COMMON NVIDIA GPUS:
    /// - RTX 4090: 24 GB
    /// - RTX 4080: 16 GB
    /// - RTX 4070 Ti: 12 GB
    /// - RTX 4070: 12 GB
    /// - RTX 4060 Ti: 8 GB or 16 GB
    /// - RTX 4060: 8 GB
    /// - RTX 3090: 24 GB
    /// - RTX 3080: 10 GB or 12 GB
    /// - RTX 3070: 8 GB
    /// - RTX 3060: 12 GB
    ///
    /// CONSERVATIVE APPROACH:
    /// When multiple VRAM variants exist (e.g., RTX 4060 Ti 8GB/16GB),
    /// we use the lower value to avoid overestimating.
    /// </summary>
    private static long EstimateVramFromGpuName(string name)
    {
        var upperName = name.ToUpperInvariant();

        // RTX 40 series
        if (upperName.Contains("4090")) return 24L * 1024 * 1024 * 1024;
        if (upperName.Contains("4080")) return 16L * 1024 * 1024 * 1024;
        if (upperName.Contains("4070 TI") || upperName.Contains("4070TI")) return 12L * 1024 * 1024 * 1024;
        if (upperName.Contains("4070")) return 12L * 1024 * 1024 * 1024;
        if (upperName.Contains("4060 TI") || upperName.Contains("4060TI")) return 8L * 1024 * 1024 * 1024; // Conservative (also comes in 16GB)
        if (upperName.Contains("4060")) return 8L * 1024 * 1024 * 1024;

        // RTX 30 series
        if (upperName.Contains("3090")) return 24L * 1024 * 1024 * 1024;
        if (upperName.Contains("3080 TI") || upperName.Contains("3080TI")) return 12L * 1024 * 1024 * 1024;
        if (upperName.Contains("3080")) return 10L * 1024 * 1024 * 1024;
        if (upperName.Contains("3070 TI") || upperName.Contains("3070TI")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("3070")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("3060 TI") || upperName.Contains("3060TI")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("3060")) return 12L * 1024 * 1024 * 1024;
        if (upperName.Contains("3050")) return 8L * 1024 * 1024 * 1024;

        // RTX 20 series
        if (upperName.Contains("2080 TI") || upperName.Contains("2080TI")) return 11L * 1024 * 1024 * 1024;
        if (upperName.Contains("2080 SUPER") || upperName.Contains("2080S")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("2080")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("2070 SUPER") || upperName.Contains("2070S")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("2070")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("2060 SUPER") || upperName.Contains("2060S")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("2060")) return 6L * 1024 * 1024 * 1024;

        // GTX 16 series
        if (upperName.Contains("1660")) return 6L * 1024 * 1024 * 1024;
        if (upperName.Contains("1650")) return 4L * 1024 * 1024 * 1024;

        // GTX 10 series
        if (upperName.Contains("1080 TI") || upperName.Contains("1080TI")) return 11L * 1024 * 1024 * 1024;
        if (upperName.Contains("1080")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("1070 TI") || upperName.Contains("1070TI")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("1070")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("1060")) return 6L * 1024 * 1024 * 1024;

        // AMD RX 7000 series
        if (upperName.Contains("7900 XTX") || upperName.Contains("7900XTX")) return 24L * 1024 * 1024 * 1024;
        if (upperName.Contains("7900 XT") || upperName.Contains("7900XT")) return 20L * 1024 * 1024 * 1024;
        if (upperName.Contains("7800 XT") || upperName.Contains("7800XT")) return 16L * 1024 * 1024 * 1024;
        if (upperName.Contains("7700 XT") || upperName.Contains("7700XT")) return 12L * 1024 * 1024 * 1024;
        if (upperName.Contains("7600")) return 8L * 1024 * 1024 * 1024;

        // AMD RX 6000 series
        if (upperName.Contains("6900 XT") || upperName.Contains("6900XT")) return 16L * 1024 * 1024 * 1024;
        if (upperName.Contains("6800 XT") || upperName.Contains("6800XT")) return 16L * 1024 * 1024 * 1024;
        if (upperName.Contains("6800")) return 16L * 1024 * 1024 * 1024;
        if (upperName.Contains("6700 XT") || upperName.Contains("6700XT")) return 12L * 1024 * 1024 * 1024;
        if (upperName.Contains("6600 XT") || upperName.Contains("6600XT")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("6600")) return 8L * 1024 * 1024 * 1024;

        // Intel Arc
        if (upperName.Contains("A770")) return 16L * 1024 * 1024 * 1024;
        if (upperName.Contains("A750")) return 8L * 1024 * 1024 * 1024;
        if (upperName.Contains("A380")) return 6L * 1024 * 1024 * 1024;

        // If we can't determine, return 0 (will use WMI value)
        return 0;
    }

    /// <summary>
    /// Gets a priority score for a GPU based on its name.
    /// Lower score = higher priority (better for ML workloads).
    ///
    /// PRIORITY ORDER:
    /// 1. NVIDIA GPUs (best CUDA/DirectCompute performance for ML)
    /// 2. AMD discrete GPUs (good discrete GPU option)
    /// 3. Intel Arc (discrete Intel GPUs)
    /// 4. Other/Unknown GPUs
    /// 5. Intel Iris (integrated, better than UHD)
    /// 6. Intel UHD/HD (integrated, slowest)
    /// </summary>
    private static int GetGpuPriorityScore(string gpuName)
    {
        var name = gpuName.ToUpperInvariant();

        // Priority 1: NVIDIA GPUs
        if (name.Contains("NVIDIA") || name.Contains("GEFORCE") ||
            name.Contains("RTX") || name.Contains("GTX") ||
            name.Contains("QUADRO") || name.Contains("TESLA") ||
            name.Contains("TITAN"))
        {
            return 1;
        }

        // Priority 2: AMD discrete GPUs
        if (name.Contains("RADEON") || name.Contains("AMD"))
        {
            if (IsAmdIntegratedGraphicsName(name))
            {
                return 4;
            }

            if (name.Contains(" RX") || name.Contains("PRO ") || name.Contains("VEGA"))
            {
                return 2;
            }
            // AMD integrated graphics (APU)
            return 4;
        }

        // Priority 3: Intel Arc discrete GPUs
        if (IsIntelHighPriorityIntegratedGraphicsName(name))
        {
            return 5;
        }

        if (name.Contains("ARC"))
        {
            return 3;
        }

        // Priority 5-6: Intel integrated graphics
        if (name.Contains("INTEL"))
        {
            if (name.Contains("IRIS"))
            {
                return 5;
            }
            return 6;
        }

        // Skip Microsoft Basic Render Driver (software rendering)
        if (name.Contains("MICROSOFT") || name.Contains("BASIC"))
        {
            return 100;
        }

        // Priority 4: Unknown/Other GPUs
        return 4;
    }

    /// <summary>
    /// Determines if a GPU is discrete (vs integrated).
    /// </summary>
    private static bool IsDiscreteGpu(string gpuName)
    {
        var name = gpuName.ToUpperInvariant();

        // NVIDIA discrete GPUs
        if (name.Contains("GEFORCE") || name.Contains("RTX") || name.Contains("GTX") ||
            name.Contains("QUADRO") || name.Contains("TESLA") || name.Contains("TITAN"))
        {
            return true;
        }

        // AMD APU integrated GPUs (for example, Ryzen Radeon Vega Graphics).
        // Check these before the AMD discrete Vega heuristic below.
        if (IsAmdIntegratedGraphicsName(name))
        {
            return false;
        }

        // Intel integrated GPUs can be branded as Arc on Core Ultra systems.
        // Check before the generic Arc discrete heuristic.
        if (IsIntelIntegratedGraphicsName(name))
        {
            return false;
        }

        // AMD discrete GPUs
        if (name.Contains("RADEON") && (name.Contains(" RX") || name.Contains("PRO ") || name.Contains("VEGA")))
        {
            return true;
        }

        // Intel Arc discrete GPUs
        if (name.Contains("ARC"))
        {
            return true;
        }

        // Intel integrated GPUs
        if (name.Contains("INTEL") && (name.Contains("UHD") || name.Contains("IRIS") || name.Contains("HD GRAPHICS")))
        {
            return false;
        }

        // Default to discrete if unknown
        return true;
    }

    private static bool IsAmdIntegratedGraphicsName(string uppercaseGpuName)
    {
        return (uppercaseGpuName.Contains("AMD") || uppercaseGpuName.Contains("RADEON"))
               && uppercaseGpuName.Contains("GRAPHICS")
               && !IsAmdRxVegaMDiscreteGraphicsName(uppercaseGpuName)
               && ((!uppercaseGpuName.Contains(" RX") && !uppercaseGpuName.Contains("PRO "))
                   || IsAmdRxVegaIntegratedGraphicsName(uppercaseGpuName));
    }

    private static bool IsAmdRxVegaMDiscreteGraphicsName(string uppercaseGpuName)
    {
        return uppercaseGpuName.Contains(" RX VEGA M ");
    }

    private static bool IsAmdRxVegaIntegratedGraphicsName(string uppercaseGpuName)
    {
        return uppercaseGpuName.Contains(" RX VEGA ") &&
               (uppercaseGpuName.Contains(" VEGA 3 ") ||
                uppercaseGpuName.Contains(" VEGA 5 ") ||
                uppercaseGpuName.Contains(" VEGA 6 ") ||
                uppercaseGpuName.Contains(" VEGA 7 ") ||
                uppercaseGpuName.Contains(" VEGA 8 ") ||
                uppercaseGpuName.Contains(" VEGA 10 ") ||
                uppercaseGpuName.Contains(" VEGA 11 "));
    }

    private static bool IsIntelIntegratedGraphicsName(string uppercaseGpuName)
    {
        if (!uppercaseGpuName.Contains("INTEL"))
        {
            return false;
        }

        if (uppercaseGpuName.Contains("UHD") ||
            uppercaseGpuName.Contains("IRIS") ||
            uppercaseGpuName.Contains("HD GRAPHICS"))
        {
            return true;
        }

        if (IsIntelIntegratedVSeriesName(uppercaseGpuName))
        {
            return true;
        }

        return uppercaseGpuName.Contains("ARC") &&
               !IsIntelArcDiscreteModelName(uppercaseGpuName) &&
               uppercaseGpuName.Contains("GRAPHICS");
    }

    private static bool IsIntelHighPriorityIntegratedGraphicsName(string uppercaseGpuName)
    {
        return uppercaseGpuName.Contains("INTEL") &&
               (IsIntelIntegratedVSeriesName(uppercaseGpuName) ||
                (uppercaseGpuName.Contains("ARC") &&
                 uppercaseGpuName.Contains("GRAPHICS") &&
                 !IsIntelArcDiscreteModelName(uppercaseGpuName)));
    }

    private static bool IsIntelArcDiscreteModelName(string uppercaseGpuName)
    {
        return uppercaseGpuName.Contains(" A3") ||
               uppercaseGpuName.Contains(" A5") ||
               uppercaseGpuName.Contains(" A7") ||
               uppercaseGpuName.Contains(" B5") ||
               uppercaseGpuName.Contains(" B6") ||
               uppercaseGpuName.Contains(" B7") ||
               uppercaseGpuName.Contains("PRO A") ||
               uppercaseGpuName.Contains("PRO B");
    }

    private static bool IsIntelIntegratedVSeriesName(string uppercaseGpuName)
    {
        return uppercaseGpuName.Contains(" 130V") ||
               uppercaseGpuName.Contains(" 140V");
    }

    /// <summary>
    /// Clears the cached GPU information.
    /// Call this if you need to re-detect GPUs (e.g., after driver update).
    /// </summary>
    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedGpuInfo = null;
        }
        LoggingService.Debug("GpuInfoService: Cache cleared");
    }
}
