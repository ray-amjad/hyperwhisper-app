// PROCESS LOOPBACK INTEROP
// Hand-written COM interop for process-loopback capture (Windows 10 build 20348+ / Windows 11).
// NAudio 2.2.1 has no working process-loopback activation, so the PROPVARIANT and the agile
// completion handler are written out here. References: audioclientactivationparams.h,
// mmdeviceapi.h, audioclient.h and Microsoft's ApplicationLoopback sample.

using System.Runtime.InteropServices;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Muffle;

internal static class ProcessLoopbackNative
{
    /// <summary>First Windows build with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.</summary>
    public const int MinimumWindowsBuild = 20348;

    public const string ProcessLoopbackDevicePath = "VAD\\Process_Loopback";

    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    public const ushort VT_BLOB = 65;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int ERROR_TIMEOUT_HRESULT = unchecked((int)0x800705B4);

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild);

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    /// <summary>
    /// Activates a process-loopback IAudioClient that captures <paramref name="targetProcessId"/>
    /// and its child processes. Blocks until the audio service answers, at most <paramref name="timeout"/>.
    /// </summary>
    public static Result<IAudioClientRaw> ActivateProcessLoopback(uint targetProcessId, TimeSpan timeout)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = targetProcessId,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var variant = new PropVariantBlob { vt = VT_BLOB, cbSize = (uint)paramsSize, pBlobData = paramsPtr };
            Marshal.StructureToPtr(variant, variantPtr, false);

            using var handler = new ActivationCompletionHandler();
            var iid = IID_IAudioClient;
            var hr = ActivateAudioInterfaceAsync(ProcessLoopbackDevicePath, ref iid, variantPtr, handler, out var operation);
            if (hr < 0)
            {
                return Result<IAudioClientRaw>.Failure($"ActivateAudioInterfaceAsync failed (hr=0x{hr:X8})");
            }

            if (!handler.Wait(timeout))
            {
                return Result<IAudioClientRaw>.Failure($"process loopback activation timed out (hr=0x{ERROR_TIMEOUT_HRESULT:X8})");
            }

            GC.KeepAlive(operation);
            if (handler.CallHr < 0)
            {
                return Result<IAudioClientRaw>.Failure($"GetActivateResult failed (hr=0x{handler.CallHr:X8})");
            }

            if (handler.ActivateHr < 0)
            {
                return Result<IAudioClientRaw>.Failure($"process loopback activation failed (hr=0x{handler.ActivateHr:X8})");
            }

            return handler.Activated is IAudioClientRaw client
                ? Result<IAudioClientRaw>.Success(client)
                : Result<IAudioClientRaw>.Failure($"activated object is not an IAudioClient (hr=0x{E_NOINTERFACE:X8})");
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }
}

// AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType; union { AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS { TargetProcessId; ProcessLoopbackMode; } } }
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

// A PROPVARIANT holding a BLOB: vt @0, three reserved WORDs, BLOB.cbSize @8, BLOB.pBlobData at the
// next pointer boundary. Size 24 on x64 and ARM64, 16 on x86.
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public uint cbSize;
    public IntPtr pBlobData;
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted([MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation activateOperation);
}

// IAgileObject marker. The audio service calls the handler from its own worker thread and refuses
// a handler that is not agile (E_ILLEGAL_METHOD_CALL).
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

// The whole IAudioClient vtable in order, PreserveSig so the audio thread sees raw HRESULTs and
// never allocates an exception.
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClientRaw
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([In] ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClientRaw
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolumeRaw
{
    [PreserveSig] int SetMasterVolume(float level, [In] ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [In] ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject, IDisposable
{
    private readonly ManualResetEventSlim _done = new(false);

    public int CallHr { get; private set; }
    public int ActivateHr { get; private set; }
    public object? Activated { get; private set; }

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            CallHr = activateOperation.GetActivateResult(out var activateHr, out var activated);
            ActivateHr = activateHr;
            Activated = activated;
        }
        catch (Exception ex)
        {
            CallHr = ex.HResult;
        }
        finally
        {
            _done.Set();
        }

        return 0;
    }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    // A completion that arrives after a timeout only sets an event nobody waits on, so the event is
    // left to the finalizer instead of being disposed under a late callback.
    public void Dispose()
    {
        if (_done.IsSet)
        {
            _done.Dispose();
        }
    }
}
