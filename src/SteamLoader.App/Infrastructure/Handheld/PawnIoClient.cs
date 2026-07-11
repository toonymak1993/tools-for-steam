using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class PawnIoClient : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint LoadBinaryControl = DeviceType | (0x821u << 2);
    private const uint ExecuteControl = DeviceType | (0x841u << 2);
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const int FunctionNameLength = 32;

    private SafeFileHandle? _handle;

    public void ConnectAndLoad(byte[] module)
    {
        _handle = CreateFile(
            @"\\?\GLOBALROOT\Device\PawnIO",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            _handle = CreateFile(
                @"\\.\PawnIO",
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
        }

        if (_handle.IsInvalid)
        {
            throw new InvalidOperationException($"PawnIO device could not be opened (Win32 {Marshal.GetLastWin32Error()}).");
        }

        if (!DeviceIoControl(_handle, LoadBinaryControl, module, (uint)module.Length, null, 0, out _, IntPtr.Zero))
        {
            throw new InvalidOperationException($"The signed RyzenSMU module could not be loaded (Win32 {Marshal.GetLastWin32Error()}).");
        }
    }

    public ulong[] Execute(string functionName, ulong[]? arguments, int outputCount)
    {
        if (_handle is null || _handle.IsInvalid)
        {
            throw new InvalidOperationException("PawnIO is not connected.");
        }

        var input = new byte[FunctionNameLength + ((arguments?.Length ?? 0) * sizeof(ulong))];
        var name = Encoding.ASCII.GetBytes(functionName);
        Buffer.BlockCopy(name, 0, input, 0, Math.Min(name.Length, FunctionNameLength - 1));
        if (arguments is { Length: > 0 })
        {
            Buffer.BlockCopy(arguments, 0, input, FunctionNameLength, arguments.Length * sizeof(ulong));
        }

        var outputBytes = outputCount > 0 ? new byte[outputCount * sizeof(ulong)] : null;
        if (!DeviceIoControl(
                _handle,
                ExecuteControl,
                input,
                (uint)input.Length,
                outputBytes,
                (uint)(outputBytes?.Length ?? 0),
                out var returned,
                IntPtr.Zero))
        {
            throw new InvalidOperationException($"PawnIO function {functionName} failed (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var output = new ulong[Math.Min(outputCount, (int)returned / sizeof(ulong))];
        if (output.Length > 0)
        {
            Buffer.BlockCopy(outputBytes!, 0, output, 0, output.Length * sizeof(ulong));
        }

        return output;
    }

    public void Dispose() => _handle?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] input,
        uint inputSize,
        byte[]? output,
        uint outputSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
