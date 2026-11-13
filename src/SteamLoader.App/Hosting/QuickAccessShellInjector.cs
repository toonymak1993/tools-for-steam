using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Services;
using System.Text.Json;

namespace SteamLoader.App.Hosting;

/// <summary>
/// Attaches Tools for Steam to Steam's CEF surfaces the way Decky Loader does:
/// one persistent DevTools connection per target, re-injection driven by page
/// events (DOMContentLoaded / navigation / disconnect) instead of a polling
/// loop that reconnects to every target, and a tiny loader that pulls the real
/// bundle from the local API server. The old "evaluate the whole bundle"
/// path stays as an automatic fallback when the loader cannot reach the server.
/// </summary>
public sealed class QuickAccessShellInjector
{
    private static readonly TimeSpan TargetScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VersionVerifyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InjectionTimeout = TimeSpan.FromSeconds(30);

    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SteamLoaderHostState _hostState;
    private readonly SteamClientLaunchService _steamClientLaunchService;
    private readonly FrontendBundleRegistry _bundles;
    private readonly FrontendBundle _sharedBundle;
    private readonly FrontendBundle _popupBundle;
    private readonly FrontendBundle _themeSurfaceBundle;
    private readonly FrontendBundle? _menuSurfaceBundle;
    private readonly FrontendBundle? _storeBundle;
    // Further feature modules (Theme Store, ...) that run on top of tfs-core.
    private readonly IReadOnlyList<FrontendBundle> _sharedModuleBundles;
    private readonly FrontendBundle? _coreBundle;
    private readonly Dictionary<string, TargetAttachment> _attachments = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Action<string>? _diagnosticLog;

    public QuickAccessShellInjector(
        SteamDevToolsClient devToolsClient,
        FrontendBundleRegistry bundles,
        SteamClientLaunchService steamClientLaunchService,
        FrontendBundle sharedBundle,
        FrontendBundle popupBundle,
        FrontendBundle themeSurfaceBundle,
        FrontendBundle? coreBundle,
        SteamLoaderHostState hostState,
        Action<string>? diagnosticLog = null,
        FrontendBundle? menuSurfaceBundle = null,
        FrontendBundle? storeBundle = null,
        IReadOnlyList<FrontendBundle>? sharedModuleBundles = null)
    {
        _sharedModuleBundles = sharedModuleBundles ?? [];
        _devToolsClient = devToolsClient;
        _bundles = bundles;
        _steamClientLaunchService = steamClientLaunchService;
        _sharedBundle = sharedBundle;
        _popupBundle = popupBundle;
        _themeSurfaceBundle = themeSurfaceBundle;
        _coreBundle = coreBundle;
        _menuSurfaceBundle = menuSurfaceBundle;
        _storeBundle = storeBundle;
        _hostState = hostState;
        _diagnosticLog = diagnosticLog;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
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
                    await DetachAllAsync($"Injector error: {exception.Message}");
                    _hostState.UpdateError($"Injector error: {exception.Message}");
                }

