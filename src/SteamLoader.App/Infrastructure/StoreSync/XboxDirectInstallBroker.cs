using System.Diagnostics;
using System.Text;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record XboxDirectInstallResult(
    bool Accepted,
    string Reason,
    int ExitCode)
{
    public bool IsMachineIncompatible => ExitCode is 5 or 10;
}

/// <summary>
/// Queues an Xbox product with Windows' Store installation broker.
///
/// The AppInstallItem returned by StartProductInstallAsync is scoped to this
/// broker process. It is deliberately used only as an acknowledgement: Windows
/// can replace or invalidate that object while Gaming Services keeps the
/// download alive. Progress is therefore observed separately through
/// XboxInstallEventTracker.
/// </summary>
internal static class XboxDirectInstallBroker
{
    private const string ProductIdEnvironmentVariable = "TFS_XBOX_PRODUCT_ID";
    private const string AcceptedMessage = "TFS_XBOX_ACCEPTED";
    private static readonly TimeSpan AcceptanceTimeout = TimeSpan.FromSeconds(18);

    private const string BrokerScript = """
        $ErrorActionPreference = 'Stop'
        $productId = [Environment]::GetEnvironmentVariable('TFS_XBOX_PRODUCT_ID', 'Process')
        if ([string]::IsNullOrWhiteSpace($productId) -or $productId -notmatch '^[A-Za-z0-9_.-]+$') {
            exit 64
        }

        try {
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $apiInformation = [Windows.Foundation.Metadata.ApiInformation, Windows, ContentType = WindowsRuntime]
            if (-not $apiInformation::IsTypePresent('Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager')) {
                exit 10
            }

            $managerType = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager, Windows, ContentType = WindowsRuntime]
            $optionsType = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallOptions, Windows, ContentType = WindowsRuntime]
            $manager = [Activator]::CreateInstance($managerType)
            $options = [Activator]::CreateInstance($optionsType)
            if ($null -eq $manager -or $null -eq $options) {
                exit 10
            }

            $operation = $manager.StartProductInstallAsync(
                $productId,
                '',
                'ToolsForSteam',
                '',
                $options)
            if ($null -eq $operation) {
                exit 12
            }

            $itemType = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem, Windows, ContentType = WindowsRuntime]
            $genericListType = [System.Collections.Generic.IReadOnlyList[string]].GetGenericTypeDefinition()
            $resultType = $genericListType.MakeGenericType([Type[]]@($itemType))
            $asTaskDefinition = [System.WindowsRuntimeSystemExtensions].GetMethods() |
                Where-Object {
                    if ($_.Name -ne 'AsTask' -or
                        -not $_.IsGenericMethodDefinition -or
                        $_.GetGenericArguments().Length -ne 1 -or
                        $_.GetParameters().Length -ne 1) {
                        return $false
                    }

                    $parameterType = $_.GetParameters()[0].ParameterType
                    return $parameterType.IsGenericType -and
                        $parameterType.GetGenericTypeDefinition().FullName -eq 'Windows.Foundation.IAsyncOperation`1'
                } |
                Select-Object -First 1
            if ($null -eq $asTaskDefinition) {
                exit 10
            }

            $closedAsTask = $asTaskDefinition.MakeGenericMethod([Type[]]@($resultType))
            $arguments = New-Object object[] 1
            $arguments[0] = $operation
            $task = $closedAsTask.Invoke($null, $arguments)
            if ($null -eq $task) {
                exit 12
            }

            $deadline = [DateTime]::UtcNow.AddSeconds(12)
            while (-not $task.IsCompleted -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }

            if (-not $task.IsCompleted) {
                exit 13
            }

            $items = @($task.GetAwaiter().GetResult() | Where-Object { $null -ne $_ })
            if ($items.Count -eq 0) {
                exit 12
            }

            # Do not poll AppInstallItem here. The object is tied to this
            # projection host and may be invalidated immediately while the
            # persistent Windows installation queue continues normally.
            Write-Output 'TFS_XBOX_ACCEPTED'
            exit 0
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) {
                $exception = $exception.InnerException
            }
            [Console]::Error.WriteLine($exception.Message)
            if ($exception -is [System.TypeLoadException]) {
                exit 10
            }
            if ($exception -is [System.UnauthorizedAccessException] -or
                $exception.HResult -eq -2147024891) {
                exit 5
            }
            exit 12
        }
        """;

    public static XboxDirectInstallResult TryQueue(string productId)
    {
        if (!IsSafeProductId(productId))
        {
            return new XboxDirectInstallResult(
                false,
                "The Xbox product ID is invalid.",
                64);
        }

        var powerShellPath = ResolveWindowsPowerShellPath();
        if (string.IsNullOrWhiteSpace(powerShellPath))
        {
            return new XboxDirectInstallResult(
                false,
                "The Windows installation broker is unavailable.",
                10);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(BrokerScript)));
        startInfo.Environment[ProductIdEnvironmentVariable] = productId.Trim();

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new XboxDirectInstallResult(
                    false,
                    "The Windows installation broker could not be started.",
                    10);
            }

            WriteLog(productId, $"broker started pid={process.Id}");
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            try
            {
                process.WaitForExitAsync()
                    .WaitAsync(AcceptanceTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException)
            {
                TryStop(process);
                WriteLog(productId, "acceptance timed out");
                return new XboxDirectInstallResult(
                    false,
                    "Windows did not answer the direct installation request in time.",
                    13);
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult().Trim();
            var accepted = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), AcceptedMessage, StringComparison.Ordinal));
            if (accepted)
            {
                WriteLog(productId, $"installation accepted code={process.ExitCode}");
                return new XboxDirectInstallResult(
                    true,
                    "Windows accepted the Xbox installation.",
                    process.ExitCode);
            }

            WriteLog(
                productId,
                $"broker rejected request code={process.ExitCode}" +
                (string.IsNullOrWhiteSpace(error) ? string.Empty : $" error={error}"));
            return process.ExitCode switch
            {
                5 => new XboxDirectInstallResult(
                    false,
                    "Windows did not grant the private Store installation capability.",
                    5),
                10 => new XboxDirectInstallResult(
                    false,
                    "The Windows installation broker is unavailable.",
                    10),
                13 => new XboxDirectInstallResult(
                    false,
                    "Windows did not answer the direct installation request in time.",
                    13),
                _ => new XboxDirectInstallResult(
                    false,
                    "Windows did not accept the direct installation request.",
                    process.ExitCode),
            };
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Xbox direct install broker failed: {exception}");
            WriteLog(productId, $"broker exception: {exception.Message}");
            return new XboxDirectInstallResult(
                false,
                "The Windows installation broker is unavailable.",
                10);
        }
    }

    private static bool IsSafeProductId(string productId)
    {
        return !string.IsNullOrWhiteSpace(productId) &&
               productId.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }

    internal static void WriteLog(string productId, string message)
    {
        try
        {
            var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            File.AppendAllText(
                Path.Combine(dataDirectory, "omnilibrary-xbox-download.log"),
                $"{DateTimeOffset.Now:O} product={productId} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string ResolveWindowsPowerShellPath()
    {
        try
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var candidate = Path.Combine(
                systemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            return File.Exists(candidate) ? candidate : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
        }
    }
}
