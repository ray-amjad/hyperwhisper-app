using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// Which LLamaSharp native backend a local-LLM load will attempt.
/// </summary>
public enum LocalLlmBackend
{
    /// <summary>No GPU offload — CPU backend only.</summary>
    None,
    Cuda,
    Vulkan
}

/// <summary>
/// Local LLM-specific GPU guidance.
/// This stays separate from Whisper runtime detection because the two stacks
/// pick backends differently: Whisper.net is forced onto vendor-neutral Vulkan
/// for every GPU, while LLamaSharp prefers CUDA on NVIDIA hardware and falls
/// back to Vulkan for other ML-class discrete adapters (AMD RX/PRO/VEGA,
/// Intel Arc). HyperWhisper decides Vulkan viability itself (discrete GPU +
/// system Vulkan loader + shipped runtime files) and pins the native library
/// explicitly, instead of relying on LLamaSharp's own Vulkan detection —
/// which silently requires the Vulkan SDK's vulkaninfo tool that end users
/// don't have installed.
/// </summary>
public static class LocalLlmGpuHelper
{
    /// <summary>Crash-store identifier for the CUDA backend.</summary>
    public const string CudaBackendId = "cuda";

    /// <summary>Crash-store identifier for the Vulkan backend.</summary>
    public const string VulkanBackendId = "vulkan";

    public sealed record RuntimePlan(
        GpuInfoService.GpuInfo? Gpu,
        LocalLlmBackend Backend,
        int GpuLayerCount,
        string BackendSummary,
        bool SharesGpuWithWhisper);

    /// <summary>
    /// Computes the backend plan for this machine. With a model path the plan
    /// also honours that model's per-backend crash pins, so callers rendering
    /// per-model state (Model Library badges) never claim an offload the load
    /// path would refuse.
    /// </summary>
    public static RuntimePlan GetRuntimePlan(string? modelPath = null)
        => GetRuntimePlan(modelPath, restrictToBackend: null);

    /// <summary>
    /// Plan computation with an optional backend restriction. LLamaSharp's
    /// native library choice is process-wide and one-shot, so once a library
    /// has loaded, later loads must re-plan against only that backend
    /// (<see cref="LocalLlmService"/> passes the frozen process backend here).
    /// </summary>
    internal static RuntimePlan GetRuntimePlan(string? modelPath, LocalLlmBackend? restrictToBackend)
    {
        var gpu = GpuInfoService.GetBestGpu();
        if (gpu == null)
        {
            return CpuPlan(null);
        }

        var cudaPinned = modelPath != null
            && LocalLlmService.GpuLoadCrashedPreviously(modelPath, gpu.Name, CudaBackendId);
        var vulkanPinned = modelPath != null
            && LocalLlmService.GpuLoadCrashedPreviously(modelPath, gpu.Name, VulkanBackendId);

        var backend = DecideBackend(
            gpu.Name,
            gpu.IsDiscreteForMl,
            hasCudaRuntime: PlatformHelper.HasLocalLlmCudaRuntime
                && restrictToBackend is null or LocalLlmBackend.Cuda,
            hasVulkanRuntime: PlatformHelper.HasLocalLlmVulkanRuntime
                && restrictToBackend is null or LocalLlmBackend.Vulkan,
            hasSystemVulkanLoader: PlatformHelper.HasSystemVulkanLoader,
            cudaPinned: cudaPinned,
            vulkanPinned: vulkanPinned);

        return backend switch
        {
            LocalLlmBackend.Cuda => new RuntimePlan(
                Gpu: gpu,
                Backend: backend,
                GpuLayerCount: 99,
                BackendSummary: "CUDA first, CPU fallback",
                SharesGpuWithWhisper: true),
            LocalLlmBackend.Vulkan => new RuntimePlan(
                Gpu: gpu,
                Backend: backend,
                GpuLayerCount: 99,
                BackendSummary: "Vulkan first, CPU fallback",
                SharesGpuWithWhisper: true),
            _ => CpuPlan(gpu)
        };
    }

    internal static RuntimePlan CpuPlan(GpuInfoService.GpuInfo? gpu) => new(
        Gpu: gpu,
        Backend: LocalLlmBackend.None,
        GpuLayerCount: 0,
        BackendSummary: "CPU fallback",
        SharesGpuWithWhisper: false);

    /// <summary>
    /// Pure backend decision so SmokeTests can cover the eligibility matrix
    /// without hardware. NVIDIA prefers CUDA and falls through to Vulkan when
    /// the CUDA runtime files are absent or the model is CUDA-crash-pinned;
    /// other ML-class discrete adapters go straight to Vulkan; integrated
    /// GPUs, APUs, and unrecognised adapters stay on CPU (the safe direction
    /// for unknown hardware).
    /// </summary>
    internal static LocalLlmBackend DecideBackend(
        string gpuName,
        bool isDiscreteForMl,
        bool hasCudaRuntime,
        bool hasVulkanRuntime,
        bool hasSystemVulkanLoader,
        bool cudaPinned,
        bool vulkanPinned)
    {
        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            && hasCudaRuntime
            && !cudaPinned)
        {
            return LocalLlmBackend.Cuda;
        }

        if (isDiscreteForMl
            && hasVulkanRuntime
            && hasSystemVulkanLoader
            && !vulkanPinned)
        {
            return LocalLlmBackend.Vulkan;
        }

        return LocalLlmBackend.None;
    }

    /// <summary>Crash-store id for a backend; null for the CPU plan.</summary>
    internal static string? BackendId(LocalLlmBackend backend) => backend switch
    {
        LocalLlmBackend.Cuda => CudaBackendId,
        LocalLlmBackend.Vulkan => VulkanBackendId,
        _ => null
    };

    internal static string BackendDisplayName(LocalLlmBackend backend) => backend switch
    {
        LocalLlmBackend.Cuda => "CUDA",
        LocalLlmBackend.Vulkan => "Vulkan",
        _ => "CPU"
    };

    /// <summary>
    /// Display name with the "(Recommended)" suffix hidden on machines whose
    /// local-LLM plan is CPU-only — the recommendation implies an offload this
    /// machine cannot deliver. <see cref="LocalLlmModelInfo.GetDefault"/> still
    /// returns the recommended model everywhere; only the label changes.
    /// </summary>
    public static string DisplayNameFor(LocalLlmModelInfo model)
    {
        if (!model.IsRecommended)
        {
            return model.DisplayName;
        }

        return GetRuntimePlan().Backend == LocalLlmBackend.None
            ? model.DisplayName.Replace(" (Recommended)", "", StringComparison.Ordinal)
            : model.DisplayName;
    }
}
