using System.Management;

namespace SteamLoader.App.Infrastructure.Handheld;

/// <summary>
/// Thin wrapper over MSI's ACPI WMI interface (namespace root\WMI, class
/// MSI_ACPI) - the same mechanism MSI Center M and Handheld Companion use to talk
/// to the embedded controller. No kernel driver is involved.
///
/// Each MSI_ACPI method takes and returns a 32-byte package: input byte 0 selects
/// the subfeature, the remaining 31 bytes are data; output byte 0 is 0x00 on
/// failure. Set_* methods require the process to run elevated.
/// </summary>
public sealed class MsiWmiInterface
{
    private const string ScopePath = @"\\.\root\WMI";
    private const string AcpiClass = "MSI_ACPI";
    private const string PackageClass = "Package_32";
    private const int PackageSize = 32;

    /// <summary>True if the MSI_ACPI WMI class is present and has an instance.</summary>
    public bool IsAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(ScopePath),
                new ObjectQuery($"SELECT * FROM {AcpiClass}"));
            using var results = searcher.Get();
            foreach (var instance in results)
            {
                instance.Dispose();
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Names of the WMI methods MSI_ACPI exposes on this machine (for diagnostics).</summary>
    public IReadOnlyList<string> GetMethodNames()
    {
        try
        {
            using var wmiClass = new ManagementClass(
                new ManagementScope(ScopePath),
                new ManagementPath(AcpiClass),
                null);

            return wmiClass.Methods
                .Cast<MethodData>()
                .Select(method => method.Name)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Invokes an MSI_ACPI method with a 32-byte input package and returns the
    /// 32-byte output package, or null on failure. <paramref name="input"/> is
    /// padded or truncated to 32 bytes.
    /// </summary>
    public byte[]? Invoke(string method, params byte[] input)
    {
        try
        {
            var buffer = new byte[PackageSize];
            if (input is { Length: > 0 })
            {
                Array.Copy(input, buffer, Math.Min(input.Length, PackageSize));
            }

            var scope = new ManagementScope(ScopePath);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {AcpiClass}"));
            using var results = searcher.Get();
            var instance = results.Cast<ManagementObject>().FirstOrDefault();
            if (instance is null)
            {
                return null;
            }

            using (instance)
            using (var packageClass = new ManagementClass(scope, new ManagementPath(PackageClass), null))
            using (var package = packageClass.CreateInstance())
            {
                package["Bytes"] = buffer;

                using var inParams = instance.GetMethodParameters(method);
                inParams["Data"] = package;

                using var outParams = instance.InvokeMethod(method, inParams, null);
                if (outParams?["Data"] is ManagementBaseObject outPackage)
                {
                    return outPackage["Bytes"] as byte[];
                }

                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Convenience: invoke a Get_* method with the given subfeature selector.</summary>
    public byte[]? Read(string method, byte subFeature = 0x00) => Invoke(method, subFeature);
}
