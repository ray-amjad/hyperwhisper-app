using System.Text;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.SystemIntegration;

public sealed record LinuxGpuDetectionCapabilities(bool VulkanToolAvailable, bool CudaToolAvailable,
    bool SoftwareRendererDetected, string Evidence);

public sealed class LinuxGpuInfoProvider : IGpuInfoProvider
{
    private readonly IDesktopCommandRunner _runner;
    private readonly string? _vulkanInfo;
    private readonly string? _nvidiaSmi;
    private GpuInfo? _cache;
    private bool _cached;
    private LinuxGpuDetectionCapabilities _capabilities;
    public LinuxGpuInfoProvider() : this(new DesktopCommandRunner(), CommandClipboardBackend.FindExecutable("vulkaninfo"),
        CommandClipboardBackend.FindExecutable("nvidia-smi")) { }
    internal LinuxGpuInfoProvider(IDesktopCommandRunner runner, string? vulkanInfo, string? nvidiaSmi)
    { _runner = runner; _vulkanInfo = vulkanInfo; _nvidiaSmi = nvidiaSmi; _capabilities = new(vulkanInfo is not null, nvidiaSmi is not null, false, "not-probed"); }
    public LinuxGpuDetectionCapabilities GetCapabilities() => _capabilities;
    public PlatformResult<GpuInfo?> GetBestGpu()
    {
        if (_cached) return PlatformResult<GpuInfo?>.Success(_cache);
        try
        {
            var cuda = ProbeCuda();
            if (cuda is not null) return Cache(cuda, false, "nvidia-smi-hardware");
            var vulkan = ProbeVulkan(out var software);
            return Cache(software ? null : vulkan, software, software ? "vulkan-software-renderer" : vulkan is null ? "no-hardware-evidence" : "vulkaninfo-hardware");
        }
        catch { return PlatformResult<GpuInfo?>.Failure("gpu.detection_failed", "Linux GPU detection failed."); }
    }
    private PlatformResult<GpuInfo?> Cache(GpuInfo? gpu, bool software, string evidence)
    { _cache = gpu; _cached = true; _capabilities = new(_vulkanInfo is not null, _nvidiaSmi is not null, software, evidence); return PlatformResult<GpuInfo?>.Success(gpu); }
    private GpuInfo? ProbeCuda()
    {
        if (_nvidiaSmi is null) return null;
        var result = _runner.RunAsync(_nvidiaSmi, ["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"], null,
            CancellationToken.None, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (result.ExitCode != 0) return null;
        var first = Encoding.UTF8.GetString(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (first is null) return null;
        var comma = first.LastIndexOf(',');
        if (comma < 1 || !long.TryParse(first[(comma + 1)..].Trim(), out var mib)) return null;
        return new GpuInfo { Name = first[..comma].Trim(), DedicatedMemoryBytes = mib * 1024 * 1024,
            IsDiscrete = true, SupportsCuda = true, SupportsVulkan = false };
    }
    private GpuInfo? ProbeVulkan(out bool software)
    {
        software = false;
        if (_vulkanInfo is null) return null;
        var result = _runner.RunAsync(_vulkanInfo, ["--summary"], null, CancellationToken.None,
            TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        if (result.ExitCode != 0) return null;
        var text = Encoding.UTF8.GetString(result.Output);
        var name = ValueAfter(text, "deviceName") ?? "Vulkan device";
        software = new[] { "llvmpipe", "lavapipe", "swiftshader", "software rasterizer" }
            .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (software) return null;
        var type = ValueAfter(text, "deviceType") ?? string.Empty;
        var discrete = type.Contains("DISCRETE", StringComparison.OrdinalIgnoreCase);
        return new GpuInfo { Name = name, IsDiscrete = discrete, SupportsVulkan = true,
            DedicatedMemoryBytes = ReadLargestVram(), SharedMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes };
    }
    private static string? ValueAfter(string text, string key)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
            if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is var index && index >= 0) return line[(index + 1)..].Trim();
        return null;
    }
    private static long ReadLargestVram()
    {
        try { return Directory.EnumerateFiles("/sys/class/drm", "mem_info_vram_total", SearchOption.AllDirectories)
            .Select(path => long.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0).DefaultIfEmpty().Max(); }
        catch { return 0; }
    }
    public void ClearCache() { _cache = null; _cached = false; _capabilities = _capabilities with { SoftwareRendererDetected = false, Evidence = "not-probed" }; }
}
