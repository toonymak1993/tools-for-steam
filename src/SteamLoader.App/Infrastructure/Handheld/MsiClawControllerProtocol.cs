using HidSharp;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// MSI Claw controller-mode commands used to expose M1/M2 as regular
/// DirectInput buttons. The Claw A8 uses firmware profile 0x0308.
/// </summary>
internal static class MsiClawControllerProtocol
{
    private const int VendorId = 0x0DB0;
    private const int XInputProductId = 0x1901;
    private const int DirectInputProductId = 0x1902;
    private const int TestingProductId = 0x1903;
    private const byte SwitchModeCommand = 0x24;
    private const byte SyncToRomCommand = 0x22;
    private const byte XInputMode = 0x01;
    private const byte DirectInputMode = 0x02;
    private static readonly int[] ProductIds = [XInputProductId, DirectInputProductId, TestingProductId];
    private static readonly object VibrationSync = new();

    public static bool IsDirectInputActive =>
        DeviceList.Local.GetHidDevices(VendorId, DirectInputProductId).Any();

    public static bool TryEnableTfsButtonMode(out string status)
    {
        if (IsDirectInputActive)
        {
            status = "MSI DirectInput button mode is already active.";
            return true;
        }

        if (!TryOpenControlStream(out var stream, out var deviceName))
        {
            status = "MSI controller HID could not be opened. Check the MSI gamepad driver.";
            return false;
        }

        using (stream)
        {
            try
            {
                // Firmware 0x0308 stores M1/M2 at 0x00BA and 0x0163.
                Write(stream, BuildMKeyReport(0x00, 0xBA));
                Thread.Sleep(300);
                Write(stream, BuildMKeyReport(0x01, 0x63));
                Thread.Sleep(300);
                Write(stream, BuildCommandReport(SyncToRomCommand));
                Thread.Sleep(300);
                Write(stream, BuildSwitchModeReport(DirectInputMode));
                Thread.Sleep(1200);
                status = $"MSI DirectInput button mode enabled through {deviceName}.";
                return true;
            }
            catch (Exception exception)
            {
                status = $"MSI DirectInput mode failed: {exception.Message}";
                return false;
            }
        }
    }

    public static bool TryRestoreXInputMode(out string status)
    {
        if (!IsDirectInputActive)
        {
            status = "MSI controller is already in XInput mode.";
            return true;
        }

        if (!TryOpenControlStream(out var stream, out var deviceName))
        {
            status = "MSI DirectInput HID could not be opened; MSI Center M will restore its preferred mode.";
            return false;
        }

        using (stream)
        {
            try
            {
                Write(stream, BuildSwitchModeReport(XInputMode));
                Thread.Sleep(1200);
                status = $"MSI XInput mode restored through {deviceName}.";
                return true;
            }
            catch (Exception exception)
            {
                status = $"MSI XInput restore failed: {exception.Message}";
                return false;
            }
        }
    }

    public static bool TrySetVibration(byte largeMotor, byte smallMotor)
    {
        var report = BuildVibrationReport(largeMotor, smallMotor);
        lock (VibrationSync)
        {
            var candidates = DeviceList.Local.GetHidDevices(VendorId, DirectInputProductId)
                .Where(device => device.GetMaxOutputReportLength() >= report.Length)
                .OrderBy(device => Math.Abs(device.GetMaxOutputReportLength() - report.Length));
            foreach (var device in candidates)
            {
                if (!device.TryOpen(out var stream))
                {
                    continue;
                }

                using (stream)
                {
                    try
                    {
                        Write(stream, report);
                        return true;
                    }
                    catch
                    {
                    }
                }
            }
        }
        return false;
    }

    internal static byte ScaleVibration(byte motor, int strengthPercent) =>
        (byte)Math.Clamp(
            (int)Math.Round(motor * (Math.Clamp(strengthPercent, 0, 100) / 100.0)),
            0,
            byte.MaxValue);

    internal static byte[] BuildVibrationReport(byte largeMotor, byte smallMotor) =>
    [
        0x05, 0x01, 0x00, 0x00,
        smallMotor,
        largeMotor,
        0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    internal static byte[] BuildSwitchModeReport(byte mode)
    {
        var report = BuildCommandReport(SwitchModeCommand);
        report[5] = mode;
        report[6] = 0x00; // M-key function: macro
        return report;
    }

    internal static byte[] BuildMKeyReport(byte addressHigh, byte addressLow)
    {
        var report = new byte[64];
        byte[] payload =
        [
            0x0F, 0x00, 0x00, 0x3C,
            0x21, 0x01,
            addressHigh, addressLow,
            0x05, 0x01,
            0x00, 0x00,
            0x11, 0x00
        ];
        payload.CopyTo(report, 0);
        return report;
    }

    private static byte[] BuildCommandReport(byte command)
    {
        var report = new byte[64];
        report[0] = 0x0F;
        report[3] = 0x3C;
        report[4] = command;
        return report;
    }

    private static bool TryOpenControlStream(out HidStream stream, out string deviceName)
    {
        var candidates = ProductIds
            .SelectMany(productId => DeviceList.Local.GetHidDevices(VendorId, productId))
            .Where(device => device.GetMaxOutputReportLength() >= 64)
            .OrderByDescending(device => device.DevicePath.Contains("MI_01", StringComparison.OrdinalIgnoreCase))
            .ThenBy(device => device.ProductID == DirectInputProductId ? 0 : 1);

        foreach (var device in candidates)
        {
            if (!device.TryOpen(out var candidate))
            {
                continue;
            }

            stream = candidate;
            deviceName = $"VID_{device.VendorID:X4}&PID_{device.ProductID:X4}";
            return true;
        }

        stream = null!;
        deviceName = string.Empty;
        return false;
    }

    private static void Write(HidStream stream, byte[] report)
    {
        stream.Write(report);
        stream.Flush();
    }
}
