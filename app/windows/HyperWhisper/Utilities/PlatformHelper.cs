using System.IO;
using System.Runtime.InteropServices;
using HyperWhisper.Services;

namespace HyperWhisper.Utilities;

/// <summary>
/// Provides platform and architecture detection utilities.
/// Used to determine feature availability based on CPU architecture and GPU.
/// </summary>
public static class PlatformHelper
{
    /// <summary>
    /// Returns true if the OS is running on ARM64 architecture.
    /// Uses OSArchitecture (not ProcessArchitecture) so we detect ARM64 even when
    /// running an x64 build via Windows x64 emulation.
    /// </summary>
    public static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// Returns true if the OS is running on x64 architecture.
    /// </summary>
    public static bool IsX64 => RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>
    /// Returns true if Whisper.net local transcription is supported.
    /// Whisper.net 1.9.x ships Windows ARM64 CPU runtimes, but its native
    /// Windows runtime requires Windows 11 / Server 2022 or newer. Keep x64
    /// Windows 10 supported, but only enable ARM64 on Windows 11-era builds.
    /// </summary>
    public static bool SupportsWhisperTranscription => IsX64 || (IsArm64 && IsWindows11OrNewer);

    /// <summary>
    /// Returns true if Parakeet-family sherpa-onnx local transcription is supported.
    /// x64 builds always package the daemon. ARM64 builds become available when
    /// the native ARM64 daemon payload is present in the app directory.
    /// </summary>
    public static bool SupportsParakeetTranscription => IsX64 || (IsArm64 && HasNativeArm64ParakeetDaemon);

    /// <summary>
    /// Returns true if any local transcription engine is supported on the current platform.
    /// </summary>
    public static bool SupportsLocalTranscription => SupportsWhisperTranscription || SupportsParakeetTranscription;

    /// <summary>
    /// Returns true if LLamaSharp local post-processing is supported.
    /// LLamaSharp 0.27.0 packages a CPU backend for Windows ARM64; GPU backends
    /// remain x64-only in HyperWhisper's current Windows setup.
    /// </summary>
    public static bool SupportsLocalLlmPostProcessing => HasLocalLlmCpuRuntime;

    /// <summary>
    /// Returns true if GPU-accelerated transcription is supported.
    /// Requires x64 architecture with a discrete GPU.
    /// Supports NVIDIA (CUDA), AMD (Vulkan), and Intel Arc (Vulkan).
    /// </summary>
    public static bool SupportsGpuTranscription => IsX64 && HasDiscreteGpu;

    /// <summary>
    /// Returns the current process architecture as a string (e.g., "X64", "Arm64").
    /// </summary>
    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture.ToString();

    public static bool IsWindows11OrNewer => Environment.OSVersion.Version.Build >= 22000;

    public static bool HasLocalLlmCudaRuntime =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
        && File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native",
            "cuda12",
            "llama.dll"));

    /// <summary>
    /// Cached discrete GPU detection result.
    /// </summary>
    private static bool? _hasDiscreteGpu;

    /// <summary>
    /// Detects if a discrete GPU capable of accelerating transcription is present.
    /// With Vulkan runtime, this includes NVIDIA, AMD, and Intel Arc GPUs.
    /// Result is cached for performance since hardware doesn't change at runtime.
    /// </summary>
    private static bool HasDiscreteGpu
    {
        get
        {
            if (_hasDiscreteGpu == null)
            {
                var gpu = GpuInfoService.GetBestGpu();
                if (gpu == null)
                {
                    _hasDiscreteGpu = false;
                }
                else
                {
                    var name = gpu.Name.ToUpperInvariant();
                    _hasDiscreteGpu = name.Contains("NVIDIA") || name.Contains("GEFORCE") ||
                                      name.Contains("RTX") || name.Contains("GTX") ||
                                      name.Contains("RADEON") || name.Contains("AMD") ||
                                      name.Contains("ARC");
                }
            }
            return _hasDiscreteGpu.Value;
        }
    }

    private static bool HasNativeArm64ParakeetDaemon
    {
        get
        {
            try
            {
                var engineDir = Path.Combine(AppContext.BaseDirectory, "parakeet-engine");
                var daemonPath = Path.Combine(engineDir, "parakeet-engine.exe");
                var sherpaPath = Path.Combine(engineDir, "sherpa-onnx-c-api.dll");
                var onnxRuntimePath = Path.Combine(engineDir, "onnxruntime.dll");
                var vadPath = Path.Combine(engineDir, "silero_vad.onnx");

                return File.Exists(vadPath)
                    && IsArm64Pe(daemonPath)
                    && IsArm64Pe(sherpaPath)
                    && IsArm64Pe(onnxRuntimePath);
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool HasLocalLlmCpuRuntime
    {
        get
        {
            var runtimeId = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null
            };
            if (runtimeId == null) return false;

            var nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeId, "native");
            return HasLlamaSharpCpuRuntime(nativeDir);
        }
    }

    private static bool HasLlamaSharpCpuRuntime(string nativeDir)
    {
        if (HasLlamaSharpCpuRuntimeFiles(nativeDir)) return true;

        foreach (var variant in new[] { "avx2", "avx", "noavx" })
        {
            if (HasLlamaSharpCpuRuntimeFiles(Path.Combine(nativeDir, variant))) return true;
        }

        return false;
    }

    private static bool HasLlamaSharpCpuRuntimeFiles(string directory) =>
        File.Exists(Path.Combine(directory, "llama.dll"))
        && File.Exists(Path.Combine(directory, "ggml.dll"))
        && File.Exists(Path.Combine(directory, "ggml-base.dll"))
        && File.Exists(Path.Combine(directory, "ggml-cpu.dll"));

    private static bool IsArm64Pe(string path)
    {
        if (!File.Exists(path)) return false;

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 0x40) return false;
        stream.Position = 0x3C;
        var peHeaderOffset = reader.ReadInt32();
        if (peHeaderOffset <= 0 || peHeaderOffset + 6 > stream.Length) return false;

        stream.Position = peHeaderOffset;
        if (reader.ReadUInt32() != 0x00004550) return false; // "PE\0\0"

        const ushort imageFileMachineArm64 = 0xAA64;
        return reader.ReadUInt16() == imageFileMachineArm64;
    }
}
