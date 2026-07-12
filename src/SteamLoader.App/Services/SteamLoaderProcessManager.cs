using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using SteamLoader.App.Hosting;

namespace SteamLoader.App.Services;

public sealed class SteamLoaderProcessManager
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseUri;
    private readonly string _backgroundArguments;

    public SteamLoaderProcessManager(Uri apiBaseUri, string backgroundArguments)
    {
        _apiBaseUri = apiBaseUri;
        _backgroundArguments = backgroundArguments;
        _httpClient = new HttpClient
        {
            BaseAddress = apiBaseUri,
            Timeout = StatusTimeout
        };
        _httpClient.DefaultRequestHeaders.Add(
            LocalApiSession.HeaderName,
            LocalApiSession.GetOrCreateDefault());
    }

    public Uri ApiBaseUri => _apiBaseUri;

    public string ExecutablePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Unable to resolve the current executable path.");

    public string WorkingDirectory => AppContext.BaseDirectory;

    public async Task<SteamLoaderHostStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/control/status", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SteamLoaderHostStatus>(cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await _httpClient.GetStringAsync("health", cancellationToken);
            return string.Equals(content, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (await IsRunningAsync(cancellationToken))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = _backgroundArguments,
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(startInfo)?.Dispose();
        await WaitUntilAsync(() => IsRunningAsync(cancellationToken), expectedValue: true, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsRunningAsync(cancellationToken))
        {
            return;
        }

        using var response = await _httpClient.PostAsync("api/control/shutdown", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        await WaitUntilAsync(() => IsRunningAsync(cancellationToken), expectedValue: false, cancellationToken);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task RequestStartupSyncAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync("api/store-sync/startup-sync", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> check,
        bool expectedValue,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;

        while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(12))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await check() == expectedValue)
            {
                return;
            }

            await Task.Delay(350, cancellationToken);
        }

        throw new TimeoutException(expectedValue
            ? "SteamLoader did not start in time."
            : "SteamLoader did not stop in time.");
    }
}
