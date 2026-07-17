using System.Globalization;
using HidSharp;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class MsiClawA8LightingController : IHandheldLightingController
{
    private const int VendorId = 0x0DB0;
    private static readonly int[] ProductIds = [0x1901, 0x1902, 0x1903];

    public string Apply(HandheldLightingSettings settings)
    {
        var device = HandheldDeviceCatalog.Detect();
        if (!device.IsDetected || device.Id != "msi-claw-a8")
        {
            throw new InvalidOperationException("RGB writes are restricted to a detected MSI Claw A8 (MS-1T8K).");
        }

        var left = settings.Enabled ? ParseColor(settings.LeftColor) : (R: (byte)0, G: (byte)0, B: (byte)0);
        var right = settings.Enabled ? ParseColor(settings.RightColor) : (R: (byte)0, G: (byte)0, B: (byte)0);
        var buttons = settings.Enabled ? ParseColor(settings.ButtonColor) : (R: (byte)0, G: (byte)0, B: (byte)0);
        if (settings.Effect == "solid")
        {
            right = left;
            buttons = left;
        }
        else if (settings.Effect != "dual-zone")
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The selected RGB effect is not supported by the MSI Claw A8.");
        }

        var report = BuildReport(settings.Enabled ? Math.Clamp(settings.Brightness, 0, 100) : 0, left, right, buttons);
        foreach (var hidDevice in ProductIds.SelectMany(id => DeviceList.Local.GetHidDevices(VendorId, id)))
        {
            if (hidDevice.GetMaxOutputReportLength() < report.Length || !hidDevice.TryOpen(out var stream))
            {
                continue;
            }

            using (stream)
            {
                stream.Write(report);
                stream.Flush();
                return $"MSI HID {hidDevice.ProductID:X4}";
            }
        }

        throw new InvalidOperationException("The MSI Claw RGB HID interface (VID 0DB0) could not be opened.");
    }

    internal static byte[] BuildReport(
        int brightness,
        (byte R, byte G, byte B) left,
        (byte R, byte G, byte B) right,
        (byte R, byte G, byte B) buttons)
    {
        var report = new byte[64];
        // The complete RGB frame is 0x24 bytes. Using the older 0x20 length
        // applies the eight stick-ring zones but truncates zone 8 (ABXY).
        byte[] header = [0x0F, 0x00, 0x00, 0x3C, 0x21, 0x01, 0x02, 0x4A, 0x24, 0x00, 0x01, 0x09, 0x03, (byte)brightness];
        header.CopyTo(report, 0);
        for (var led = 0; led < 9; led++)
        {
            var color = led < 4 ? right : led < 8 ? left : buttons;
            var offset = 14 + (led * 3);
            report[offset] = color.R;
            report[offset + 1] = color.G;
            report[offset + 2] = color.B;
        }
        return report;
    }

    private static (byte R, byte G, byte B) ParseColor(string value)
    {
        if (value is null || value.Length != 7 || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new ArgumentException("RGB colors must use #RRGGBB format.", nameof(value));
        }
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
