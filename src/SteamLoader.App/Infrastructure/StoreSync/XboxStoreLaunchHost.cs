using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Keeps the process launched by a Steam non-Steam shortcut alive while an Xbox/Game Pass
/// title hands off from its short-lived bootstrap executable to the real game process.
/// </summary>
internal static class XboxStoreLaunchHost
{
    internal const string LaunchArgument = "--store-sync-xbox-launch";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StableProcessThreshold = TimeSpan.FromSeconds(5);
    // Once a stable game has existed, a short grace period is enough to cover
    // a final process handoff without leaving Steam in "Playing" for ten seconds
    // after the actual game has already closed.
    private static readonly TimeSpan ProcessHandoffGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan OverlayBootstrapWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OverlayProbeInterval = TimeSpan.FromSeconds(1);

    internal static string BuildLaunchArguments(string executablePath, string startDirectory)
    {
        var payload = new LaunchPayload(executablePath, startDirectory);
        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{LaunchArgument} {encoded}";
    }

    internal static bool TryParseArguments(string[] args, out LaunchPayload payload)
    {
        payload = default!;
        var argumentIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, LaunchArgument, StringComparison.OrdinalIgnoreCase));
        if (argumentIndex < 0 || argumentIndex + 1 >= args.Length)
        {
            return false;
        }

        try
        {
            var encoded = args[argumentIndex + 1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var parsed = JsonSerializer.Deserialize<LaunchPayload>(Convert.FromBase64String(encoded));
            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.ExecutablePath) ||
                !string.Equals(Path.GetExtension(parsed.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var executablePath = Path.GetFullPath(parsed.ExecutablePath);
            var startDirectory = string.IsNullOrWhiteSpace(parsed.StartDirectory)
                ? Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
                : Path.GetFullPath(parsed.StartDirectory);
            payload = new LaunchPayload(executablePath, startDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static int Run(LaunchPayload payload)
    {
        if (!File.Exists(payload.ExecutablePath))
        {
            WriteLog($"launch failed: executable not found path={payload.ExecutablePath}");
            return 2;
        }

        var rootDirectory = ResolveGameRoot(payload.ExecutablePath, payload.StartDirectory);
        var targetExecutableName = Path.GetFileName(payload.ExecutablePath);
        var trustedExecutableNames = LoadTrustedExecutableNames(rootDirectory, payload.ExecutablePath);
        var baselineProcessIds = CaptureProcessTree().Keys.ToHashSet();
        var sessionProcessIds = new HashSet<uint>();
        var firstSeenAt = new Dictionary<uint, DateTimeOffset>();
        var processPathCache = new Dictionary<uint, string>();
        var lastOverlayProbeAt = new Dictionary<uint, DateTimeOffset>();
        var overlayFoundProcessIds = new HashSet<uint>();
        var overlayStatusLoggedProcessIds = new HashSet<uint>();
        var launchedAt = DateTimeOffset.UtcNow;
        var lastSessionProcessSeenAt = launchedAt;
        var stableGameProcessSeen = false;
        var hostOverlay = WaitForOverlayRenderer((uint)Environment.ProcessId, OverlayBootstrapWaitTimeout);

        WriteLog(
            $"overlay bootstrap hostPid={Environment.ProcessId} hostOverlay={DescribeOverlayProbe(hostOverlay)} " +
            $"steamContext={BuildSteamContextSummary()}");
        launchedAt = DateTimeOffset.UtcNow;
        lastSessionProcessSeenAt = launchedAt;

        try
        {
            uint launchedProcessId;
            string launchStrategy;
            var nativeLaunchDetail = "host overlay unavailable";
            if (hostOverlay.Status == OverlayProbeStatus.Present &&
                TryStartSuspended(payload, out launchedProcessId, out nativeLaunchDetail))
            {
                launchStrategy = $"native-suspended {nativeLaunchDetail}";
            }
            else
            {
                if (hostOverlay.Status == OverlayProbeStatus.Present)
                {
                    WriteLog($"overlay-aware native launch unavailable: {nativeLaunchDetail}; using managed fallback");
                }

                using var launchedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = payload.ExecutablePath,
                    WorkingDirectory = GetWorkingDirectory(payload),
                    UseShellExecute = false,
                });

                if (launchedProcess is null)
                {
                    WriteLog($"launch failed: Process.Start returned null path={payload.ExecutablePath}");
                    return 3;
                }

                launchedProcessId = (uint)launchedProcess.Id;
                launchStrategy = hostOverlay.Status == OverlayProbeStatus.Present
                    ? "managed-fallback"
                    : "managed-host-not-injected";
            }

            sessionProcessIds.Add(launchedProcessId);
            firstSeenAt[launchedProcessId] = launchedAt;
            WriteLog(
                $"launch started pid={launchedProcessId} strategy={launchStrategy} " +
                $"target={payload.ExecutablePath} root={rootDirectory}");
        }
        catch (Exception exception)
        {
            WriteLog($"launch failed: {exception.Message} path={payload.ExecutablePath}");
            return 4;
        }

        while (true)
        {
            Thread.Sleep(PollInterval);
            var now = DateTimeOffset.UtcNow;
            var processes = CaptureProcessTree();
            var activeSessionProcessIds = new HashSet<uint>();

            foreach (var process in processes.Values)
            {
                if (baselineProcessIds.Contains(process.ProcessId) && !sessionProcessIds.Contains(process.ProcessId))
                {
                    continue;
                }

                var isKnownSessionProcess = sessionProcessIds.Contains(process.ProcessId);
                var isNewSessionProcess = IsDescendantOfSession(process, processes, sessionProcessIds) ||
                                          MatchesGameProcess(process, rootDirectory, trustedExecutableNames, processPathCache);
                if (!isKnownSessionProcess &&
                    isNewSessionProcess &&
                    LooksLikeHelperProcess(process.Name) &&
                    !string.Equals(process.Name, targetExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var belongsToSession = isKnownSessionProcess || isNewSessionProcess;
                if (!belongsToSession)
                {
                    continue;
                }

                sessionProcessIds.Add(process.ProcessId);
                activeSessionProcessIds.Add(process.ProcessId);
                if (!firstSeenAt.TryGetValue(process.ProcessId, out var seenAt))
                {
                    seenAt = now;
                    firstSeenAt[process.ProcessId] = seenAt;
                    WriteLog(
                        $"session process discovered pid={process.ProcessId} parentPid={process.ParentProcessId} " +
                        $"name={process.Name}");
                }

                ProbeAndLogOverlayStatus(
                    process,
                    seenAt,
                    now,
                    lastOverlayProbeAt,
                    overlayFoundProcessIds,
                    overlayStatusLoggedProcessIds);

                if (now - seenAt >= StableProcessThreshold)
                {
                    stableGameProcessSeen = true;
                }
            }

            if (activeSessionProcessIds.Count > 0)
            {
                lastSessionProcessSeenAt = now;
                continue;
            }

            var waitExpired = stableGameProcessSeen
                ? now - lastSessionProcessSeenAt >= ProcessHandoffGrace
                : now - launchedAt >= StartupTimeout;
            if (waitExpired)
            {
                var overlayProcessSummary = overlayFoundProcessIds.Count == 0
                    ? "none"
                    : string.Join(',', overlayFoundProcessIds.OrderBy(processId => processId));
                WriteLog(
                    (stableGameProcessSeen
                        ? "game session ended"
                        : "game process was not detected before the startup timeout") +
                    $"; overlaySummary host={hostOverlay.Status.ToString().ToLowerInvariant()} " +
                    $"gamePids={overlayProcessSummary}");
                return 0;
            }
        }
    }

    private static void ProbeAndLogOverlayStatus(
        ProcessEntry process,
        DateTimeOffset firstSeenAt,
        DateTimeOffset now,
        IDictionary<uint, DateTimeOffset> lastProbeAt,
        ISet<uint> overlayFoundProcessIds,
        ISet<uint> statusLoggedProcessIds)
    {
        if (overlayFoundProcessIds.Contains(process.ProcessId) ||
            (lastProbeAt.TryGetValue(process.ProcessId, out var lastProbe) && now - lastProbe < OverlayProbeInterval))
        {
            return;
        }

        lastProbeAt[process.ProcessId] = now;
        var probe = ProbeOverlayRenderer(process.ProcessId);
        if (probe.Status == OverlayProbeStatus.Present)
        {
            overlayFoundProcessIds.Add(process.ProcessId);
            WriteLog(
                $"overlay renderer detected pid={process.ProcessId} name={process.Name} module={probe.ModuleName}");
            return;
        }

        if (now - firstSeenAt >= StableProcessThreshold && statusLoggedProcessIds.Add(process.ProcessId))
        {
            WriteLog(
                $"overlay renderer not detected pid={process.ProcessId} name={process.Name} " +
                $"probe={DescribeOverlayProbe(probe)}");
        }
    }

    private static bool TryStartSuspended(
        LaunchPayload payload,
        out uint processId,
        out string detail)
    {
        processId = 0;
        detail = string.Empty;
        var startupInfo = new NativeMethods.StartupInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.StartupInfo>(),
        };
        var commandLine = new StringBuilder(QuoteCommandLineArgument(payload.ExecutablePath));

        if (!NativeMethods.CreateProcess(
                payload.ExecutablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CreateSuspended,
                IntPtr.Zero,
                GetWorkingDirectory(payload),
                ref startupInfo,
                out var processInformation))
        {
            detail = $"CreateProcess error={Marshal.GetLastWin32Error()}";
            return false;
        }

        processId = processInformation.ProcessId;
        try
        {
            var preResumeOverlay = ProbeOverlayRenderer(processId);
            if (NativeMethods.ResumeThread(processInformation.Thread) == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.TerminateProcess(processInformation.Process, 1);
                processId = 0;
                detail = $"ResumeThread error={error}";
                return false;
            }

            detail = $"preResumeOverlay={DescribeOverlayProbe(preResumeOverlay)}";
            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(processInformation.Thread);
            NativeMethods.CloseHandle(processInformation.Process);
        }
    }

    private static string GetWorkingDirectory(LaunchPayload payload)
    {
        return Directory.Exists(payload.StartDirectory)
            ? payload.StartDirectory
            : Path.GetDirectoryName(payload.ExecutablePath) ?? AppContext.BaseDirectory;
    }

    internal static string QuoteCommandLineArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
                backslashCount = 0;
                continue;
            }

            result.Append('\\', backslashCount);
            backslashCount = 0;
            result.Append(character);
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }

    private static OverlayProbeResult WaitForOverlayRenderer(uint processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        OverlayProbeResult probe;
        do
        {
            probe = ProbeOverlayRenderer(processId);
            if (probe.Status == OverlayProbeStatus.Present)
            {
                return probe;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }
        while (DateTimeOffset.UtcNow < deadline);

        return probe;
    }

    private static OverlayProbeResult ProbeOverlayRenderer(uint processId)
    {
        IntPtr snapshot = NativeMethods.InvalidHandleValue;
        var error = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            snapshot = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32,
                processId);
            if (snapshot != NativeMethods.InvalidHandleValue)
            {
                break;
            }

            error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorBadLength)
            {
                return new OverlayProbeResult(OverlayProbeStatus.Unavailable, null, error);
            }
        }

        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return new OverlayProbeResult(OverlayProbeStatus.Unavailable, null, error);
        }

        try
        {
            var entry = new NativeMethods.ModuleEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>(),
            };
            if (!NativeMethods.Module32First(snapshot, ref entry))
            {
                error = Marshal.GetLastWin32Error();
                return error == NativeMethods.ErrorNoMoreFiles
                    ? new OverlayProbeResult(OverlayProbeStatus.Missing, null, 0)
                    : new OverlayProbeResult(OverlayProbeStatus.Unavailable, null, error);
            }

            do
            {
                if (IsSteamOverlayRendererModule(entry.ModuleName))
                {
                    return new OverlayProbeResult(OverlayProbeStatus.Present, entry.ModuleName, 0);
                }

                entry.Size = (uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>();
            }
            while (NativeMethods.Module32Next(snapshot, ref entry));

            return new OverlayProbeResult(OverlayProbeStatus.Missing, null, 0);
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    internal static bool IsSteamOverlayRendererModule(string? moduleName)
    {
        return string.Equals(moduleName, "GameOverlayRenderer.dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(moduleName, "GameOverlayRenderer64.dll", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSteamOverlayRendererMissing(uint processId)
    {
        return ProbeOverlayRenderer(processId).Status == OverlayProbeStatus.Missing;
    }

    private static string DescribeOverlayProbe(OverlayProbeResult probe)
    {
        return probe.Status switch
        {
            OverlayProbeStatus.Present => $"present({probe.ModuleName})",
            OverlayProbeStatus.Unavailable => $"unavailable(error={probe.ErrorCode})",
            _ => "missing",
        };
    }

    private static string BuildSteamContextSummary()
    {
        var names = new[]
        {
            "SteamGameId",
            "SteamAppId",
            "SteamOverlayGameId",
            "SteamClientLaunch",
            "SteamEnv",
        };
        var values = names.Select(name =>
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value)
                ? $"{name}=<unset>"
                : $"{name}={SanitizeLogValue(value)}";
        });
        return string.Join(',', values);
    }

    private static string SanitizeLogValue(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 128 ? sanitized : sanitized[..128];
    }

    private static bool MatchesGameProcess(
        ProcessEntry process,
        string rootDirectory,
        IReadOnlySet<string> trustedExecutableNames,
        IDictionary<uint, string> processPathCache)
    {
        if (!processPathCache.TryGetValue(process.ProcessId, out var executablePath))
        {
            executablePath = TryGetProcessPath(process.ProcessId);
            processPathCache[process.ProcessId] = executablePath;
        }

        if (!string.IsNullOrWhiteSpace(executablePath) &&
            IsPathWithin(executablePath, rootDirectory))
        {
            return !LooksLikeHelperProcess(process.Name);
        }

        return trustedExecutableNames.Contains(process.Name) && !LooksLikeHelperProcess(process.Name);
    }

    private static bool IsDescendantOfSession(
        ProcessEntry process,
        IReadOnlyDictionary<uint, ProcessEntry> processes,
        IReadOnlySet<uint> sessionProcessIds)
    {
        var parentProcessId = process.ParentProcessId;
        for (var depth = 0; depth < 12 && parentProcessId != 0; depth++)
        {
            if (sessionProcessIds.Contains(parentProcessId))
            {
                return true;
            }

            if (!processes.TryGetValue(parentProcessId, out var parent))
            {
                break;
            }

            parentProcessId = parent.ParentProcessId;
        }

        return false;
    }

    private static string ResolveGameRoot(string executablePath, string startDirectory)
    {
        var directory = Directory.Exists(startDirectory)
            ? Path.GetFullPath(startDirectory)
            : Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        return string.Equals(Path.GetFileName(directory), "Content", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(directory)?.FullName ?? directory
            : directory;
    }

    private static HashSet<string> LoadTrustedExecutableNames(string rootDirectory, string targetPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(targetPath),
        };

        try
        {
            foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.exe", SearchOption.AllDirectories).Take(512))
            {
                names.Add(Path.GetFileName(path));
            }
        }
        catch
        {
        }

        return names;
    }

    private static bool LooksLikeHelperProcess(string processName)
    {
        var normalized = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();
        return normalized.Contains("crash") ||
               normalized.Contains("report") ||
               normalized.Contains("helper") ||
               normalized.Contains("updater") ||
               normalized.Contains("service");
    }

    private static bool IsPathWithin(string path, string rootDirectory)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var normalizedRoot = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<uint, ProcessEntry> CaptureProcessTree()
    {
        var result = new Dictionary<uint, ProcessEntry>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>(),
            };
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[entry.ProcessId] = new ProcessEntry(
                    entry.ProcessId,
                    entry.ParentProcessId,
                    entry.ExecutableFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    private static string TryGetProcessPath(uint processId)
    {
        var processHandle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var capacity = 32768;
            var builder = new StringBuilder(capacity);
            return NativeMethods.QueryFullProcessImageName(processHandle, 0, builder, ref capacity)
                ? builder.ToString()
                : string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            File.AppendAllText(
                Path.Combine(dataDirectory, "store-sync-launch.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    internal sealed record LaunchPayload(string ExecutablePath, string StartDirectory);

    private sealed record ProcessEntry(uint ProcessId, uint ParentProcessId, string Name);

    private sealed record OverlayProbeResult(
        OverlayProbeStatus Status,
        string? ModuleName,
        int ErrorCode);

    private enum OverlayProbeStatus
    {
        Missing,
        Present,
        Unavailable,
    }

    private static class NativeMethods
    {
        internal const uint Th32csSnapProcess = 0x00000002;
        internal const uint Th32csSnapModule = 0x00000008;
        internal const uint Th32csSnapModule32 = 0x00000010;
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint CreateSuspended = 0x00000004;
        internal const int ErrorBadLength = 24;
        internal const int ErrorNoMoreFiles = 18;
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry32
        {
            internal uint Size;
            internal uint Usage;
            internal uint ProcessId;
            internal IntPtr DefaultHeapId;
            internal uint ModuleId;
            internal uint Threads;
            internal uint ParentProcessId;
            internal int BasePriority;
            internal uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string? ExecutableFile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ModuleEntry32
        {
            internal uint Size;
            internal uint ModuleId;
            internal uint ProcessId;
            internal uint GlobalUsageCount;
            internal uint ProcessUsageCount;
            internal IntPtr BaseAddress;
            internal uint BaseSize;
            internal IntPtr Module;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string ModuleName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string ExecutablePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal uint Size;
            internal string? Reserved;
            internal string? Desktop;
            internal string? Title;
            internal uint X;
            internal uint Y;
            internal uint XSize;
            internal uint YSize;
            internal uint XCountChars;
            internal uint YCountChars;
            internal uint FillAttribute;
            internal uint Flags;
            internal ushort ShowWindow;
            internal ushort Reserved2Length;
            internal IntPtr Reserved2;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Module32First(IntPtr snapshot, ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Module32Next(IntPtr snapshot, ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
