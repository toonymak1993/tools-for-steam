using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed class FpsHelperScheduledTaskService
{
    private const string FolderPath = "\\ToolsForSteam";
    private const string TaskName = "FpsHelper";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskActionExecute = 0;
    private const int TaskTriggerLogon = 9;

    private readonly string _executablePath;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private readonly string _registrationLogPath;

    public FpsHelperScheduledTaskService(string executablePath, string arguments, string workingDirectory)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        _registrationLogPath = Path.Combine(_workingDirectory, "data", "fps-helper-task.log");
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
                    ? "The elevated TFS FPS helper task is not installed yet."
                    : $"The elevated TFS FPS helper needs repair. {compatibilityIssue}".Trim();
                return false;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments = $"/Run /TN \"{FolderPath}\\{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                errorText = "The elevated TFS FPS helper task could not be started.";
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

                errorText = "The elevated TFS FPS helper task start timed out.";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
            {
                errorText = !string.IsNullOrWhiteSpace(error)
                    ? error
                    : !string.IsNullOrWhiteSpace(output)
                        ? output
                        : $"The elevated TFS FPS helper task exited with code {process.ExitCode}.";
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

    public bool TryEnsureRegistered(out string errorText, out bool cancelledByUser)
    {
        cancelledByUser = false;

        try
        {
            if (IsRegistered())
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
                Arguments = SteamLoaderRuntime.RegisterFpsHelperTaskArgument,
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
                if (IsRegistered())
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

            if (process.HasExited && process.ExitCode == 0 && IsRegistered())
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

                errorText = ReadRegistrationLogOrDefault("The Windows admin prompt for the elevated TFS FPS helper timed out.");
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
            errorText = "The Windows admin prompt for the elevated TFS FPS helper was cancelled.";
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
            throw new InvalidOperationException("Admin rights are required to register the elevated TFS FPS helper task.");
        }

        dynamic service = CreateSchedulerService();
        dynamic folder = EnsureFolder(service);
        dynamic definition = service.NewTask(0);

        definition.RegistrationInfo.Description = "Launches the Tools for Steam FPS helper with elevation.";
        definition.Settings.Enabled = true;
        definition.Settings.Hidden = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
        definition.Settings.ExecutionTimeLimit = "PT0S";

        definition.Principal.UserId = ResolveInteractiveUserId();
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = TaskRunLevelHighest;

        dynamic action = definition.Actions.Create(TaskActionExecute);
        action.Path = _executablePath;
        action.Arguments = _arguments;
        action.WorkingDirectory = _workingDirectory;

        dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
        trigger.Enabled = true;
        trigger.UserId = ResolveInteractiveUserId();
        trigger.StartBoundary = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        folder.RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreateOrUpdate,
            Type.Missing,
            Type.Missing,
            TaskLogonInteractiveToken,
            Type.Missing);

        if (!IsRegistered())
        {
            throw new InvalidOperationException("Windows created the elevated TFS FPS helper task, but it does not match this installation.");
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

    private static dynamic? TryGetTask(dynamic service)
    {
        try
        {
            dynamic folder = service.GetFolder(FolderPath);
            return folder.GetTask(TaskName);
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetRegisteredTaskCompatibilityIssue(out string issue)
    {
        var xml = TryReadTaskXmlFromSchtasks();
        if (string.IsNullOrWhiteSpace(xml))
        {
            issue = string.Empty;
            return false;
        }

        return IsCompatibleTaskXml(xml, out issue);
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

            if (!TextEquals(arguments, _arguments))
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

            if (logonTrigger is null)
            {
                issue = "The helper task does not start automatically when Windows signs in.";
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

    private static string TryReadTaskXmlFromSchtasks()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments = $"/Query /TN \"{FolderPath}\\{TaskName}\" /XML",
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

    private static bool IdentitiesMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = NormalizeIdentity(left);
        var normalizedRight = NormalizeIdentity(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentity(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        try
        {
            return ((SecurityIdentifier)new NTAccount(trimmed).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch
        {
            return trimmed;
        }
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