                // Page events wake the loop immediately; otherwise only the cheap
                // HTTP target list is checked on this cadence.
                await _wake.WaitAsync(TargetScanInterval, cancellationToken);
                while (_wake.CurrentCount > 0)
                {
                    await _wake.WaitAsync(0, cancellationToken);
                }
            }
        }
        finally
        {
            await DetachAllAsync("Injector stopped.");
        }
    }

    private async Task EnsureInjectedAsync(CancellationToken cancellationToken)
    {
        var launchState = await _steamClientLaunchService.EnsureDevToolsReadyAsync(cancellationToken);
        _hostState.UpdateSteamStartup(launchState.StartupStage, launchState.Message);
        if (!launchState.DevToolsReady)
        {
            _hostState.UpdateSteamUiState(launchState.StartupStage switch
            {
                SteamClientStartupStage.Updating => SteamUiState.Updating,
                SteamClientStartupStage.Failed => SteamUiState.Error,
                _ => SteamUiState.Starting
            });
            await DetachAllAsync(launchState.Message);
            return;
        }

        var targets = await _devToolsClient.GetTargetsAsync(cancellationToken);
        _hostState.UpdateSteamUiState(SteamUiStateClassifier.Classify(targets));

        var desired = BuildDesiredAttachments(targets);
        foreach (var staleId in _attachments.Keys.Where(id => !desired.ContainsKey(id)).ToArray())
        {
            await DetachAsync(staleId);
        }

        foreach (var (targetId, plan) in desired)
        {
            // While Steam starts, SharedJSContext briefly looks like the Big
            // Picture window (index.html URL, no title yet). Its role must follow
            // the target once Steam names it, or the shared modules skip themselves.
            if (_attachments.TryGetValue(targetId, out var existing) &&
                (!existing.Session.IsOpen ||
                 !string.Equals(existing.Role, plan.Role, StringComparison.Ordinal) ||
                 !string.Equals(existing.Session.Target.WebSocketDebuggerUrl, plan.Target.WebSocketDebuggerUrl, StringComparison.Ordinal)))
            {
                await DetachAsync(targetId);
                existing = null;
            }

            if (existing is null)
            {
                existing = await TryAttachAsync(plan, cancellationToken);
                if (existing is null)
                {
                    continue;
                }
            }

            existing.Bundles = plan.Bundles;
            await EnsureTargetCurrentAsync(existing, cancellationToken);
        }

        PublishHostState();
    }

    private Dictionary<string, AttachmentPlan> BuildDesiredAttachments(IReadOnlyList<SteamDevToolsTarget> targets)
    {
        var desired = new Dictionary<string, AttachmentPlan>(StringComparer.Ordinal);

        void Add(SteamDevToolsTarget? target, FrontendBundle bundle)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
            {
                return;
            }

            if (!desired.TryGetValue(target.Id, out var plan))
            {
                plan = new AttachmentPlan(target, [], SteamDevToolsClient.ClassifySurfaceRole(target));
                desired[target.Id] = plan;
            }

            if (!plan.Bundles.Contains(bundle))
            {
                plan.Bundles.Add(bundle);
            }
        }

        var sharedTarget = SteamDevToolsClient.FindSharedJsContextTarget(targets);
        if (_coreBundle is not null)
        {
            // The Decky-style core lives where Steam's React runs: the shared
            // context renders the Quick Access Menu and the Gamepad UI router.
            Add(sharedTarget, _coreBundle);
            if (_storeBundle is not null)
            {
                // Native Store pages register their routes on top of tfs-core.
                Add(sharedTarget, _storeBundle);
            }

            foreach (var moduleBundle in _sharedModuleBundles)
            {
                Add(sharedTarget, moduleBundle);
            }
        }

        Add(sharedTarget, _sharedBundle);
        foreach (var themeTarget in SteamDevToolsClient.FindThemeSurfaceTargets(targets))
        {
            // Steam's menu popups only need theming; the library, game-page and
            // overlay surfaces would just poll and observe there for nothing.
            var isMenu = SteamDevToolsClient.ClassifySurfaceRole(themeTarget) == "menu";
            Add(themeTarget, isMenu && _menuSurfaceBundle is not null ? _menuSurfaceBundle : _themeSurfaceBundle);
        }

        Add(SteamDevToolsClient.FindQuickAccessTarget(targets), _popupBundle);
        return desired;
    }

    private async Task<TargetAttachment?> TryAttachAsync(AttachmentPlan plan, CancellationToken cancellationToken)
    {
        SteamCdpSession session;
        try
        {
            session = await SteamCdpSession.ConnectAsync(plan.Target, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log($"attach failed target={plan.Target.Title} error={exception.Message}");
            return null;
        }

        var attachment = new TargetAttachment(session) { Bundles = plan.Bundles, Role = plan.Role };
        session.EventReceived += (method, parameters) => OnPageEvent(attachment, method, parameters);
        _ = session.Closed.ContinueWith(
            _ =>
            {
                attachment.MarkStale();
                SignalWake();
            },
            TaskScheduler.Default);

        try
        {
            await session.SendAsync("Page.enable", null, cancellationToken, TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync();
            throw;
        }
        catch (Exception exception)
        {
            // Without page events we still inject now and rely on the version check.
            Log($"Page.enable failed target={plan.Target.Title} error={exception.Message}");
        }

        _attachments[plan.Target.Id] = attachment;
        Log($"attached target={plan.Target.Title} role={plan.Role} bundles={string.Join(",", plan.Bundles.Select(bundle => bundle.Id))}");
        return attachment;
    }

    private void OnPageEvent(TargetAttachment attachment, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Page.domContentEventFired":
                attachment.RequestInjection();
                SignalWake();
                break;
            case "Page.frameNavigated" when IsMainFrameNavigation(parameters):
                attachment.RequestInjection();
                SignalWake();
                break;
            case "Inspector.detached":
            case "Inspector.targetCrashed":
                attachment.MarkStale();
                SignalWake();
                break;
        }
    }

    internal static bool IsMainFrameNavigation(JsonElement parameters)
    {
        return parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("frame", out var frame) &&
            frame.ValueKind == JsonValueKind.Object &&
            !frame.TryGetProperty("parentId", out _);
    }

    private async Task EnsureTargetCurrentAsync(TargetAttachment attachment, CancellationToken cancellationToken)
    {
        var verifyDue = DateTimeOffset.UtcNow - attachment.LastVerifiedAt >= VersionVerifyInterval;
        if (!attachment.ConsumeInjectionRequest() && !verifyDue)
        {
            return;
        }

        // Bundles that ran under another surface role (Steam startup race)
        // skipped their role-bound modules; run all of them again once.
        var roleCurrent = await IsRoleCurrentAsync(attachment, cancellationToken);
        if (!roleCurrent)
        {
            Log($"surface role changed target={attachment.Session.Target.Title} role={attachment.Role}; re-injecting");
        }

        foreach (var bundle in attachment.Bundles)
        {
            // One cheap probe over the open socket. Bundles also guard against
            // double installs themselves, but skipping a current bundle avoids
            // re-running large scripts on harmless page events.
            if (roleCurrent && await IsBundleCurrentAsync(attachment.Session, bundle, cancellationToken))
            {
                attachment.Loaded[bundle.Id] = true;
                continue;
            }

            attachment.Loaded[bundle.Id] = await InjectBundleAsync(attachment.Session, bundle, attachment.Role, cancellationToken);
        }

        attachment.LastVerifiedAt = DateTimeOffset.UtcNow;
    }

    private async Task<bool> IsBundleCurrentAsync(
        SteamCdpSession session,
        FrontendBundle bundle,
        CancellationToken cancellationToken)
    {
        var expression = BuildVersionCheckExpression(bundle);
        var result = await session.EvaluateAsync(expression, false, cancellationToken, TimeSpan.FromSeconds(5));
        return result.Success && IsCurrentScriptVersionValue(result.Value);
    }

    private static async Task<bool> IsRoleCurrentAsync(TargetAttachment attachment, CancellationToken cancellationToken)
    {
        var result = await attachment.Session.EvaluateAsync(
            BuildRoleCheckExpression(attachment.Role),
            false,
            cancellationToken,
            TimeSpan.FromSeconds(5));
        // A fresh page has no role yet; only a different, set role forces a re-run.
        return !result.Success || IsCurrentScriptVersionValue(result.Value);
    }

    internal static string BuildRoleCheckExpression(string role)
    {
        return $"!window.__tfsSurfaceRole || window.__tfsSurfaceRole === {JsonSerializer.Serialize(role)}";
    }

    private async Task<bool> InjectBundleAsync(
        SteamCdpSession session,
        FrontendBundle bundle,
        string role,
        CancellationToken cancellationToken)
    {
        var loaderResult = await session.EvaluateAsync(
            BuildLoaderScript(bundle, _bundles.ApiBaseUri, _bundles.ApiSessionToken, role),
            awaitPromise: true,
            cancellationToken,
            InjectionTimeout);

        var value = loaderResult.Value?.ToString();
        if (loaderResult.Success && IsInjectedValue(value))
        {
            return true;
        }

        if (loaderResult.Success && value?.StartsWith(LoaderFetchFailedPrefix, StringComparison.Ordinal) == true &&
            !bundle.IsModule)
        {
            // The API server was not reachable from the page (startup race,
            // firewall product). Fall back to shipping the bundle through the
            // debugger, exactly like before.
            Log($"loader fetch failed bundle={bundle.Id} target={session.Target.Title} detail={value}; using inline fallback");
            var inlineResult = await session.EvaluateAsync(
                BuildInlineScript(bundle, _bundles.ApiBaseUri, _bundles.ApiSessionToken, role),
                awaitPromise: false,
                cancellationToken,
                InjectionTimeout);
            if (inlineResult.Success && IsInjectedValue(inlineResult.Value?.ToString()))
            {
                return true;
            }

            _hostState.UpdateError($"Injection failed: {inlineResult.ErrorMessage ?? inlineResult.Value?.ToString()}");
            return false;
        }

        var detail = loaderResult.ErrorMessage ?? value ?? "no result";
        Log($"inject failed bundle={bundle.Id} target={session.Target.Title} detail={detail}");
        _hostState.UpdateError($"Injection failed ({bundle.Id}): {detail}");
        return false;
    }

    internal const string LoaderFetchFailedPrefix = "tfs-fetch-failed:";
    internal const string LoaderModuleFailedPrefix = "tfs-module-failed:";

    internal static bool IsInjectedValue(string? value)
    {
        return string.Equals(value, "injected", StringComparison.Ordinal) ||
            string.Equals(value, "waiting", StringComparison.Ordinal);
    }

    internal static string BuildVersionCheckExpression(FrontendBundle bundle)
    {
        return $"window[{JsonSerializer.Serialize(bundle.VersionProperty)}] === {JsonSerializer.Serialize(bundle.Version)}";
    }

    internal static string BuildSessionBootstrap(Uri apiBaseUri, string apiSessionToken, string? role = null)
    {
        var apiBase = apiBaseUri.ToString();
        var roleJson = JsonSerializer.Serialize(role ?? "page");
        return $$"""
            (() => {
              window.__tfsSurfaceRole = {{roleJson}};
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
    }

    /// <summary>
    /// The only code sent through the debugger on the normal path: session
    /// globals, then the bundle fetched from the API server. Classic bundles are
    /// evaluated in global scope (indirect eval) so they behave exactly like the
    /// former inline evaluation; modules are loaded with import().
    /// </summary>
    internal static string BuildLoaderScript(FrontendBundle bundle, Uri apiBaseUri, string apiSessionToken, string? role = null)
    {
        var url = new Uri(apiBaseUri, FrontendBundleRegistry.GetRelativeUrl(bundle)).ToString();
        var urlJson = JsonSerializer.Serialize(url);
        var versionPropertyJson = JsonSerializer.Serialize(bundle.VersionProperty);
        var versionJson = JsonSerializer.Serialize(bundle.Version);
        var bootstrap = BuildSessionBootstrap(apiBaseUri, apiSessionToken, role);
        // Keep a strong reference to the pending promise: CEF otherwise may
        // collect it before Runtime.evaluate(awaitPromise) gets the result.
        var runKeyJson = JsonSerializer.Serialize(bundle.FileName);

        if (bundle.IsModule)
        {
            return $$"""
                (window.__tfsLoaderRuns ??= {})[{{runKeyJson}}] = (async () => {
                {{bootstrap}}
                  try {
                    const module = await import({{urlJson}});
                    if (typeof module?.start === "function") {
                      await module.start({ apiBase: window.__steamLoaderApiBase, version: {{versionJson}} });
                    }
                    window[{{versionPropertyJson}}] = {{versionJson}};
                    return "injected";
                  } catch (error) {
                    return {{JsonSerializer.Serialize(LoaderModuleFailedPrefix)}} + String(error?.stack || error).slice(0, 600);
                  }
                })()
                """;
        }

        return $$"""
            (window.__tfsLoaderRuns ??= {})[{{runKeyJson}}] = (async () => {
            {{bootstrap}}
              let source;
              try {
                const nativeFetch = window.__steamLoaderNativeFetch || window.fetch.bind(window);
                const response = await nativeFetch({{urlJson}}, { credentials: "omit" });
                if (!response.ok) {
                  return {{JsonSerializer.Serialize(LoaderFetchFailedPrefix)}} + response.status;
                }
                source = await response.text();
              } catch (error) {
                return {{JsonSerializer.Serialize(LoaderFetchFailedPrefix)}} + String(error?.message || error);
              }
              return (0, eval)(source + "\n//# sourceURL=" + {{urlJson}});
            })()
            """;
    }

    /// <summary>Former behaviour: session globals plus the whole bundle in one evaluation.</summary>
    internal static string BuildInlineScript(FrontendBundle bundle, Uri apiBaseUri, string apiSessionToken, string? role = null)
    {
        return string.Join(
            Environment.NewLine,
            BuildSessionBootstrap(apiBaseUri, apiSessionToken, role),
            bundle.Body);
    }

    internal static bool IsCurrentScriptVersionValue(object? value)
    {
        return SteamDevToolsClient.TryReadBoolean(value, out var isCurrent) && isCurrent;
    }

    private void PublishHostState()
    {
        var shared = _attachments.Values.FirstOrDefault(attachment =>
            attachment.Bundles.Contains(_sharedBundle));
        if (shared is null)
        {
            _hostState.UpdateSharedContext(false, "Waiting for Steam SharedJSContext.");
        }
        else if (shared.Loaded.TryGetValue(_sharedBundle.Id, out var sharedLoaded) && sharedLoaded)
        {
            _hostState.UpdateSharedContext(true, "SharedJSContext attached.");
        }

        var popup = _attachments.Values.FirstOrDefault(attachment =>
            attachment.Bundles.Contains(_popupBundle));
        if (popup is null)
        {
            _hostState.UpdateQuickAccess(false, "Waiting for the Quick Access popup.");
        }
        else if (popup.Loaded.TryGetValue(_popupBundle.Id, out var popupLoaded) && popupLoaded)
        {
            _hostState.UpdateQuickAccess(true, "Quick Access attached.");
        }
    }

    private void SignalWake()
    {
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task DetachAsync(string targetId)
    {
        if (!_attachments.Remove(targetId, out var attachment))
        {
            return;
        }

        Log($"detached target={attachment.Session.Target.Title}");
        await attachment.Session.DisposeAsync();
    }

    private async Task DetachAllAsync(string message)
    {
        foreach (var targetId in _attachments.Keys.ToArray())
        {
            await DetachAsync(targetId);
        }

        _hostState.UpdateSharedContext(false, message);
        _hostState.UpdateQuickAccess(false, message);
    }

    private void Log(string message)
    {
        try
        {
            _diagnosticLog?.Invoke(message);
        }
        catch
        {
        }
    }

    private sealed record AttachmentPlan(SteamDevToolsTarget Target, List<FrontendBundle> Bundles, string Role);

    private sealed class TargetAttachment(SteamCdpSession session)
    {
        private int _injectionRequested = 1;

        public SteamCdpSession Session { get; } = session;

        public List<FrontendBundle> Bundles { get; set; } = [];

        public string Role { get; init; } = "page";

        public Dictionary<string, bool> Loaded { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastVerifiedAt { get; set; } = DateTimeOffset.MinValue;

        public void RequestInjection()
        {
            Interlocked.Exchange(ref _injectionRequested, 1);
        }

        public bool ConsumeInjectionRequest()
        {
            return Interlocked.Exchange(ref _injectionRequested, 0) == 1;
        }

        public void MarkStale()
        {
            Interlocked.Exchange(ref _injectionRequested, 1);
        }
    }
}
