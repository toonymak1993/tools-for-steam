using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace SteamLoader.App.Infrastructure.Helpers;

internal sealed class ElevatedHelperTaskService
{
    private static readonly TimeSpan CompatibilityCacheLifetime = TimeSpan.FromMinutes(5);
    private const string FolderPath = "\\ToolsForSteam";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskActionExecute = 0;
    private const int TaskStateRunning = 4;

    private readonly string _taskName;
    private readonly string _displayName;
    private readonly string _taskDescription;
    private readonly string _executablePath;
    private readonly string _runArguments;
    private readonly string _registerArguments;
    private readonly string _workingDirectory;
    private readonly string _registrationLogPath;
    private readonly object _compatibilityCacheGate = new();
    private DateTimeOffset _compatibilityCacheExpiresAtUtc = DateTimeOffset.MinValue;
    private bool _cachedCompatibilityResult;
    private string _cachedCompatibilityIssue = string.Empty;

    public ElevatedHelperTaskService(
        string taskName,
        string displayName,
        string taskDescription,
        string executablePath,
        string runArguments,
        string registerArguments,
        string workingDirectory,
        string registrationLogFileName)
    {
        _taskName = taskName;
        _displayName = displayName;
        _taskDescription = taskDescription;
        _executablePath = executablePath;
        _runArguments = runArguments;
        _registerArguments = registerArguments;
        _workingDirectory = workingDirectory;
        _registrationLogPath = Path.Combine(_workingDirectory, "data", registrationLogFileName);
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public bool TryRun(out string errorText)
    {
        try
        {
            string compatibilityIssue;
            if (!TryGetRegisteredTaskCompatibilityIssue(out compatibilityIssue))
            {
                errorText = string.IsNullOrWhiteSpace(compatibilityIssue)
                    ? $"The elevated {_displayName} task is not installed yet."
                    : $"The elevated {_displayName} needs repair. {compatibilityIssue}".Trim();
                return false;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments = $"/Run /TN \"{FolderPath}\\{_taskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                InvalidateCompatibilityCache();
                errorText = $"The elevated {_displayName} task could not be started.";
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                InvalidateCompatibilityCache();
                errorText = $"The elevated {_displayName} task start timed out.";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
            {
                InvalidateCompatibilityCache();
                errorText = !string.IsNullOrWhiteSpace(error)
                    ? error
                    : !string.IsNullOrWhiteSpace(output)
                        ? output
                        : $"The elevated {_displayName} task exited with code {process.ExitCode}.";
                return false;
            }

            errorText = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            errorText = exception.Message;
            return false;
        }
    }

    public bool IsRegistered()
    {
        try
        {
            string compatibilityIssue;
            return TryGetRegisteredTaskCompatibilityIssue(out compatibilityIssue);
        }
        catch
        {
            return false;
        }
    }

    public bool IsRunning()
    {
        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;

        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            folderObject = service.GetFolder(FolderPath);
            dynamic folder = folderObject;
            taskObject = folder.GetTask(_taskName);
            dynamic task = taskObject;
            return Convert.ToInt32(task.State) == TaskStateRunning;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(taskObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    public bool TryEnsureRegistered(out string errorText, out bool cancelledByUser)
    {
        cancelledByUser = false;

        try
        {
            if (IsRegistered(forceRefresh: true))
            {
                errorText = string.Empty;
                return true;
            }

            if (IsCurrentProcessElevated())
            {
                ClearRegistrationLog();
                EnsureRegistered();
                errorText = string.Empty;
                return true;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = _registerArguments,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            ClearRegistrationLog();
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                errorText = "The elevated helper setup could not be started.";
                return false;
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (IsRegistered(forceRefresh: true))
                {
                    errorText = string.Empty;
                    return true;
                }

                if (process.HasExited)
                {
                    break;
                }

                Thread.Sleep(200);
            }

            if (process.HasExited && process.ExitCode == 0 && IsRegistered(forceRefresh: true))
            {
                errorText = string.Empty;
                ClearRegistrationLog();
                return true;
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                errorText = ReadRegistrationLogOrDefault($"The Windows admin prompt for the elevated {_displayName} timed out.");
                return false;
            }

            errorText = process.ExitCode == 0
                ? ReadRegistrationLogOrDefault("The elevated helper task was created, but its registration does not match this TFS install.")
                : ReadRegistrationLogOrDefault($"The elevated helper setup exited with code {process.ExitCode}.");
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            cancelledByUser = true;
            errorText = $"The Windows admin prompt for the elevated {_displayName} was cancelled.";
            return false;
        }
        catch (Exception exception)
        {
            errorText = exception.Message;
            return false;
        }
    }

    public void EnsureRegistered()
    {
        if (!IsCurrentProcessElevated())
        {
            throw new InvalidOperationException($"Admin rights are required to register the elevated {_displayName} task.");
        }

        dynamic service = CreateSchedulerService();
        dynamic folder = EnsureFolder(service);
        dynamic definition = service.NewTask(0);

        definition.RegistrationInfo.Description = _taskDescription;
        definition.Settings.Enabled = true;
        definition.Settings.Hidden = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.RestartCount = 3;
        definition.Settings.RestartInterval = "PT1M";

        definition.Principal.UserId = ResolveInteractiveUserId();
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = TaskRunLevelHighest;

        dynamic action = definition.Actions.Create(TaskActionExecute);
        action.Path = _executablePath;
        action.Arguments = _runArguments;
        action.WorkingDirectory = _workingDirectory;

        folder.RegisterTaskDefinition(
            _taskName,
            definition,
            TaskCreateOrUpdate,
            Type.Missing,
            Type.Missing,
            TaskLogonInteractiveToken,
            Type.Missing);

        InvalidateCompatibilityCache();
        if (!IsRegistered(forceRefresh: true))
        {
            throw new InvalidOperationException($"Windows created the elevated {_displayName} task, but it does not match this installation.");
        }
    }

    private static dynamic CreateSchedulerService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
        service.Connect();
        return service;
    }

    private static dynamic EnsureFolder(dynamic service)
    {
        try
        {
            return service.GetFolder(FolderPath);
        }
        catch
        {
            dynamic rootFolder = service.GetFolder("\\");
            return rootFolder.CreateFolder(FolderPath.TrimStart('\\'));
        }
    }

    private bool IsRegistered(bool forceRefresh)
    {
        try
        {
            return TryGetRegisteredTaskCompatibilityIssue(out _, forceRefresh);
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetRegisteredTaskCompatibilityIssue(
        out string issue,
        bool forceRefresh = false)
    {
        lock (_compatibilityCacheGate)
        {
            if (!forceRefresh &&
                DateTimeOffset.UtcNow < _compatibilityCacheExpiresAtUtc)
            {
                issue = _cachedCompatibilityIssue;
                return _cachedCompatibilityResult;
            }
        }

        var xml = TryReadTaskXmlFromSchtasks();
        bool compatible;
        if (string.IsNullOrWhiteSpace(xml))
        {
            issue = string.Empty;
            compatible = false;
        }
        else
        {
            compatible = IsCompatibleTaskXml(xml, out issue);
        }

        lock (_compatibilityCacheGate)
        {
            _cachedCompatibilityResult = compatible;
            _cachedCompatibilityIssue = issue;
            _compatibilityCacheExpiresAtUtc =
                DateTimeOffset.UtcNow.Add(CompatibilityCacheLifetime);
        }

        return compatible;
    }

    private void InvalidateCompatibilityCache()
    {
        lock (_compatibilityCacheGate)
        {
            _compatibilityCacheExpiresAtUtc = DateTimeOffset.MinValue;
        }
    }

    private bool IsCompatibleTaskXml(string xml, out string issue)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var taskNamespace = document.Root?.Name.Namespace ?? XNamespace.None;
            var command = GetTaskValue(document, taskNamespace, "Command");
            var arguments = GetTaskValue(document, taskNamespace, "Arguments");
            var workingDirectory = GetTaskValue(document, taskNamespace, "WorkingDirectory");
            var runLevel = GetTaskValue(document, taskNamespace, "RunLevel");
            var logonType = GetTaskValue(document, taskNamespace, "LogonType");
            var hidden = GetTaskValue(document, taskNamespace, "Hidden");
            var multipleInstancesPolicy = GetTaskValue(document, taskNamespace, "MultipleInstancesPolicy");
            var logonTrigger = document.Descendants(taskNamespace + "LogonTrigger").FirstOrDefault();
            if (!PathsEqual(command, _executablePath))
            {
                issue = "The helper task points to another TFS executable.";
                return false;
            }

            if (!TextEquals(arguments, _runArguments))
            {
                issue = "The helper task is using outdated launch arguments.";
                return false;
            }

            if (!PathsEqual(workingDirectory, _workingDirectory))
            {
                issue = "The helper task is using another working directory.";
                return false;
            }

            if (!TextEquals(runLevel, "HighestAvailable"))
            {
                issue = "The helper task is not configured to run elevated.";
                return false;
            }

            if (!TextEquals(logonType, "InteractiveToken"))
            {
                issue = "The helper task is not bound to the interactive Windows session.";
                return false;
            }

            if (!TextEquals(hidden, "true"))
            {
                issue = "The helper task is not configured as hidden.";
                return false;
            }

            if (!TextEquals(multipleInstancesPolicy, "IgnoreNew"))
            {
                issue = "The helper task still allows duplicate helper starts.";
                return false;
            }

            if (logonTrigger is not null)
            {
                issue = "The helper task still auto-starts at Windows sign-in.";
                return false;
            }

            issue = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            issue = exception.Message;
            return false;
        }
    }

    private string TryReadTaskXmlFromSchtasks()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments = $"/Query /TN \"{FolderPath}\\{_taskName}\" /XML",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                return string.Empty;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return string.Empty;
            }

            if (process.ExitCode != 0)
            {
                return string.Empty;
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetTaskValue(XContainer document, XNamespace taskNamespace, string elementName)
    {
        return document
            .Descendants(taskNamespace + elementName)
            .Select(node => node.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim()
            ?? string.Empty;
    }

    private static bool TextEquals(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveInteractiveUserId()
    {
        var currentSessionUserId = TryResolveSessionUserId(Process.GetCurrentProcess().SessionId);
        if (!string.IsNullOrWhiteSpace(currentSessionUserId))
        {
            return currentSessionUserId;
        }

        var activeConsoleSessionId = unchecked((int)WTSGetActiveConsoleSessionId());
        var activeConsoleUserId = TryResolveSessionUserId(activeConsoleSessionId);
        if (!string.IsNullOrWhiteSpace(activeConsoleUserId))
        {
            return activeConsoleUserId;
        }

        return WindowsIdentity.GetCurrent().Name;
    }

    private static string TryResolveSessionUserId(int sessionId)
    {
        if (sessionId < 0)
        {
            return string.Empty;
        }

        var userName = QuerySessionValue(sessionId, WtsInfoClass.UserName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return string.Empty;
        }

        var domainName = QuerySessionValue(sessionId, WtsInfoClass.DomainName);
        return string.IsNullOrWhiteSpace(domainName)
            ? userName
            : $"{domainName}\\{userName}";
    }

    private static string QuerySessionValue(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private void ClearRegistrationLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_registrationLogPath)!);
            if (File.Exists(_registrationLogPath))
            {
                File.Delete(_registrationLogPath);
            }
        }
        catch
        {
        }
    }

    private string ReadRegistrationLogOrDefault(string fallback)
    {
        try
        {
            if (!File.Exists(_registrationLogPath))
            {
                return fallback;
            }

            var text = File.ReadAllText(_registrationLogPath).Trim();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
        catch
        {
            return fallback;
        }
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer,
        int sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
