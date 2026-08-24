using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed class UInputPasteBackend : IUInputPasteBackend
{
    private const string DevicePath = "/dev/uinput";
    private const ulong SetEventBit = 0x40045564;
    private const ulong SetKeyBit = 0x40045565;
    private const ulong DeviceSetup = 0x405C5503;
    private const ulong DeviceCreate = 0x5501;
    private const ulong DeviceDestroy = 0x5502;
    private const int EventKey = 1;
    private const int EventSync = 0;
    private const int KeyLeftControl = 29;
    private const int KeyV = 47;

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var stream = new FileStream(DevicePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return stream.CanWrite;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public PlatformResult Paste()
    {
        FileStream? stream = null;
        var created = false;
        try
        {
            stream = new FileStream(DevicePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var descriptor = checked((int)stream.SafeFileHandle.DangerousGetHandle());
            if (IoctlValue(descriptor, SetEventBit, EventKey) < 0
                || IoctlValue(descriptor, SetEventBit, EventSync) < 0
                || IoctlValue(descriptor, SetKeyBit, KeyLeftControl) < 0
                || IoctlValue(descriptor, SetKeyBit, KeyV) < 0)
            {
                return PlatformResult.Failure("uinput_configure_failed", "The virtual keyboard could not be configured.");
            }

            var setup = new UInputSetup
            {
                Id = new InputId { BusType = 3, Vendor = 0x1209, Product = 0x0001, Version = 1 },
                Name = "HyperWhisper Virtual Keyboard",
            };
            if (IoctlSetup(descriptor, DeviceSetup, ref setup) < 0 || Ioctl(descriptor, DeviceCreate) < 0)
            {
                return PlatformResult.Failure("uinput_create_failed", "The virtual keyboard could not be created.");
            }
            created = true;
            Thread.Sleep(50);

            WriteEvent(stream, EventKey, KeyLeftControl, 1);
            WriteEvent(stream, EventKey, KeyV, 1);
            WriteEvent(stream, EventSync, 0, 0);
            WriteEvent(stream, EventKey, KeyV, 0);
            WriteEvent(stream, EventKey, KeyLeftControl, 0);
            WriteEvent(stream, EventSync, 0, 0);
            stream.Flush(flushToDisk: false);
            return PlatformResult.Success();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or OverflowException
            or NotSupportedException
            or InvalidOperationException)
        {
            return PlatformResult.Failure("uinput_unavailable", "The virtual keyboard is unavailable.");
        }
        finally
        {
            if (created && stream is not null)
            {
                try { Ioctl(checked((int)stream.SafeFileHandle.DangerousGetHandle()), DeviceDestroy); } catch { }
            }
            try { stream?.Dispose(); } catch { }
        }
    }

    private static void WriteEvent(Stream stream, ushort type, ushort code, int value)
    {
        Span<byte> input = stackalloc byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(input[16..18], type);
        BinaryPrimitives.WriteUInt16LittleEndian(input[18..20], code);
        BinaryPrimitives.WriteInt32LittleEndian(input[20..24], value);
        stream.Write(input);
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int descriptor, ulong request);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlValue(int descriptor, ulong request, int value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlSetup(int descriptor, ulong request, ref UInputSetup setup);

    [StructLayout(LayoutKind.Sequential)]
    private struct InputId
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct UInputSetup
    {
        public InputId Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string Name;
        public uint ForceFeedbackEffectsMaximum;
    }
}
