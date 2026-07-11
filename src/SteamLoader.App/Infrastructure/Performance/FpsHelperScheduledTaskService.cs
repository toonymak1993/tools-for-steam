using SteamLoader.App.Infrastructure.Helpers;

namespace SteamLoader.App.Infrastructure.Performance;

internal sealed class FpsHelperScheduledTaskService
{
    private readonly ElevatedHelperTaskService _taskService;

    public FpsHelperScheduledTaskService(string executablePath, string arguments, string workingDirectory)
    {
        _taskService = new ElevatedHelperTaskService(
            taskName: "FpsHelper",
            displayName: "TFS FPS helper",
            taskDescription: "Launches the Tools for Steam FPS helper with elevation on demand.",
            executablePath: executablePath,
            runArguments: arguments,
            registerArguments: SteamLoaderRuntime.RegisterFpsHelperTaskArgument,
            workingDirectory: workingDirectory,
            registrationLogFileName: "fps-helper-task.log");
    }

    public static bool IsCurrentProcessElevated() => ElevatedHelperTaskService.IsCurrentProcessElevated();

    public bool TryRun(out string errorText) => _taskService.TryRun(out errorText);

    public bool IsRegistered() => _taskService.IsRegistered();

    public bool TryEnsureRegistered(out string errorText, out bool cancelledByUser)
        => _taskService.TryEnsureRegistered(out errorText, out cancelledByUser);

    public void EnsureRegistered() => _taskService.EnsureRegistered();
}
