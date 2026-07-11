using System.Reflection;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class MsiClawA8TdpController
{
    private const uint StrixPointCodeName = 31;
    private const uint SetStapmCommand = 0x14;
    private const uint SetFastCommand = 0x15;
    private const uint SetSlowCommand = 0x16;
    private const uint Mp1CommandAddress = 0x03B10928;
    private const uint Mp1ResponseAddress = 0x03B10978;
    private const uint Mp1ArgumentsAddress = 0x03B10998;
    private const int MailboxRetries = 8096;
    private static readonly Mutex PciMutex = new(false, @"Global\Access_PCI");

    public string Apply(int watts)
    {
        var device = HandheldDeviceCatalog.Detect();
        if (!device.IsDetected || !string.Equals(device.Id, "msi-claw-a8", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TDP writes are restricted to a detected MSI Claw A8 (MS-1T8K).");
        }

        if (watts < device.MinimumTdpWatts || watts > device.MaximumTdpWatts)
        {
            throw new ArgumentOutOfRangeException(nameof(watts), $"TDP must be between {device.MinimumTdpWatts} W and {device.MaximumTdpWatts} W.");
        }

        using var pawnIo = new PawnIoClient();
        pawnIo.ConnectAndLoad(ReadOfficialRyzenSmuModule());
        var codeName = pawnIo.Execute("ioctl_get_code_name", null, 1).FirstOrDefault();
        if (codeName != StrixPointCodeName)
        {
            throw new InvalidOperationException($"Expected AMD Strix Point (31), but RyzenSMU reported {codeName}.");
        }

        if (!PciMutex.WaitOne(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The shared PCI bus lock could not be acquired.");
        }

        try
        {
            ValidateMailbox(pawnIo);
            var milliwatts = checked((uint)watts * 1000u);
            SetAndVerify(pawnIo, SetStapmCommand, milliwatts, "STAPM");
            SetAndVerify(pawnIo, SetSlowCommand, milliwatts, "Slow");
            SetAndVerify(pawnIo, SetFastCommand, milliwatts, "Fast");
        }
        finally
        {
            PciMutex.ReleaseMutex();
        }

        return "StrixPoint";
    }

    private static void SetAndVerify(PawnIoClient pawnIo, uint command, uint milliwatts, string limitName)
    {
        WriteRegister(pawnIo, Mp1ResponseAddress, 0);
        for (var index = 0; index < 6; index++)
        {
            WriteRegister(pawnIo, Mp1ArgumentsAddress + ((uint)index * 4), index == 0 ? milliwatts : 0);
        }

        WriteRegister(pawnIo, Mp1CommandAddress, command);
        var status = WaitForNonZero(pawnIo, Mp1ResponseAddress, $"{limitName} response");
        if (status != 0x01)
        {
            throw new InvalidOperationException($"RyzenSMU rejected the {limitName} limit with status 0x{status:X2}.");
        }

        var confirmed = ReadRegister(pawnIo, Mp1ArgumentsAddress);
        if (confirmed != milliwatts)
        {
            throw new InvalidOperationException(
                $"RyzenSMU returned {confirmed} mW instead of {milliwatts} mW for the {limitName} limit.");
        }
    }

    private static void ValidateMailbox(PawnIoClient pawnIo)
    {
        _ = WaitForNonZero(pawnIo, Mp1ResponseAddress, "initial mailbox");
        WriteRegister(pawnIo, Mp1ResponseAddress, 0);
        WriteRegister(pawnIo, Mp1ArgumentsAddress, 0x47);
        if (ReadRegister(pawnIo, Mp1ArgumentsAddress) != 0x47)
        {
            throw new InvalidOperationException("The Strix Point MP1 argument register did not echo the validation marker.");
        }

        WriteRegister(pawnIo, Mp1CommandAddress, 0x01);
        var status = WaitForNonZero(pawnIo, Mp1ResponseAddress, "test message");
        if (status != 0x01)
        {
            throw new InvalidOperationException($"The Strix Point MP1 test message returned status 0x{status:X2}.");
        }
    }

    private static uint ReadRegister(PawnIoClient pawnIo, uint address)
    {
        var output = pawnIo.Execute("ioctl_read_smu_register", [address], 1);
        return output.Length == 1
            ? checked((uint)output[0])
            : throw new InvalidOperationException($"RyzenSMU did not return register 0x{address:X8}.");
    }

    private static void WriteRegister(PawnIoClient pawnIo, uint address, uint value)
    {
        _ = pawnIo.Execute("ioctl_write_smu_register", [address, value], 0);
    }

    private static uint WaitForNonZero(PawnIoClient pawnIo, uint address, string operation)
    {
        for (var attempt = 0; attempt < MailboxRetries; attempt++)
        {
            var value = ReadRegister(pawnIo, address);
            if (value != 0)
            {
                return value;
            }

            Thread.Yield();
        }

        throw new TimeoutException($"The Strix Point MP1 mailbox timed out during {operation}.");
    }

    private static byte[] ReadOfficialRyzenSmuModule()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith("ThirdParty.PawnIO.RyzenSMU.bin", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidOperationException("The official PawnIO RyzenSMU module is missing.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
