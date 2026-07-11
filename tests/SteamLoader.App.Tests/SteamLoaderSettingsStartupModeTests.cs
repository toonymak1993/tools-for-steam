using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using Microsoft.Win32;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsStartupModeTests
{
    private const string ExternalStartupMode = SteamLoaderRuntime.StartupModeXbox;
    private const string ExplorerShellCommand = "explorer.exe";
    private const string ExecutablePath = @"C:\ToolsForSteam\ToolsForSteam.exe";
    private const string ShellLaunchArguments = "--shell";

    [Fact]
    public void GetSnapshot_ReadsExternalStartupMode_WhenConfigured()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "external",
                  "runOnWindowsSignInUserConfigured": true
                }
                """);

            var service = CreateService(settingsPath);

            Assert.Equal(ExternalStartupMode, service.GetSnapshot().StartupMode);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_PreservesUserConfiguredExternalMode()
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "external",
                  "runOnWindowsSignInUserConfigured": true
                }
                """);

            var service = CreateService(settingsPath);
            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(ExternalStartupMode, snapshot.StartupMode);
            Assert.Equal(ExternalStartupMode, service.GetSnapshot().StartupMode);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_FallsBackToTrayWhenXboxSupportIsLost()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();
        var xboxMode = new TestXboxModeService { IsSupported = false };

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "xbox",
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var service = CreateService(settingsPath, registryRoot, xboxMode);

            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(SteamLoaderRuntime.StartupModeTray, snapshot.StartupMode);
            Assert.True(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_Tray_ForcesExplorerShell()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            SeedPreviousShell(registryRoot, "custom-shell.exe");
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);

            service.SetStartupMode("tray");

            var shellService = CreateShellService(registryRoot);
            Assert.Equal(ExplorerShellCommand, shellService.GetShellCommand());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_External_ForcesExplorerShell()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            SeedPreviousShell(registryRoot, "custom-shell.exe");
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);

            service.SetStartupMode(ExternalStartupMode);

            var shellService = CreateShellService(registryRoot);
            Assert.Equal(ExplorerShellCommand, shellService.GetShellCommand());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_Shell_SetsToolsForSteamShellCommand()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);

            service.SetStartupMode("shell");

            var shellService = CreateShellService(registryRoot);
            Assert.Equal($"\"{ExecutablePath}\" {ShellLaunchArguments}", shellService.GetShellCommand());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_Tray_DisablesHideWindowsShellInConsoleMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            service.SetStartupMode("shell");
            service.SetHideWindowsShellInConsoleMode(true);

            var snapshot = service.SetStartupMode("tray");

            Assert.False(snapshot.HideWindowsShellInConsoleMode);
            Assert.False(service.GetSnapshot().HideWindowsShellInConsoleMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_External_DisablesHideWindowsShellInConsoleMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            service.SetStartupMode("shell");
            service.SetHideWindowsShellInConsoleMode(true);

            var snapshot = service.SetStartupMode(ExternalStartupMode);

            Assert.False(snapshot.HideWindowsShellInConsoleMode);
            Assert.False(service.GetSnapshot().HideWindowsShellInConsoleMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_TransitionsRemainMutuallyExclusive()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            var shellService = CreateShellService(registryRoot);
            var autostartService = CreateAutostartService(registryRoot);

            service.SetStartupMode("shell");
            Assert.Equal($"\"{ExecutablePath}\" {ShellLaunchArguments}", shellService.GetShellCommand());
            Assert.False(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(SteamLoaderRuntime.StartupModeShell, service.GetSnapshot().StartupMode);

            service.SetStartupMode("tray");
            Assert.Equal(ExplorerShellCommand, shellService.GetShellCommand());
            Assert.True(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(SteamLoaderRuntime.StartupModeTray, service.GetSnapshot().StartupMode);

            service.SetStartupMode(ExternalStartupMode);
            Assert.Equal(ExplorerShellCommand, shellService.GetShellCommand());
            Assert.False(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(ExternalStartupMode, service.GetSnapshot().StartupMode);

            service.SetStartupMode("shell");
            Assert.Equal($"\"{ExecutablePath}\" {ShellLaunchArguments}", shellService.GetShellCommand());
            Assert.False(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(SteamLoaderRuntime.StartupModeShell, service.GetSnapshot().StartupMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_XboxFailureRollsBackToPreviousMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();
        var xboxMode = new TestXboxModeService { FailOnEnable = true };

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, xboxMode);
            var shellService = CreateShellService(registryRoot);
            var autostartService = CreateAutostartService(registryRoot);
            service.SetStartupMode("shell");

            Assert.Throws<InvalidOperationException>(() => service.SetStartupMode(SteamLoaderRuntime.StartupModeXbox));

            Assert.Equal($"\"{ExecutablePath}\" {ShellLaunchArguments}", shellService.GetShellCommand());
            Assert.False(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(SteamLoaderRuntime.StartupModeShell, service.GetSnapshot().StartupMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetHideWindowsShellInConsoleMode_IgnoresEnableRequestOutsideShellMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            service.SetStartupMode("tray");

            var snapshot = service.SetHideWindowsShellInConsoleMode(true);

            Assert.False(snapshot.HideWindowsShellInConsoleMode);
            Assert.False(service.GetSnapshot().HideWindowsShellInConsoleMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    private static SteamLoaderSettingsService CreateService(
        string settingsPath,
        string? registryRoot = null,
        IXboxModeService? xboxModeService = null)
    {
        var autostartService = registryRoot is null
            ? new WindowsAutostartService("ToolsForSteamTests")
            : CreateAutostartService(registryRoot);
        var shellService = registryRoot is null
            ? new WindowsShellService()
            : CreateShellService(registryRoot);
        return xboxModeService is null
            ? new SteamLoaderSettingsService(
                autostartService,
                shellService,
                executablePath: ExecutablePath,
                shellLaunchArguments: ShellLaunchArguments,
                settingsPath: settingsPath)
            : new SteamLoaderSettingsService(
                autostartService,
                shellService,
                xboxModeService,
                executablePath: ExecutablePath,
                shellLaunchArguments: ShellLaunchArguments,
                settingsPath: settingsPath);
    }

    private static WindowsAutostartService CreateAutostartService(string registryRoot)
    {
        return new WindowsAutostartService(GetRunKeyPath(registryRoot), "ToolsForSteamTests");
    }

    private static WindowsShellService CreateShellService(string registryRoot)
    {
        return new WindowsShellService(
            GetWinlogonKeyPath(registryRoot),
            GetStateKeyPath(registryRoot));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateRegistryRoot()
    {
        return $@"Software\SteamLoaderTests\{Guid.NewGuid():N}";
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DeleteRegistryRoot(string registryRoot)
    {
        Registry.CurrentUser.DeleteSubKeyTree(registryRoot, throwOnMissingSubKey: false);
    }

    private static void SeedPreviousShell(string registryRoot, string previousShell)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey(GetWinlogonKeyPath(registryRoot));
        shellKey?.SetValue("Shell", "\"C:\\ToolsForSteam\\ToolsForSteam.exe\" --shell", RegistryValueKind.String);

        using var stateKey = Registry.CurrentUser.CreateSubKey(GetStateKeyPath(registryRoot));
        stateKey?.SetValue("PreviousShell", previousShell, RegistryValueKind.String);
    }

    private static string GetRunKeyPath(string registryRoot) => $@"{registryRoot}\Run";

    private static string GetWinlogonKeyPath(string registryRoot) => $@"{registryRoot}\Winlogon";

    private static string GetStateKeyPath(string registryRoot) => $@"{registryRoot}\State";

    private sealed class TestXboxModeService : IXboxModeService
    {
        private bool _enabled;

        public bool FailOnEnable { get; init; }

        public bool IsSupported { get; init; } = true;

        public XboxModeSupportStatus GetSupportStatus() => new(IsSupported, IsSupported ? string.Empty : "Unsupported test platform.");

        public void SetStartupEnabled(bool enabled)
        {
            if (enabled && FailOnEnable)
            {
                throw new InvalidOperationException("Simulated Xbox Mode activation failure.");
            }

            _enabled = enabled;
        }

        public bool VerifyStartupEnabled(bool expectedEnabled) => _enabled == expectedEnabled;

        public void RestoreOnUninstall()
        {
            _enabled = false;
        }
    }
}
