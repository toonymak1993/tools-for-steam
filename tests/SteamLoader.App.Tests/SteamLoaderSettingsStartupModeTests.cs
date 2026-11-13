using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Services;
using Microsoft.Win32;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamLoaderSettingsStartupModeTests
{
    private const string ExplorerShellCommand = "explorer.exe";
    private const string ExecutablePath = @"C:\ToolsForSteam\ToolsForSteam.exe";
    private const string ShellLaunchArguments = "--shell";
    private const string LegacyShellCommand = $"\"{ExecutablePath}\" --tray --shell-bootstrap --startup-sync";

    [Theory]
    [InlineData("xbox")]
    [InlineData("external")]
    public void EnsureDefaultConsoleModeEnabled_MigratesRetiredXboxModeToConsoleMode(string legacyMode)
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();
        var gamingConfigurationPath = $@"{registryRoot}\GamingConfiguration";
        var backupPath = $@"{registryRoot}\XboxModeBackup";
        var leaveSessionCalls = 0;

        try
        {
            using (var gaming = Registry.CurrentUser.CreateSubKey(gamingConfigurationPath))
            {
                gaming!.SetValue("GamingHomeApp", LegacyXboxModeCleanup.LegacyHostAumid, RegistryValueKind.String);
                gaming.SetValue("StartupToGamingHome", 1, RegistryValueKind.DWord);
            }

            using (var backup = Registry.CurrentUser.CreateSubKey(backupPath))
            {
                backup!.SetValue("Created", 1, RegistryValueKind.DWord);
                backup.SetValue("GamingHomeAppExisted", 1, RegistryValueKind.DWord);
                backup.SetValue("GamingHomeApp", "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", RegistryValueKind.String);
                backup.SetValue("StartupToGamingHomeExisted", 0, RegistryValueKind.DWord);
            }

            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                $$"""
                {
                  "startupMode": "{{legacyMode}}",
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var cleanup = new LegacyXboxModeCleanup(gamingConfigurationPath, backupPath, () => leaveSessionCalls++);
            var task = new FakeConsoleStartTask();
            var service = CreateService(settingsPath, registryRoot, cleanup, task);

            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, snapshot.StartupMode);
            Assert.False(snapshot.HideWindowsShellInConsoleMode);
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
            Assert.True(task.IsRegistered());
            Assert.False(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Contains("\"startupMode\": \"console\"", File.ReadAllText(settingsPath));

            using var restored = Registry.CurrentUser.OpenSubKey(gamingConfigurationPath);
            Assert.Equal("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", restored!.GetValue("GamingHomeApp"));
            Assert.Null(restored.GetValue("StartupToGamingHome"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(backupPath));
            Assert.Equal(0, leaveSessionCalls);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Theory]
    [InlineData("console", true)]
    [InlineData("tray", false)]
    public void EnsureDefaultConsoleModeEnabled_ReEnablesOnlyAConsoleModeTaskThatWasDisabled(
        string startupMode,
        bool expectRepair)
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                $$"""
                {
                  "startupMode": "{{startupMode}}",
                  "consoleModeDefaultApplied": true,
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var task = new FakeConsoleStartTask();
            task.Register();
            task.Disabled = true;
            var service = CreateService(settingsPath, registryRoot, consoleStartTask: task);

            service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(expectRepair, task.IsRegistered());
            Assert.Equal(expectRepair ? 2 : 1, task.RegisterCalls);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_TurnsTheOldHideDefaultOffOnceAndKeepsALaterChoice()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "console",
                  "hideWindowsShellInConsoleMode": true,
                  "consoleModeDefaultApplied": true,
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var task = new FakeConsoleStartTask();
            task.Register();
            var service = CreateService(settingsPath, registryRoot, consoleStartTask: task);

            Assert.False(service.EnsureDefaultConsoleModeEnabled().HideWindowsShellInConsoleMode);

            // The user turns it on in the QAM afterwards: later starts keep it.
            service.SetHideWindowsShellInConsoleMode(true);
            Assert.True(service.EnsureDefaultConsoleModeEnabled().HideWindowsShellInConsoleMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void ConsoleStartTaskXml_DisabledTaskIsRecognizedOnlyForTheSameExecutable()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Settings><Enabled>false</Enabled></Settings>
              <Actions><Exec><Command>C:\ToolsForSteam\ToolsForSteam.exe</Command></Exec></Actions>
            </Task>
            """;

        Assert.True(ConsoleStartTaskService.IsDisabledTaskXmlForExecutable(xml, ExecutablePath));
        Assert.False(ConsoleStartTaskService.IsDisabledTaskXmlForExecutable(xml, @"D:\Dev\ToolsForSteam.exe"));
        Assert.False(ConsoleStartTaskService.IsDisabledTaskXmlForExecutable(
            xml.Replace("<Enabled>false</Enabled>", "<Enabled>true</Enabled>"),
            ExecutablePath));
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_MigratesRemovedShellTakeoverToConsoleMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            SeedShellCommand(registryRoot, LegacyShellCommand);
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "shell",
                  "hideWindowsShellInConsoleMode": false,
                  "consoleModeDefaultApplied": true,
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var task = new FakeConsoleStartTask();
            var service = CreateService(settingsPath, registryRoot, consoleStartTask: task);

            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, snapshot.StartupMode);
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
            Assert.True(task.IsRegistered());
            // The user's own choice to keep the taskbar visible survives.
            Assert.False(snapshot.HideWindowsShellInConsoleMode);
            Assert.Contains("\"startupMode\": \"console\"", File.ReadAllText(settingsPath));
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_RemovesALeftoverShellValueAndKeepsETray()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            SeedShellCommand(registryRoot, @"""D:\Old\ToolsForSteam.exe"" --tray --shell-bootstrap --startup-sync");
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "tray",
                  "consoleModeDefaultApplied": true,
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var task = new FakeConsoleStartTask();
            var service = CreateService(settingsPath, registryRoot, consoleStartTask: task);

            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(SteamLoaderRuntime.StartupModeTray, snapshot.StartupMode);
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
            Assert.True(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.False(task.IsRegistered());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_FallsBackToETrayWhenTheSignInTaskCannotBeCreated()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            SeedShellCommand(registryRoot, LegacyShellCommand);
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "startupMode": "shell",
                  "runOnWindowsSignInUserConfigured": true
                }
                """);
            var task = new FakeConsoleStartTask { FailRegistration = true };
            var service = CreateService(settingsPath, registryRoot, consoleStartTask: task);

            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            // Never left on the removed shell: Explorer is back and TFS still
            // starts at sign-in from the tray.
            Assert.Equal(SteamLoaderRuntime.StartupModeTray, snapshot.StartupMode);
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
            Assert.True(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void EnsureDefaultConsoleModeEnabled_FreshInstallStartsInConsoleMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var task = new FakeConsoleStartTask();
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);

            var snapshot = service.EnsureDefaultConsoleModeEnabled();

            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, snapshot.StartupMode);
            Assert.True(task.IsRegistered());
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Theory]
    [InlineData("xbox")]
    [InlineData("shell")]
    public void GetSnapshot_ResolvesRemovedModesAsConsoleMode(string removedMode)
    {
        var root = CreateTempRoot();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                $$"""
                {
                  "startupMode": "{{removedMode}}",
                  "runOnWindowsSignInUserConfigured": true
                }
                """);

            var service = CreateService(settingsPath);

            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, service.GetSnapshot().StartupMode);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("console", true)]
    [InlineData(" Console ", true)]
    [InlineData("tray", true)]
    [InlineData("shell", false)]
    [InlineData("xbox", false)]
    [InlineData("external", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedStartupMode_OnlyAcceptsConsoleAndTray(string? mode, bool expected)
    {
        Assert.Equal(expected, SteamLoaderSettingsService.IsSupportedStartupMode(mode));
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
    public void SetStartupMode_Tray_DisablesHideWindowsShellInConsoleMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            service.SetStartupMode("console");
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
    public void SetStartupMode_TransitionsRemainMutuallyExclusive()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var task = new FakeConsoleStartTask();
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);
            var shellService = CreateShellService(registryRoot);
            var autostartService = CreateAutostartService(registryRoot);

            service.SetStartupMode("console");
            Assert.Equal(ExplorerShellCommand, shellService.GetShellCommand());
            Assert.False(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.True(task.IsRegistered());
            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, service.GetSnapshot().StartupMode);

            service.SetStartupMode("tray");
            Assert.Equal(ExplorerShellCommand, shellService.GetShellCommand());
            Assert.True(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.False(task.IsRegistered());
            Assert.Equal(SteamLoaderRuntime.StartupModeTray, service.GetSnapshot().StartupMode);

            service.SetStartupMode("console");
            Assert.False(autostartService.IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.True(task.IsRegistered());
            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, service.GetSnapshot().StartupMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetHideWindowsShellInConsoleMode_IgnoresEnableRequestOutsideConsoleMode()
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

    [Fact]
    public void SetStartupMode_Console_KeepsExplorerAndRegistersTheSignInTask()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var task = new FakeConsoleStartTask();
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);
            service.SetStartupMode("tray");

            var snapshot = service.SetStartupMode("console");

            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, snapshot.StartupMode);
            Assert.Equal(SteamLoaderRuntime.StartupModeConsole, service.GetSnapshot().StartupMode);
            Assert.Equal(ExplorerShellCommand, CreateShellService(registryRoot).GetShellCommand());
            Assert.False(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.True(task.IsRegistered());
            Assert.False(snapshot.HideWindowsShellInConsoleMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_LeavingConsoleMode_RemovesTheSignInTask()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var task = new FakeConsoleStartTask();
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);
            service.SetStartupMode("console");

            var snapshot = service.SetStartupMode("tray");

            Assert.Equal(SteamLoaderRuntime.StartupModeTray, snapshot.StartupMode);
            Assert.False(task.IsRegistered());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_Console_WhenTheTaskCannotBeCreated_KeepsThePreviousMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var task = new FakeConsoleStartTask { FailRegistration = true };
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);
            service.SetStartupMode("tray");

            Assert.Throws<InvalidOperationException>(() => service.SetStartupMode("console"));

            Assert.True(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.Equal(SteamLoaderRuntime.StartupModeTray, service.GetSnapshot().StartupMode);
            Assert.False(task.IsRegistered());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_Tray_StillSwitchesWhenTheOldSignInTaskCannotBeRemoved()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var task = new FakeConsoleStartTask { FailRemoval = true };
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);

            var snapshot = service.SetStartupMode("tray");

            Assert.Equal(SteamLoaderRuntime.StartupModeTray, snapshot.StartupMode);
            Assert.True(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetStartupMode_FromTrayToConsole_KeepsHidingTheTaskbarOffByDefault()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            service.SetStartupMode("tray");

            var snapshot = service.SetStartupMode("console");

            Assert.False(snapshot.HideWindowsShellInConsoleMode);
            Assert.False(service.ShouldHideWindowsShellInConsoleMode());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void SetHideWindowsShellInConsoleMode_KeepsTheUserChoiceInConsoleMode()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);
            service.SetStartupMode("console");

            Assert.False(service.SetHideWindowsShellInConsoleMode(false).HideWindowsShellInConsoleMode);
            Assert.False(service.SetStartupMode("console").HideWindowsShellInConsoleMode);
            Assert.True(service.SetHideWindowsShellInConsoleMode(true).HideWindowsShellInConsoleMode);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void RemoveStartupIntegration_GivesWindowsItsShellBackAndDropsEveryHook()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            SeedPreviousShell(registryRoot, "custom-shell.exe");
            var task = new FakeConsoleStartTask();
            task.Register();
            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot, consoleStartTask: task);
            CreateAutostartService(registryRoot).SetEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments, true);

            service.RemoveStartupIntegration();

            Assert.Equal("custom-shell.exe", CreateShellService(registryRoot).GetShellCommand());
            Assert.False(CreateAutostartService(registryRoot).IsEnabled(ExecutablePath, SteamLoaderRuntime.AutostartArguments));
            Assert.False(task.IsRegistered());
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Theory]
    [InlineData("console", true)]
    [InlineData("tray", false)]
    public void GetSnapshot_ReportsWindowsXboxModeConflictOnlyForConsoleStarts(string mode, bool expected)
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();
        var gamingConfigurationPath = $@"{registryRoot}\GamingConfiguration";

        try
        {
            using (var gaming = Registry.CurrentUser.CreateSubKey(gamingConfigurationPath))
            {
                gaming!.SetValue("StartupToGamingHome", 1, RegistryValueKind.DWord);
            }

            var service = CreateService(
                Path.Combine(root, "settings.json"),
                registryRoot,
                xboxModeDetector: new WindowsXboxModeDetector(gamingConfigurationPath, () => false));
            service.SetStartupMode(mode);

            Assert.Equal(expected, service.GetSnapshot().WindowsXboxModeConflict);
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteRegistryRoot(registryRoot);
        }
    }

    [Fact]
    public void RemoveStartupIntegration_LeavesAForeignShellAlone()
    {
        var root = CreateTempRoot();
        var registryRoot = CreateRegistryRoot();

        try
        {
            using (var shellKey = Registry.CurrentUser.CreateSubKey(GetWinlogonKeyPath(registryRoot)))
            {
                shellKey!.SetValue("Shell", "playnite.fullscreenapp.exe", RegistryValueKind.String);
            }

            var service = CreateService(Path.Combine(root, "settings.json"), registryRoot);

            service.RemoveStartupIntegration();

            Assert.Equal("playnite.fullscreenapp.exe", CreateShellService(registryRoot).GetShellCommand());
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
        LegacyXboxModeCleanup? legacyXboxModeCleanup = null,
        IConsoleStartTask? consoleStartTask = null,
        WindowsXboxModeDetector? xboxModeDetector = null)
    {
        var autostartService = registryRoot is null
            ? new WindowsAutostartService("ToolsForSteamTests") { DisableScheduledTasks = false }
            : CreateAutostartService(registryRoot);
        var shellService = registryRoot is null
            ? new WindowsShellService()
            : CreateShellService(registryRoot);
        // Never touch the real Windows Gaming configuration from tests.
        var cleanup = legacyXboxModeCleanup ?? new LegacyXboxModeCleanup(
            $@"Software\SteamLoaderTests\{Guid.NewGuid():N}\GamingConfiguration",
            $@"Software\SteamLoaderTests\{Guid.NewGuid():N}\XboxModeBackup",
            () => { });
        // Never touch the real Task Scheduler from tests either.
        return new SteamLoaderSettingsService(
            autostartService,
            shellService,
            cleanup,
            consoleStartTask ?? new FakeConsoleStartTask(),
            executablePath: ExecutablePath,
            shellLaunchArguments: ShellLaunchArguments,
            settingsPath: settingsPath)
        {
            // Never read the real Windows Xbox Mode state in these tests.
            XboxModeDetector = xboxModeDetector ?? new WindowsXboxModeDetector(
                $@"Software\SteamLoaderTests\{Guid.NewGuid():N}\GamingConfiguration",
                () => false)
        };
    }

    private sealed class FakeConsoleStartTask : IConsoleStartTask
    {
        private bool _registered;

        public bool FailRegistration { get; init; }

        public bool FailRemoval { get; init; }

        public bool IsRegistered() => _registered && !Disabled;

        public bool Disabled { get; set; }

        public int RegisterCalls { get; private set; }

        public bool IsDisabledForThisExecutable() => _registered && Disabled;

        public void Register()
        {
            RegisterCalls++;
            if (FailRegistration)
            {
                throw new InvalidOperationException("Task Scheduler refused the task.");
            }

            _registered = true;
            Disabled = false;
        }

        public void Unregister()
        {
            if (FailRemoval)
            {
                throw new InvalidOperationException("Task Scheduler refused the removal.");
            }

            _registered = false;
        }
    }

    private static WindowsAutostartService CreateAutostartService(string registryRoot)
    {
        // Never disable real Task Scheduler tasks from tests.
        return new WindowsAutostartService(GetRunKeyPath(registryRoot), "ToolsForSteamTests")
        {
            DisableScheduledTasks = false
        };
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

    private static void SeedShellCommand(string registryRoot, string shellCommand)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey(GetWinlogonKeyPath(registryRoot));
        shellKey!.SetValue("Shell", shellCommand, RegistryValueKind.String);
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
}
