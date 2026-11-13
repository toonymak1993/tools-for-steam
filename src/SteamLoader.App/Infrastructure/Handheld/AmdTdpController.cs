using System.Reflection;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// MP1 mailbox layout and power-limit message ids of one AMD APU family.
/// Values follow RyzenAdj's per-family tables; the family itself is reported by the
/// official PawnIO RyzenSMU module (ioctl_get_code_name), not guessed from the CPU name.
/// </summary>
internal sealed record AmdSmuFamily(
    string Name,
    uint CommandAddress,
    uint ResponseAddress,
    uint ArgumentsAddress,
    uint StapmCommand,
    uint FastCommand,
    uint SlowCommand,
    bool EchoesArgument);

internal static class AmdSmuFamilies
{
    private static readonly AmdSmuFamily Raven = new("Raven", 0x03B10528, 0x03B10564, 0x03B10998, 0x1A, 0x1B, 0x1C, false);
    private static readonly AmdSmuFamily Renoir = new("Renoir", 0x03B10528, 0x03B10578, 0x03B10998, 0x14, 0x15, 0x16, false);
    private static readonly AmdSmuFamily Strix = new("StrixPoint", 0x03B10928, 0x03B10978, 0x03B10998, 0x14, 0x15, 0x16, true);

    // PawnIO RyzenSMU code names (enum order of the module).
    private static readonly Dictionary<uint, AmdSmuFamily> ByCodeName = new()
    {
        [6] = Raven with { Name = "RavenRidge" },
        [7] = Raven with { Name = "RavenRidge2" },
        [2] = Raven with { Name = "Picasso" },
        [15] = Raven with { Name = "Dali" },
        [1] = Renoir,
        [22] = Renoir with { Name = "Lucienne" },
        [13] = Renoir with { Name = "Cezanne" },
        [12] = Renoir with { Name = "VanGogh" },
        [10] = Renoir with { Name = "Rembrandt" },
        [25] = Renoir with { Name = "Mendocino" },
        [23] = Renoir with { Name = "Phoenix" },
        [24] = Renoir with { Name = "Phoenix2" },
        [30] = Renoir with { Name = "HawkPoint" },
        [31] = Strix,
        // Same mailbox as Strix Point; the argument echo is only proven on Strix Point.
        [32] = Strix with { Name = "StrixHalo", EchoesArgument = false },
        [33] = Strix with { Name = "KrackanPoint", EchoesArgument = false },
        [34] = Strix with { Name = "KrackanPoint2", EchoesArgument = false },
    };

    public static AmdSmuFamily? Find(uint codeName)
        => ByCodeName.TryGetValue(codeName, out var family) ? family : null;

    public static IReadOnlyCollection<uint> SupportedCodeNames => ByCodeName.Keys;
}

/// <summary>
/// Writes STAPM (SPL), slow (SPPT) and fast (FPPT) limits through the AMD MP1 mailbox
/// using the signed PawnIO RyzenSMU module. Runs only in the elevated helper.
/// </summary>
internal sealed class AmdTdpController
{
    private const int MailboxRetries = 8096;
    private static readonly Mutex PciMutex = new(false, @"Global\Access_PCI");

    public string Apply(HandheldTdpCapability capability, int splWatts, int spptWatts, int fpptWatts)
    {
        ValidateLimits(capability, splWatts, spptWatts, fpptWatts);

        using var pawnIo = new PawnIoClient();
        pawnIo.ConnectAndLoad(ReadOfficialRyzenSmuModule());
        var codeName = (uint)pawnIo.Execute("ioctl_get_code_name", null, 1).FirstOrDefault();
        var family = AmdSmuFamilies.Find(codeName)
            ?? throw new InvalidOperationException(
                $"This AMD processor family (RyzenSMU code {codeName}) has no verified TDP mailbox yet.");

        if (!PciMutex.WaitOne(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The shared PCI bus lock could not be acquired.");
        }

        try
        {
            ValidateMailbox(pawnIo, family);
            SetLimit(pawnIo, family, family.StapmCommand, checked((uint)splWatts * 1000u), "STAPM");
            SetLimit(pawnIo, family, family.SlowCommand, checked((uint)spptWatts * 1000u), "Slow");
            SetLimit(pawnIo, family, family.FastCommand, checked((uint)fpptWatts * 1000u), "Fast");
        }
        finally
        {
            PciMutex.ReleaseMutex();
        }

        return family.Name;
    }

    internal static void ValidateLimits(HandheldTdpCapability capability, int splWatts, int spptWatts, int fpptWatts)
    {
        if (splWatts < capability.MinimumWatts || splWatts > capability.MaximumWatts ||
            spptWatts < splWatts || spptWatts > Math.Max(splWatts, capability.MaximumSpptWatts) ||
            fpptWatts < spptWatts || fpptWatts > Math.Max(spptWatts, capability.MaximumFpptWatts))
        {
            throw new ArgumentOutOfRangeException(
                nameof(splWatts),
                $"Power limits must satisfy SPL <= SPPT <= FPPT within {capability.MinimumWatts}-{capability.MaximumWatts} W " +
                $"(SPPT <= {capability.MaximumSpptWatts} W, FPPT <= {capability.MaximumFpptWatts} W).");
        }
    }

    private static void SetLimit(PawnIoClient pawnIo, AmdSmuFamily family, uint command, uint milliwatts, string limitName)
    {
        WriteRegister(pawnIo, family.ResponseAddress, 0);
        for (var index = 0; index < 6; index++)
        {
            WriteRegister(pawnIo, family.ArgumentsAddress + ((uint)index * 4), index == 0 ? milliwatts : 0);
        }

        WriteRegister(pawnIo, family.CommandAddress, command);
        var status = WaitForNonZero(pawnIo, family, $"{limitName} response");
        if (status != 0x01)
        {
            throw new InvalidOperationException($"The {family.Name} SMU rejected the {limitName} limit with status 0x{status:X2}.");
        }

        if (family.EchoesArgument)
        {
            var confirmed = ReadRegister(pawnIo, family.ArgumentsAddress);
            if (confirmed != milliwatts)
            {
                throw new InvalidOperationException(
                    $"The {family.Name} SMU returned {confirmed} mW instead of {milliwatts} mW for the {limitName} limit.");
            }
        }
    }

    private static void ValidateMailbox(PawnIoClient pawnIo, AmdSmuFamily family)
    {
        _ = WaitForNonZero(pawnIo, family, "initial mailbox");
        WriteRegister(pawnIo, family.ResponseAddress, 0);
        WriteRegister(pawnIo, family.ArgumentsAddress, 0x47);
        if (ReadRegister(pawnIo, family.ArgumentsAddress) != 0x47)
        {
            throw new InvalidOperationException($"The {family.Name} MP1 argument register did not echo the validation marker.");
        }

        WriteRegister(pawnIo, family.CommandAddress, 0x01);
        var status = WaitForNonZero(pawnIo, family, "test message");
        if (status != 0x01)
        {
            throw new InvalidOperationException($"The {family.Name} MP1 test message returned status 0x{status:X2}.");
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

    private static uint WaitForNonZero(PawnIoClient pawnIo, AmdSmuFamily family, string operation)
    {
        for (var attempt = 0; attempt < MailboxRetries; attempt++)
        {
            var value = ReadRegister(pawnIo, family.ResponseAddress);
            if (value != 0)
            {
                return value;
            }

            Thread.Yield();
        }

        throw new TimeoutException($"The {family.Name} MP1 mailbox timed out during {operation}.");
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
