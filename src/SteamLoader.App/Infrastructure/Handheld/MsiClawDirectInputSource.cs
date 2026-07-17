using Vortice.DirectInput;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Reads the MSI Claw DirectInput controller without taking exclusive ownership.
/// The layout is shared by the supported Claw firmware families, including the
/// Claw A8 firmware 0x0308.
/// </summary>
internal sealed class MsiClawDirectInputSource : IDisposable
{
    private const ushort VendorId = 0x0DB0;
    private const ushort ProductId = 0x1902;
    private const int StickDeadzone = 4096;
    private const int TriggerDeadzone = 2048;

    private readonly IDirectInput8 _directInput;
    private readonly IDirectInputDevice8 _joystick;
    private bool _disposed;

    private MsiClawDirectInputSource(IDirectInput8 directInput, IDirectInputDevice8 joystick)
    {
        _directInput = directInput;
        _joystick = joystick;
    }

    public static bool TryOpen(out MsiClawDirectInputSource? source, out string status)
    {
        IDirectInput8? directInput = null;
        try
        {
            directInput = DInput.DirectInput8Create();
            foreach (var instance in directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                if (!MatchesProduct(instance.ProductGuid))
                {
                    continue;
                }

                IDirectInputDevice8? joystick = null;
                try
                {
                    joystick = directInput.CreateDevice(instance.InstanceGuid);
                    joystick.SetDataFormat<RawJoystickState>().CheckError();
                    joystick.SetCooperativeLevel(
                        GetDesktopWindow(),
                        CooperativeLevel.Background | CooperativeLevel.NonExclusive).CheckError();
                    joystick.Acquire().CheckError();
                    source = new MsiClawDirectInputSource(directInput, joystick);
                    status = $"MSI DirectInput controller opened: {instance.ProductName}";
                    return true;
                }
                catch
                {
                    joystick?.Dispose();
                }
            }

            directInput.Dispose();
            source = null;
            status = "MSI Claw DirectInput controller VID 0DB0 / PID 1902 was not found.";
            return false;
        }
        catch (Exception exception)
        {
            directInput?.Dispose();
            source = null;
            status = $"MSI DirectInput initialization failed: {exception.Message}";
            return false;
        }
    }

    public bool TryRead(out MsiClawPhysicalGamepadState state, out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var directInput = _joystick.GetCurrentJoystickState();
            if (directInput.RotationX == 32767 && directInput.RotationY == 32767 && directInput.RotationZ == 32767)
            {
                state = default;
                error = "MSI DirectInput returned its uninitialized neutral report.";
                return false;
            }

            state = ConvertState(directInput);
            error = string.Empty;
            return true;
        }
        catch
        {
            try
            {
                _joystick.Acquire().CheckError();
                state = ConvertState(_joystick.GetCurrentJoystickState());
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                state = default;
                error = exception.Message;
                return false;
            }
        }
    }

    private static MsiClawPhysicalGamepadState ConvertState(JoystickState state) => ConvertState(
        new MsiClawDirectInputSnapshot(
            state.X,
            state.Y,
            state.Z,
            state.RotationX,
            state.RotationY,
            state.RotationZ,
            state.PointOfViewControllers.ElementAtOrDefault(0),
            state.Buttons));

    internal static MsiClawPhysicalGamepadState ConvertState(MsiClawDirectInputSnapshot state)
    {
        uint buttons = 0;
        SetButton(state, 1, 0x1000, ref buttons); // A
        SetButton(state, 2, 0x2000, ref buttons); // B
        SetButton(state, 0, 0x4000, ref buttons); // X
        SetButton(state, 3, 0x8000, ref buttons); // Y
        SetButton(state, 4, 0x0100, ref buttons); // LB
        SetButton(state, 5, 0x0200, ref buttons); // RB
        SetButton(state, 8, 0x0020, ref buttons); // Back
        SetButton(state, 9, 0x0010, ref buttons); // Start
        SetButton(state, 10, 0x0040, ref buttons); // LS
        SetButton(state, 11, 0x0080, ref buttons); // RS

        var pov = state.PointOfView;
        if (pov is 0 or 4500 or 31500) buttons |= 0x0001;
        if (pov is 9000 or 4500 or 13500) buttons |= 0x0008;
        if (pov is 18000 or 13500 or 22500) buttons |= 0x0002;
        if (pov is 27000 or 31500 or 22500) buttons |= 0x0004;

        return new MsiClawPhysicalGamepadState(
            buttons,
            MapTrigger(state.RotationX),
            MapTrigger(state.RotationY),
            MapAxis(state.X, invert: false),
            MapAxis(state.Y, invert: true),
            MapAxis(state.Z, invert: false),
            MapAxis(state.RotationZ, invert: true),
            ReadButton(state, 15),
            ReadButton(state, 16));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _joystick.Unacquire(); }
        catch { }
        _joystick.Dispose();
        _directInput.Dispose();
    }

    private static bool MatchesProduct(Guid productGuid)
    {
        var value = unchecked((uint)BitConverter.ToInt32(productGuid.ToByteArray(), 0));
        return (value & 0xFFFF) == VendorId && ((value >> 16) & 0xFFFF) == ProductId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    private static void SetButton(MsiClawDirectInputSnapshot state, int index, uint value, ref uint buttons)
    {
        if (ReadButton(state, index))
        {
            buttons |= value;
        }
    }

    private static bool ReadButton(MsiClawDirectInputSnapshot state, int index) =>
        index >= 0 && index < state.Buttons.Length && state.Buttons[index];

    private static byte MapTrigger(int value)
    {
        var clamped = Math.Clamp(value, 0, 65535);
        if (clamped <= TriggerDeadzone)
        {
            return 0;
        }

        return (byte)Math.Clamp(
            (int)Math.Round((clamped - TriggerDeadzone) * (255.0 / (65535 - TriggerDeadzone))),
            0,
            255);
    }

    private static short MapAxis(int value, bool invert)
    {
        var clamped = Math.Clamp(value, 0, 65535);
        var delta = clamped - 32767.5;
        if (invert)
        {
            delta = -delta;
        }

        var magnitude = Math.Abs(delta);
        if (magnitude <= StickDeadzone)
        {
            return 0;
        }

        var normalized = Math.Clamp(
            (magnitude - StickDeadzone) / (32767.5 - StickDeadzone),
            0,
            1);
        var maximum = delta < 0 ? 32768.0 : 32767.0;
        return (short)Math.Round(Math.Sign(delta) * normalized * maximum);
    }
}

internal readonly record struct MsiClawDirectInputSnapshot(
    int X,
    int Y,
    int Z,
    int RotationX,
    int RotationY,
    int RotationZ,
    int PointOfView,
    bool[] Buttons);

internal readonly record struct MsiClawPhysicalGamepadState(
    uint Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    bool M1,
    bool M2);
