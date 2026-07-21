using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed class RtssProfileClient : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void ProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int SetProfilePropertyDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string propertyName,
        ref int propertyData,
        uint propertySize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateProfilesDelegate();

    private nint _module;
    private readonly string _installPath;
    private readonly ProfileDelegate _loadProfile;
    private readonly ProfileDelegate _saveProfile;
    private readonly ProfileDelegate _deleteProfile;
    private readonly SetProfilePropertyDelegate _setProfileProperty;
    private readonly UpdateProfilesDelegate _updateProfiles;

    public RtssProfileClient(string installPath)
    {
        _installPath = Path.GetFullPath(installPath);
        var libraryPath = Path.Combine(installPath, Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll");
        _module = NativeLibrary.Load(libraryPath);
        _loadProfile = LoadDelegate<ProfileDelegate>("LoadProfile");
        _saveProfile = LoadDelegate<ProfileDelegate>("SaveProfile");
        _deleteProfile = LoadDelegate<ProfileDelegate>("DeleteProfile");
        _setProfileProperty = LoadDelegate<SetProfilePropertyDelegate>("SetProfileProperty");
        _updateProfiles = LoadDelegate<UpdateProfilesDelegate>("UpdateProfiles");
    }

    public void ApplyGameProfile(string executableName, int frameLimit)
    {
        executableName = executableName.Trim();
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return;
        }

        if (!string.Equals(executableName, Path.GetFileName(executableName), StringComparison.Ordinal)
            || executableName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("RTSS profile names must be executable file names without a path.");
        }

        _loadProfile(executableName);
        SetInt("AppDetectionLevel", 1);
        SetInt("EnableOSD", 1);
        SetInt("FramerateLimit", Math.Max(0, frameLimit));
        _saveProfile(executableName);
        _updateProfiles();

        var savedProfilePath = Path.Combine(
            _installPath,
            "Profiles",
            executableName + ".cfg");
        var expectedFrameLimit = Math.Max(0, frameLimit);
        if (!ProfileContainsFrameLimit(savedProfilePath, expectedFrameLimit))
        {
            throw new InvalidOperationException(
                "RTSS could not save the per-game frame-limit profile. Repair RTSS once in Tools for Steam to restore profile access.");
        }
    }

    public void Dispose()
    {
        if (_module == 0)
        {
            return;
        }

        NativeLibrary.Free(_module);
        _module = 0;
    }

    internal void DeleteGameProfile(string executableName)
    {
        if (!string.IsNullOrWhiteSpace(executableName))
        {
            _deleteProfile(executableName);
            _updateProfiles();
        }
    }

    private void SetInt(string propertyName, int value)
    {
        if (_setProfileProperty(propertyName, ref value, sizeof(int)) == 0)
        {
            throw new InvalidOperationException($"RTSS profile property '{propertyName}' could not be set.");
        }
    }

    private static bool ProfileContainsFrameLimit(string profilePath, int expectedFrameLimit)
    {
        if (!File.Exists(profilePath))
        {
            return false;
        }

        try
        {
            var inFramerateSection = false;
            foreach (var sourceLine in File.ReadLines(profilePath))
            {
                var line = sourceLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inFramerateSection = line.Equals("[Framerate]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inFramerateSection
                    && line.StartsWith("Limit=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Limit=".Length..], out var savedFrameLimit))
                {
                    return savedFrameLimit == expectedFrameLimit;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private T LoadDelegate<T>(string exportName) where T : Delegate
    {
        var address = NativeLibrary.GetExport(_module, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}
