using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace HyperWhisper.LocalPostProcessing;

/// <summary>Checks the LLamaSharp 0.27 native assets shipped beside the app.</summary>
public static class PackagedLlamaRuntime
{
    public static bool IsAvailable(LocalLlmBackend backend, string? baseDirectory = null)
    {
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            var native = Path.Combine(root, "runtimes", "linux-x64", "native");
            return backend switch
            {
                LocalLlmBackend.Cpu => File.Exists(Path.Combine(
                    native, PreferredX64IsaDirectory(), "libllama.so")),
                LocalLlmBackend.Vulkan =>
                    HasGpuFiles(native, "vulkan", "libggml-vulkan.so", "libggml-cpu.so")
                    && CanLoadSystemLibrary("libvulkan.so.1"),
                LocalLlmBackend.Cuda =>
                    HasGpuFiles(native, "cuda12", "libggml-cuda.so", "libggml-cpu.so")
                    && CanLoadSystemLibrary("libcuda.so.1")
                    && CanLoadSystemLibrary("libcudart.so.12")
                    && CanLoadSystemLibrary("libcublas.so.12"),
                _ => false,
            };
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return backend == LocalLlmBackend.Cpu
                && File.Exists(Path.Combine(
                    root, "runtimes", "linux-arm64", "native", "libllama.so"));
        }

        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            var native = Path.Combine(root, "runtimes", "win-x64", "native");
            return backend switch
            {
                LocalLlmBackend.Cpu => File.Exists(Path.Combine(
                    native, PreferredX64IsaDirectory(), "llama.dll")),
                LocalLlmBackend.Vulkan =>
                    HasGpuFiles(native, "vulkan", "ggml-vulkan.dll", "ggml-cpu.dll")
                    && CanLoadSystemLibrary("vulkan-1.dll"),
                LocalLlmBackend.Cuda =>
                    HasGpuFiles(native, "cuda12", "ggml-cuda.dll", "ggml-cpu.dll")
                    && CanLoadSystemLibrary("nvcuda.dll")
                    && CanLoadSystemLibrary("cudart64_12.dll")
                    && CanLoadSystemLibrary("cublas64_12.dll"),
                _ => false,
            };
        }

        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return backend == LocalLlmBackend.Cpu
                && File.Exists(Path.Combine(
                    root, "runtimes", "win-arm64", "native", "llama.dll"));
        }

        if (OperatingSystem.IsMacOS())
        {
            var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
            return backend == LocalLlmBackend.Cpu
                && File.Exists(Path.Combine(root, "runtimes", runtime, "native", "libllama.dylib"));
        }
        return false;
    }

    public static string PreferredX64IsaDirectory()
    {
        if (!X86Base.IsSupported)
        {
            return "noavx";
        }
        if (Avx512F.IsSupported)
        {
            return "avx512";
        }
        if (Avx2.IsSupported)
        {
            return "avx2";
        }
        return Avx.IsSupported ? "avx" : "noavx";
    }

    private static bool HasGpuFiles(
        string nativeRoot,
        string backendDirectory,
        string acceleratorFile,
        string cpuFile)
    {
        var directory = Path.Combine(nativeRoot, backendDirectory);
        var llama = OperatingSystem.IsWindows() ? "llama.dll" : "libllama.so";
        return File.Exists(Path.Combine(directory, llama))
            && File.Exists(Path.Combine(directory, acceleratorFile))
            && File.Exists(Path.Combine(directory, cpuFile));
    }

    private static bool CanLoadSystemLibrary(string name)
    {
        if (!NativeLibrary.TryLoad(name, out var handle))
        {
            return false;
        }
        NativeLibrary.Free(handle);
        return true;
    }
}
