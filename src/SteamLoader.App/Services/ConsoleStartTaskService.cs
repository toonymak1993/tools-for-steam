using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace SteamLoader.App.Services;

/// <summary>
/// The per-user Windows sign-in task that starts Console Mode.
/// </summary>
internal interface IConsoleStartTask
{
    bool IsRegistered();

    /// <summary>
    /// The task exists for this executable but Windows has it disabled
    /// (for example by an older TFS autostart cleanup).
    /// </summary>
    bool IsDisabledForThisExecutable();

    void Register();

    void Unregister();
}

/// <summary>
/// Console Mode keeps Explorer as the Windows shell. TFS starts from an
/// ordinary, visible Task Scheduler sign-in task of the current user instead:
/// it runs as soon as the user signs in (earlier than the Run key, which
/// Explorer delays), needs no admin rights and leaves Winlogon untouched.
/// </summary>
internal sealed class ConsoleStartTaskService : IConsoleStartTask
{
    private const string FolderPath = "\\";
    private const string TaskNamePrefix = "Tools for Steam Console Start";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLeastPrivilege = 0;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskActionExecute = 0;
    private const int TaskTriggerLogon = 9;
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);

    // Task Scheduler priority 4 is NORMAL_PRIORITY_CLASS. The default (7) is
    // below normal, and Steam plus every game started through TFS would
    // inherit that priority class from the TFS process.
    internal const int NormalTaskPriority = 4;

    private readonly string _executablePath;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private readonly string _userId;

    public ConsoleStartTaskService(string executablePath)
        : this(
            executablePath,
            SteamLoaderRuntime.ConsoleLaunchArguments,
            WindowsIdentity.GetCurrent().Name)
    {
    }

    internal ConsoleStartTaskService(string executablePath, string arguments, string userId)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        _userId = userId;
        TaskName = BuildTaskName(userId);
    }

    public string TaskName { get; }

    public bool IsRegistered()
    {
        var xml = TryReadTaskXml();
        return xml is not null && IsCompatibleTaskXml(xml, _executablePath, _arguments, out _);
    }

    public bool IsDisabledForThisExecutable()
    {
        var xml = TryReadTaskXml();
        return xml is not null && IsDisabledTaskXmlForExecutable(xml, _executablePath);
    }

    internal static bool IsDisabledTaskXmlForExecutable(string xml, string executablePath)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null)
            {
                return false;
            }

            var ns = root.Name.Namespace;
            var command = root.Descendants(ns + "Exec").FirstOrDefault()?.Element(ns + "Command")?.Value;
            var enabled = root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            return PathsEqual(command, executablePath) && TextEquals(enabled, "false");
        }
        catch
        {
            return false;
        }
    }

    public void Register()
    {
        object? serviceObject = null;
        object? folderObject = null;
        object? definitionObject = null;

        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            folderObject = service.GetFolder(FolderPath);
            dynamic folder = folderObject;
            definitionObject = service.NewTask(0);
            dynamic definition = definitionObject;

            definition.RegistrationInfo.Author = SteamLoaderRuntime.ProductName;
            definition.RegistrationInfo.Description =
                "Starts Tools for Steam Console Mode when you sign in to Windows. " +
                "Explorer stays the Windows shell; switch the startup mode in Tools for Steam to remove this task.";

            definition.Settings.Enabled = true;
            definition.Settings.Hidden = false;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.StartWhenAvailable = false;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.Priority = NormalTaskPriority;
            definition.Settings.IdleSettings.StopOnIdleEnd = false;

            dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
            trigger.UserId = _userId;
            trigger.Enabled = true;

            definition.Principal.UserId = _userId;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = TaskRunLevelLeastPrivilege;

            dynamic action = definition.Actions.Create(TaskActionExecute);
            action.Path = _executablePath;
            action.Arguments = _arguments;
            action.WorkingDirectory = _workingDirectory;

            folder.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreateOrUpdate,
                Type.Missing,
                Type.Missing,
                TaskLogonInteractiveToken,
                Type.Missing);
        }
        finally
        {
            ReleaseComObject(definitionObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }

        var xml = TryReadTaskXml();
        if (xml is null)
        {
            throw new InvalidOperationException("Windows did not keep the Tools for Steam console start task.");
        }

        if (!IsCompatibleTaskXml(xml, _executablePath, _arguments, out var issue))
        {
            throw new InvalidOperationException($"Windows created the console start task, but it does not match: {issue}");
        }
    }

    public void Unregister()
    {
        object? serviceObject = null;
        object? folderObject = null;

        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            folderObject = service.GetFolder(FolderPath);
            dynamic folder = folderObject;
            folder.DeleteTask(TaskName, 0);
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
        }
        finally
        {
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }

        if (TryReadTaskXml() is not null)
        {
            throw new InvalidOperationException("Windows did not remove the Tools for Steam console start task.");
        }
    }

    internal static string BuildTaskName(string userId)
    {
        var separatorIndex = userId.LastIndexOf('\\');
        var userName = separatorIndex >= 0 ? userId[(separatorIndex + 1)..] : userId;
        var invalidCharacters = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var sanitized = new string(userName
                .Where(character => !invalidCharacters.Contains(character) && !char.IsControl(character))
                .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? TaskNamePrefix
            : $"{TaskNamePrefix} - {sanitized}";
    }

    internal static bool IsCompatibleTaskXml(
        string xml,
        string executablePath,
        string arguments,
        out string issue)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var root = document.Root;
            if (root is null)
            {
                issue = "The task definition is empty.";
                return false;
            }

            var ns = root.Name.Namespace;
            var exec = root.Descendants(ns + "Exec").FirstOrDefault();
            var command = exec?.Element(ns + "Command")?.Value;
            var taskArguments = exec?.Element(ns + "Arguments")?.Value;
            var settings = root.Element(ns + "Settings");
            var principal = root.Descendants(ns + "Principal").FirstOrDefault();
            var logonTrigger = root.Descendants(ns + "LogonTrigger").FirstOrDefault();

            if (!PathsEqual(command, executablePath))
            {
                issue = "The task starts another Tools for Steam executable.";
                return false;
            }

            if (!TextEquals(taskArguments, arguments))
            {
                issue = "The task uses outdated launch arguments.";
                return false;
            }

            if (logonTrigger is null || TextEquals(logonTrigger.Element(ns + "Enabled")?.Value, "false"))
            {
                issue = "The task does not run at Windows sign-in.";
                return false;
            }

            if (TextEquals(settings?.Element(ns + "Enabled")?.Value, "false"))
            {
                issue = "The task is disabled.";
                return false;
            }

            if (!TextEquals(settings?.Element(ns + "Priority")?.Value, NormalTaskPriority.ToString()))
            {
                issue = "The task would start Tools for Steam below normal priority.";
                return false;
            }

            // Missing battery elements mean Task Scheduler's defaults (true):
            // a handheld on battery would then never get its console start.
            if (!TextEquals(settings?.Element(ns + "DisallowStartIfOnBatteries")?.Value, "false") ||
                !TextEquals(settings?.Element(ns + "StopIfGoingOnBatteries")?.Value, "false"))
            {
                issue = "The task would not run on battery power.";
                return false;
            }

            if (!TextEquals(settings?.Element(ns + "ExecutionTimeLimit")?.Value, "PT0S"))
            {
                issue = "The task would stop Tools for Steam after a time limit.";
                return false;
            }

            var runLevel = principal?.Element(ns + "RunLevel")?.Value;
            if (TextEquals(runLevel, "HighestAvailable"))
            {
                issue = "The task must not start Tools for Steam elevated.";
                return false;
            }

            var logonType = principal?.Element(ns + "LogonType")?.Value;
            if (!string.IsNullOrWhiteSpace(logonType) && !TextEquals(logonType, "InteractiveToken"))
            {
                issue = "The task is not bound to the interactive Windows session.";
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

    private string? TryReadTaskXml()
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
            taskObject = folder.GetTask(TaskName);
            dynamic task = taskObject;
            return (string)task.Xml;
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
        finally
        {
            ReleaseComObject(taskObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    private static object CreateSchedulerService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler is not available.");
        var serviceObject = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
        dynamic service = serviceObject;
        service.Connect();
        return serviceObject;
    }

    private static bool IsNotFound(Exception exception)
    {
        return exception.HResult is ErrorFileNotFound or ErrorPathNotFound ||
            exception.InnerException?.HResult is ErrorFileNotFound or ErrorPathNotFound;
    }

    private static bool TextEquals(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(left.Trim().Trim('"'))),
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(right.Trim().Trim('"'))),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
