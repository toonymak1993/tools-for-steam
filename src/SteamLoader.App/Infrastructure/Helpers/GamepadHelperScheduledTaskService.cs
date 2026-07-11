namespace SteamLoader.App.Infrastructure.Helpers;

internal sealed class GamepadHelperScheduledTaskService
{
    private readonly ElevatedHelperTaskService _taskService;

    public GamepadHelperScheduledTaskService(string executablePath, string workingDirectory)
    {
        _taskService = new ElevatedHelperTaskService(
            taskName: "GamepadHelper",
            displayName: "TFS Xbox Mode helper",
            taskDescription: "Launches the Tools for Steam Xbox Mode helper with elevation on demand.",
            executablePath: executablePath,
            runArguments: SteamLoaderRuntime.GamepadHelperArgument,
            registerArguments: SteamLoaderRuntime.RegisterGamepadHelperTaskArgument,
            workingDirectory: workingDirectory,
            registrationLogFileName: "gamepad-helper-task.log");
    }

    public bool TryRun(out string errorText) => _taskService.TryRun(out errorText);

    public bool IsRegistered() => _taskService.IsRegistered();

    public bool IsRunning() => _taskService.IsRunning();

    public bool TryEnsureRegistered(out string errorText, out bool cancelledByUser)
        => _taskService.TryEnsureRegistered(out errorText, out cancelledByUser);

    public void EnsureRegistered() => _taskService.EnsureRegistered();
}
