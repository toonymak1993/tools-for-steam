using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Hosting;

public sealed class QuickAccessShellInjector
{
    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SteamLoaderHostState _hostState;
    private readonly SteamClientLaunchService _steamClientLaunchService;
    private readonly string _sharedScript;
    private readonly string _popupScript;
    private readonly string _themeSurfaceScript;
    private readonly string _sharedScriptVersion;
    private readonly string _popupScriptVersion;
    private readonly string _themeSurfaceScriptVersion;
    private bool _sharedReadyLogged;
    private bool _popupReadyLogged;
    private bool _themeSurfaceReadyLogged;
    private string? _sharedTargetId;
    private string? _quickAccessTargetId;
    private readonly HashSet<string> _themeSurfaceTargetIds = new(StringComparer.Ordinal);

    public QuickAccessShellInjector(
        SteamDevToolsClient devToolsClient,
        Uri apiBaseUri,
        string apiSessionToken,
        SteamClientLaunchService steamClientLaunchService,
        string sharedScriptTemplate,
        string popupScriptTemplate,
        string themeSurfaceScriptTemplate,
        SteamLoaderHostState hostState)
    {
        _devToolsClient = devToolsClient;
        _steamClientLaunchService = steamClientLaunchService;
        _sharedScriptVersion = ComputeScriptVersion($"{sharedScriptTemplate}\n{apiSessionToken}");
        _popupScriptVersion = ComputeScriptVersion($"{popupScriptTemplate}\n{apiSessionToken}");
        _themeSurfaceScriptVersion = ComputeScriptVersion($"{themeSurfaceScriptTemplate}\n{apiSessionToken}");
        _sharedScript = BuildInjectedScript(
            sharedScriptTemplate,
            "__steamLoaderSharedScriptVersion",
            _sharedScriptVersion,
            apiBaseUri,
            apiSessionToken);
        _popupScript = BuildInjectedScript(
            popupScriptTemplate,
            "__steamLoaderPopupScriptVersion",
            _popupScriptVersion,
            apiBaseUri,
            apiSessionToken);
        _themeSurfaceScript = BuildInjectedScript(
            themeSurfaceScriptTemplate,
            "__steamLoaderThemeSurfaceScriptVersion",
            _themeSurfaceScriptVersion,
            apiBaseUri,
            apiSessionToken);
        _hostState = hostState;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureInjectedAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ResetAttachedTargets($"Injector error: {exception.Message}");
                _hostState.UpdateError($"Injector error: {exception.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task EnsureInjectedAsync(CancellationToken cancellationToken)
    {
        var launchState = await _steamClientLaunchService.EnsureDevToolsReadyAsync(cancellationToken);
        if (!launchState.DevToolsReady)
        {
            ResetAttachedTargets(launchState.Message);
            return;
        }

        var targets = await _devToolsClient.GetTargetsAsync(cancellationToken);
        var sharedTarget = SteamDevToolsClient.FindSharedJsContextTarget(targets);
        if (sharedTarget is null)
        {
            _sharedReadyLogged = false;
            _sharedTargetId = null;
            _hostState.UpdateSharedContext(false, "Waiting for Steam SharedJSContext.");
        }
        else if (
            !_sharedReadyLogged ||
            !string.Equals(_sharedTargetId, sharedTarget.Id, StringComparison.Ordinal) ||
            !await IsTargetScriptCurrentAsync(
                sharedTarget,
                "__steamLoaderSharedScriptVersion",
                _sharedScriptVersion,
                cancellationToken))
        {
            _sharedReadyLogged = await InjectIntoTargetAsync(
                sharedTarget,
                _sharedScript,
                "SharedJSContext attached.",
                _sharedReadyLogged,
                (message) => _hostState.UpdateSharedContext(true, message),
                cancellationToken);

            if (_sharedReadyLogged)
            {
                _sharedTargetId = sharedTarget.Id;
            }
        }
        else
        {
            _hostState.UpdateSharedContext(true, "SharedJSContext attached.");
        }

        var themeSurfaceTargets = SteamDevToolsClient.FindThemeSurfaceTargets(targets);
        if (themeSurfaceTargets.Count == 0)
        {
            _themeSurfaceReadyLogged = false;
            _themeSurfaceTargetIds.Clear();
        }
        else
        {
            var activeThemeTargetIds = themeSurfaceTargets
                .Select(target => target.Id)
                .ToHashSet(StringComparer.Ordinal);

            _themeSurfaceTargetIds.RemoveWhere(id => !activeThemeTargetIds.Contains(id));

            foreach (var themeSurfaceTarget in themeSurfaceTargets)
            {
                var requiresInjection =
                    !_themeSurfaceTargetIds.Contains(themeSurfaceTarget.Id) ||
                    !await IsTargetScriptCurrentAsync(
                        themeSurfaceTarget,
                        "__steamLoaderThemeSurfaceScriptVersion",
                        _themeSurfaceScriptVersion,
                        cancellationToken);

                var injected = !requiresInjection
                    ? _themeSurfaceReadyLogged
                    : await InjectIntoTargetAsync(
                        themeSurfaceTarget,
                        _themeSurfaceScript,
                        "Theme surface attached.",
                        _themeSurfaceReadyLogged,
                        (_) => { },
                        cancellationToken);

                if (injected)
                {
                    _themeSurfaceReadyLogged = true;
                    _themeSurfaceTargetIds.Add(themeSurfaceTarget.Id);
                }
            }
        }

        // Prepare the stable full-screen hosts before exposing the Quick Access
        // controls that can open an overlay. This avoids a first-click race where
        // the popup closes before the Store has been injected into the main surface.
        var quickAccessTarget = SteamDevToolsClient.FindQuickAccessTarget(targets);
        if (quickAccessTarget is null)
        {
            _popupReadyLogged = false;
            _quickAccessTargetId = null;
            _hostState.UpdateQuickAccess(false, "Waiting for the Quick Access popup.");
        }
        else if (
            !_popupReadyLogged ||
            !string.Equals(_quickAccessTargetId, quickAccessTarget.Id, StringComparison.Ordinal) ||
            !await IsTargetScriptCurrentAsync(
                quickAccessTarget,
                "__steamLoaderPopupScriptVersion",
                _popupScriptVersion,
                cancellationToken))
        {
            _popupReadyLogged = await InjectIntoTargetAsync(
                quickAccessTarget,
                _popupScript,
                "Quick Access attached.",
                _popupReadyLogged,
                (message) => _hostState.UpdateQuickAccess(true, message),
                cancellationToken);

            if (_popupReadyLogged)
            {
                _quickAccessTargetId = quickAccessTarget.Id;
            }
        }
        else
        {
            _hostState.UpdateQuickAccess(true, "Quick Access attached.");
        }
    }

    private async Task<bool> InjectIntoTargetAsync(
        SteamDevToolsTarget target,
        string script,
        string readyMessage,
        bool readyLogged,
        Action<string> setReadyState,
        CancellationToken cancellationToken)
    {
        var result = await _devToolsClient.EvaluateAsync(
            target.WebSocketDebuggerUrl,
            script,
            cancellationToken);

        if (!result.Success)
        {
            _hostState.UpdateError($"Injection failed: {result.ErrorMessage}");
            return readyLogged;
        }

        if (string.Equals(result.Value?.ToString(), "injected", StringComparison.Ordinal))
        {
            if (!readyLogged)
            {
                setReadyState(readyMessage);
                readyLogged = true;
            }
            else
            {
                setReadyState(readyMessage);
            }
        }

        return readyLogged;
    }

    private static string BuildInjectedScript(
        string scriptTemplate,
        string scriptVersionProperty,
        string scriptVersion,
        Uri apiBaseUri,
        string apiSessionToken)
    {
        var apiBase = apiBaseUri.ToString();
        var authenticatedFetchBootstrap = $$"""
            (() => {
              const apiBase = {{JsonSerializer.Serialize(apiBase)}};
              const apiToken = {{JsonSerializer.Serialize(apiSessionToken)}};
              window.__steamLoaderApiBase = apiBase;
              window.__steamLoaderApiToken = apiToken;
              window.__steamLoaderApiUrl = (path) => {
                const url = new URL(path, apiBase);
                url.searchParams.set("{{LocalApiSession.QueryName}}", apiToken);
                return url.toString();
              };
              if (window.__steamLoaderAuthenticatedFetchToken === apiToken) return;
              const nativeFetch = window.__steamLoaderNativeFetch || window.fetch.bind(window);
              window.__steamLoaderNativeFetch = nativeFetch;
              window.fetch = (input, init = {}) => {
                const requestUrl = typeof input === "string" || input instanceof URL ? String(input) : input?.url || "";
                if (!requestUrl.startsWith(apiBase)) return nativeFetch(input, init);
                const inheritedHeaders = input instanceof Request ? input.headers : undefined;
                const headers = new Headers(init.headers || inheritedHeaders);
                headers.set("{{LocalApiSession.HeaderName}}", apiToken);
                return nativeFetch(input, { ...init, headers });
              };
              window.__steamLoaderAuthenticatedFetchToken = apiToken;
            })();
            """;
        var scriptBody = scriptTemplate.Replace("__STEAMLOADER_API_BASE__", apiBase, StringComparison.Ordinal);
        var script = string.Join(
            Environment.NewLine,
            authenticatedFetchBootstrap,
            scriptBody,
            $"window[{JsonSerializer.Serialize(scriptVersionProperty)}] = {JsonSerializer.Serialize(scriptVersion)};",
            "\"injected\";");
        return script;
    }

    private async Task<bool> IsTargetScriptCurrentAsync(
        SteamDevToolsTarget target,
        string scriptVersionProperty,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var expression = $"window[{JsonSerializer.Serialize(scriptVersionProperty)}] === {JsonSerializer.Serialize(expectedVersion)}";
        var result = await _devToolsClient.EvaluateAsync(
            target.WebSocketDebuggerUrl,
            expression,
            cancellationToken);

        return result.Success && IsCurrentScriptVersionValue(result.Value);
    }

    internal static bool IsCurrentScriptVersionValue(object? value)
    {
        return SteamDevToolsClient.TryReadBoolean(value, out var isCurrent) && isCurrent;
    }

    private static string ComputeScriptVersion(string script)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(script));
        return Convert.ToHexString(hashBytes[..8]);
    }

    private void ResetAttachedTargets(string message)
    {
        _sharedReadyLogged = false;
        _popupReadyLogged = false;
        _themeSurfaceReadyLogged = false;
        _sharedTargetId = null;
        _quickAccessTargetId = null;
        _themeSurfaceTargetIds.Clear();
        _hostState.UpdateSharedContext(false, message);
        _hostState.UpdateQuickAccess(false, message);
    }
}
