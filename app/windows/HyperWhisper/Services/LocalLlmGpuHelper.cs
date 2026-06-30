using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// Local LLM-specific GPU guidance.
/// This stays separate from Whisper runtime detection because LLamaSharp only
/// uses CUDA in this build, while Whisper may use CUDA, Vulkan, or CPU.
/// </summary>
public static class LocalLlmGpuHelper
{
    public sealed record RuntimePlan(
        GpuInfoService.GpuInfo? Gpu,
        bool WillTryCuda,
        int GpuLayerCount,
        string BackendSummary,
        bool SharesGpuWithWhisper);

    public static RuntimePlan GetRuntimePlan()
    {
        var gpu = GpuInfoService.GetBestGpu();
        if (gpu == null)
        {
            return new RuntimePlan(
                Gpu: null,
                WillTryCuda: false,
                GpuLayerCount: 0,
                BackendSummary: "CPU fallback",
                SharesGpuWithWhisper: false);
        }

        var willTryCuda = gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            && PlatformHelper.HasLocalLlmCudaRuntime;
        return new RuntimePlan(
            Gpu: gpu,
            WillTryCuda: willTryCuda,
            GpuLayerCount: willTryCuda ? 99 : 0,
            BackendSummary: willTryCuda ? "CUDA first, CPU fallback" : "CPU fallback",
            SharesGpuWithWhisper: willTryCuda);
    }
}
