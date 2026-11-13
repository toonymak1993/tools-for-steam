using System.Text.Json;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Infrastructure.Discord;

/// <summary>
/// Backend half of Discord Web in Big Picture. The page itself is a Steam
/// BrowserView created by tfs-discord.mjs (Steam's own browser host: native
/// mouse action set, Steam keyboard). Steam offers no way to send input into
/// a BrowserView from its UI, so the controller extras (A = click at the
/// cursor, B = close Discord's popups, mute, server/channel switching) arrive
/// here and are sent over the page's DevTools socket as trusted input.
/// Nothing here reads messages, tokens or passwords: the page helper only
/// reports the cursor position, open popups and the voice switches.
/// </summary>
public sealed class DiscordWebBridge : IAsyncDisposable
{
    private const int CdpAlt = 1;
    private const int CdpCtrl = 2;
    private const int CdpShift = 8;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);

    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _log;
    private SteamCdpSession? _session;
    private bool _permissionsRequested;

    public DiscordWebBridge(SteamDevToolsClient devToolsClient, Action<string>? log = null)
    {
        _devToolsClient = devToolsClient;
        _log = log;
    }

    /// <summary>Keyboard shortcuts the frontend may ask for (never free-form keys).</summary>
    internal static readonly IReadOnlyDictionary<string, DiscordWebKey> Shortcuts =
        new Dictionary<string, DiscordWebKey>(StringComparer.Ordinal)
        {
            ["quick-switcher"] = new("k", "KeyK", 75, CdpCtrl),
            ["previous-server"] = new("ArrowUp", "ArrowUp", 38, CdpCtrl | CdpAlt),
            ["next-server"] = new("ArrowDown", "ArrowDown", 40, CdpCtrl | CdpAlt),
            ["previous-channel"] = new("ArrowUp", "ArrowUp", 38, CdpAlt),
            ["next-channel"] = new("ArrowDown", "ArrowDown", 40, CdpAlt),
            ["previous-unread"] = new("ArrowUp", "ArrowUp", 38, CdpAlt | CdpShift),
            ["next-unread"] = new("ArrowDown", "ArrowDown", 40, CdpAlt | CdpShift),
            ["toggle-mute"] = new("M", "KeyM", 77, CdpCtrl | CdpShift),
            ["toggle-deafen"] = new("D", "KeyD", 68, CdpCtrl | CdpShift),
            ["mark-read"] = new("Escape", "Escape", 27, CdpShift),
            ["escape"] = new("Escape", "Escape", 27, 0),
            ["page-up"] = new("PageUp", "PageUp", 33, 0),
            ["page-down"] = new("PageDown", "PageDown", 34, 0),
            ["jump-to-present"] = new("End", "End", 35, CdpCtrl),
        };

    /// <summary>
    /// Installs (once per document) the page helper: cursor tracking with a
    /// drawn pointer for handhelds where Windows hides the mouse, popup count
    /// and voice switches. Discord's class names are generated, so only stable
    /// prefixes, landmarks and ARIA states are used.
    /// </summary>
    internal const string PageHelperScript = """
(() => {
  if (window.__tfsDiscordWeb?.version === 3) return true;
  const state = { x: -1, y: -1, movedAt: 0 };
  const OVERLAYS = '[role="dialog"], [aria-modal="true"], [role="menu"], [role="listbox"], [class*="layerContainer"] > *';
  let cursor = null;
  let hideTimer = 0;
  const ensureCursor = () => {
    if (cursor?.isConnected) return cursor;
    if (!document.documentElement) return null;
    cursor = document.createElement('div');
    cursor.setAttribute('data-tfs-cursor', '');
    cursor.style.cssText = 'position:fixed;left:0;top:0;width:22px;height:22px;z-index:2147483647;pointer-events:none;' +
      'opacity:0;transition:opacity .25s;will-change:transform;filter:drop-shadow(0 1px 2px rgba(0,0,0,.7));';
    cursor.innerHTML = '<svg width="22" height="22" viewBox="0 0 22 22"><path d="M3 2l14 9.2-6.2 1.2 3.6 6.8-2.6 1.3-3.6-6.8L3 18z" fill="#fff" stroke="#1a9fff" stroke-width="1.4" stroke-linejoin="round"/></svg>';
    document.documentElement.appendChild(cursor);
    const style = document.createElement('style');
    style.setAttribute('data-tfs-cursor-style', '');
    style.textContent = 'html[data-tfs-cursor-visible] *, html[data-tfs-cursor-visible] { cursor: none !important; }';
    document.documentElement.appendChild(style);
    return cursor;
  };
  const onMove = (event) => {
    state.x = event.clientX;
    state.y = event.clientY;
    state.movedAt = Date.now();
    const element = ensureCursor();
    if (!element) return;
    element.style.transform = 'translate(' + (state.x - 3) + 'px,' + (state.y - 2) + 'px)';
    element.style.opacity = '1';
    document.documentElement.setAttribute('data-tfs-cursor-visible', '');
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => {
      element.style.opacity = '0';
      document.documentElement.removeAttribute('data-tfs-cursor-visible');
    }, 4000);
  };
  window.addEventListener('mousemove', onMove, { capture: true, passive: true });
  window.addEventListener('pointermove', onMove, { capture: true, passive: true });
  const visible = (element) => element instanceof Element && element.getClientRects().length > 0;
  const panel = () => document.querySelector('section[class*="panels"]');
  const switches = () => { const p = panel(); return p ? Array.from(p.querySelectorAll('button[role="switch"]')) : []; };
  const checked = (button) => button ? button.getAttribute('aria-checked') === 'true' : null;
  window.__tfsDiscordWeb = {
    version: 3,
    pointer() {
      const width = window.innerWidth;
      const height = window.innerHeight;
      const known = state.x >= 0 && state.y >= 0;
      const x = known ? Math.min(Math.max(state.x, 0), width - 1) : Math.round(width / 2);
      const y = known ? Math.min(Math.max(state.y, 0), height - 1) : Math.round(height / 2);
      return { x, y, known, width, height };
    },
    overlays() {
      return Array.from(document.querySelectorAll(OVERLAYS)).filter(visible).length;
    },
    status() {
      const path = location.pathname;
      const loginPage = /^\/(login|register)(\/|$)/.test(path);
      const appShell = Boolean(document.querySelector('[data-list-id="guildsnav"], nav[class*="guilds"]'));
      const list = switches();
      const guild = /^\/channels\/(\d{16,32})/.exec(path);
      return {
        signedIn: loginPage ? false : appShell ? true : null,
        voiceConnected: Boolean(document.querySelector('[class*="rtcConnectionStatus"]')),
        muted: checked(list[0]),
        deafened: checked(list[1]),
        guildId: guild ? guild[1] : '',
        path,
        overlays: this.overlays(),
      };
    },
    toggleSwitch(index) {
      const button = switches()[index];
      if (!(button instanceof HTMLElement)) return false;
      button.click();
      return true;
    },
    navigate(path) {
      if (typeof path !== 'string' || !/^\/(channels|users|app|store|discovery)(\/|$)/.test(path)) return false;
      if (location.pathname === path) return true;
      history.pushState(history.state, '', path);
      window.dispatchEvent(new PopStateEvent('popstate', { state: history.state }));
      return true;
    },
  };
  return true;
})()
""";

    /// <summary>Page binding the keyboard helper calls ("show" / "hide").</summary>
    internal const string KeyboardBindingName = "tfsDiscordKeyboard";

    /// <summary>
    /// Steam's BrowserView only opens its on-screen keyboard for &lt;input&gt;
    /// and &lt;textarea&gt; (its node-has-focus check), but Discord's message
    /// box is a contenteditable editor. This helper reports clicks into any
    /// text field (and Tab focus) so the frontend can open Steam's keyboard
    /// through the BrowserView's own keyboard reference. Programmatic focus
    /// (Discord focusing the box after a channel switch) is ignored, so the
    /// keyboard only appears when the user picked a field. Nothing typed is
    /// read: only "show" or "hide" leaves the page.
    /// </summary>
    internal const string KeyboardHelperScript = """
(() => {
  if (window.__tfsDiscordKeyboard?.version === 1) return true;
  const TEXT_TYPES = new Set(['', 'text', 'search', 'email', 'url', 'tel', 'password', 'number',
    'date', 'datetime-local', 'month', 'time', 'week']);
  const editableOf = (node) => {
    const start = node instanceof Element ? node : node?.parentElement;
    const field = start?.closest?.('input, textarea, [contenteditable]:not([contenteditable="false"])');
    if (!field || field.disabled || field.readOnly === true) return null;
    if (field.tagName === 'INPUT') return TEXT_TYPES.has((field.getAttribute('type') || '').toLowerCase()) ? field : null;
    return field.tagName === 'TEXTAREA' || field.isContentEditable ? field : null;
  };
  let shown = false;
  let lastTabAt = 0;
  let lastKind = '';
  let lastAt = 0;
  const send = (kind) => {
    const now = Date.now();
    if (kind === lastKind && now - lastAt < 400) return;
    lastKind = kind;
    lastAt = now;
    shown = kind === 'show';
    try { window.tfsDiscordKeyboard?.(kind); } catch {}
  };
  window.addEventListener('click', (event) => {
    if (!event.isTrusted) return;
    if (editableOf(event.target)) send('show');
    else if (shown) send('hide');
  }, { capture: true, passive: true });
  window.addEventListener('keydown', (event) => {
    if (event.key === 'Tab') lastTabAt = Date.now();
  }, { capture: true, passive: true });
  window.addEventListener('focusin', (event) => {
    if (Date.now() - lastTabAt < 800 && editableOf(event.target)) send('show');
  }, { capture: true, passive: true });
  window.__tfsDiscordKeyboard = { version: 1, editableOf };
  return true;
})()
""";

    public async Task<DiscordWebState> GetStateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken);
            if (session is null)
            {
                return DiscordWebState.NotRunning;
            }

            var status = await EvaluateHelperAsync(session, "window.__tfsDiscordWeb.status()", cancellationToken);
            return ParseState(status, session.Target.Url);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clicks at the page cursor (A on the controller).</summary>
    public async Task<DiscordWebActionResult> ClickAsync(string? button, CancellationToken cancellationToken)
    {
        var cdpButton = string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) ? "right" : "left";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken);
            if (session is null)
            {
                return DiscordWebActionResult.NotRunning;
            }

            var pointer = await EvaluateHelperAsync(session, "window.__tfsDiscordWeb.pointer()", cancellationToken);
            if (pointer is not { ValueKind: JsonValueKind.Object })
            {
                return new DiscordWebActionResult(false, "Discord is still loading.");
            }

            var x = ReadDouble(pointer.Value, "x");
            var y = ReadDouble(pointer.Value, "y");
            await DispatchMouseAsync(session, "mouseMoved", x, y, "none", 0, cancellationToken);
            await DispatchMouseAsync(session, "mousePressed", x, y, cdpButton, 1, cancellationToken);
            await DispatchMouseAsync(session, "mouseReleased", x, y, cdpButton, 1, cancellationToken);
            return new DiscordWebActionResult(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs one whitelisted Discord shortcut (mute, quick switcher, server/channel).</summary>
    public async Task<DiscordWebActionResult> RunShortcutAsync(string? action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action) || !Shortcuts.TryGetValue(action, out var key))
        {
            return new DiscordWebActionResult(false, "Unknown Discord action.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken);
            if (session is null)
            {
                return DiscordWebActionResult.NotRunning;
            }

            // The user panel switches follow Discord's own keybind settings,
            // so they are preferred over the default Ctrl+Shift shortcuts.
            if (action is "toggle-mute" or "toggle-deafen")
            {
                var index = action == "toggle-mute" ? 0 : 1;
                var clicked = await EvaluateHelperAsync(
                    session,
                    $"window.__tfsDiscordWeb.toggleSwitch({index})",
                    cancellationToken);
                if (clicked is { ValueKind: JsonValueKind.True })
                {
                    return new DiscordWebActionResult(true, null);
                }
            }

            await DispatchKeyAsync(session, key, cancellationToken);
            return new DiscordWebActionResult(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// B on the controller: closes Discord's top popup, menu or dialog.
    /// Handled only when fewer popups remain afterwards, so with nothing open
    /// the frontend leaves the page (Discord keeps running in the background).
    /// </summary>
    public async Task<DiscordWebBackResult> BackAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken);
            if (session is null)
            {
                return new DiscordWebBackResult(false, 0);
            }

            var before = ReadInt(await EvaluateHelperAsync(session, "window.__tfsDiscordWeb.overlays()", cancellationToken));
            if (before <= 0)
            {
                return new DiscordWebBackResult(false, 0);
            }

            await DispatchKeyAsync(session, Shortcuts["escape"], cancellationToken);
            await Task.Delay(320, cancellationToken);
            var after = ReadInt(await EvaluateHelperAsync(session, "window.__tfsDiscordWeb.overlays()", cancellationToken));
            return new DiscordWebBackResult(DecideBackHandled(before, after), after);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>In-app navigation without reloading Discord (server, DM, home).</summary>
    public async Task<DiscordWebActionResult> NavigateAsync(string? path, CancellationToken cancellationToken)
    {
        var normalized = NormalizeAppPath(path);
        if (normalized is null)
        {
            return new DiscordWebActionResult(false, "Only Discord app pages can be opened.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken);
            if (session is null)
            {
                return DiscordWebActionResult.NotRunning;
            }

            var result = await EvaluateHelperAsync(
                session,
                $"window.__tfsDiscordWeb.navigate({JsonSerializer.Serialize(normalized)})",
                cancellationToken);
            return result is { ValueKind: JsonValueKind.True }
                ? new DiscordWebActionResult(true, null)
                : new DiscordWebActionResult(false, "Discord could not open that page.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _gate.Dispose();
    }

    internal static bool DecideBackHandled(int before, int after) => before > 0 && after < before;

    /// <summary>Discord app paths the frontend may open; everything else is refused.</summary>
    internal static string? NormalizeAppPath(string? path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.Length is 0 or > 120 || !value.StartsWith('/'))
        {
            return null;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => !segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '@' or '-')))
        {
            return null;
        }

        return segments[0] switch
        {
            "app" when segments.Length == 1 => "/app",
            "channels" when segments.Length is >= 2 and <= 3 &&
                (segments[1] == "@me" || IsSnowflake(segments[1])) &&
                (segments.Length == 2 || IsSnowflake(segments[2])) => value,
            "users" when segments.Length == 2 && IsSnowflake(segments[1]) => value,
            _ => null
        };
    }

    /// <summary>The CEF target of Discord Web: a page on discord.com (Steam's BrowserView).</summary>
    internal static SteamDevToolsTarget? FindDiscordTarget(IReadOnlyList<SteamDevToolsTarget> targets)
    {
        return targets
            .Where(target =>
                string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl) &&
                IsDiscordUrl(target.Url))
            // The app shell beats a sign-in or OAuth page left open elsewhere.
            .OrderByDescending(target => target.Url.Contains("/channels/", StringComparison.OrdinalIgnoreCase) ||
                                         target.Url.EndsWith("/app", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    internal static bool IsDiscordUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com" or "www.discord.com";
    }

    internal static DiscordWebState ParseState(JsonElement? status, string url)
    {
        if (status is not { ValueKind: JsonValueKind.Object } value)
        {
            return new DiscordWebState(true, null, false, null, null, string.Empty, 0, SafePath(url));
        }

        return new DiscordWebState(
            Running: true,
            SignedIn: ReadNullableBool(value, "signedIn"),
            VoiceConnected: ReadNullableBool(value, "voiceConnected") == true,
            Muted: ReadNullableBool(value, "muted"),
            Deafened: ReadNullableBool(value, "deafened"),
            GuildId: value.TryGetProperty("guildId", out var guild) && guild.ValueKind == JsonValueKind.String
                ? guild.GetString() ?? string.Empty
                : string.Empty,
            Overlays: value.TryGetProperty("overlays", out var overlays) && overlays.TryGetInt32(out var count) ? count : 0,
            Path: value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString() ?? string.Empty
                : SafePath(url));
    }

    private async Task<SteamCdpSession?> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SteamDevToolsTarget> targets;
        try
        {
            targets = await _devToolsClient.GetTargetsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var target = FindDiscordTarget(targets);
        if (target is null)
        {
            await DropSessionAsync();
            return null;
        }

        if (_session is { IsOpen: true } open &&
            string.Equals(open.Target.Id, target.Id, StringComparison.Ordinal))
        {
            return open;
        }

        await DropSessionAsync();
        try
        {
            _session = await SteamCdpSession.ConnectAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"discord-web attach failed: {exception.Message}");
            return null;
        }

        try
        {
            // Survives Discord reloads and sign-in redirects.
            await _session.SendAsync(
                "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object?> { ["source"] = PageHelperScript },
                cancellationToken,
                CommandTimeout);
            await _session.SendAsync(
                "Page.addScriptToEvaluateOnNewDocument",
                new Dictionary<string, object?> { ["source"] = KeyboardHelperScript },
                cancellationToken,
                CommandTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log?.Invoke($"discord-web new-document script failed: {exception.Message}");
        }

        try
        {
            // Bindings stay across navigations for this socket; the page
            // calls it only for "show" / "hide" (see KeyboardHelperScript).
            _session.EventReceived += OnSessionEvent;
            await _session.SendAsync(
                "Runtime.addBinding",
                new Dictionary<string, object?> { ["name"] = KeyboardBindingName },
                cancellationToken,
                CommandTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log?.Invoke($"discord-web keyboard binding failed: {exception.Message}");
        }

        await TryGrantMicrophoneAsync(_session, cancellationToken);
        _log?.Invoke($"discord-web attached target={target.Id}");
        return _session;
    }

    /// <summary>
    /// Voice needs the microphone. Steam's BrowserView has no permission
    /// prompt of its own, so the DevTools permission override is set for
    /// Discord's origin (best effort: logged when CEF refuses it).
    /// </summary>
    private async Task TryGrantMicrophoneAsync(SteamCdpSession session, CancellationToken cancellationToken)
    {
        if (_permissionsRequested)
        {
            return;
        }

        _permissionsRequested = true;
        foreach (var origin in new[] { "https://discord.com", "https://ptb.discord.com", "https://canary.discord.com" })
        {
            try
            {
                var reply = await session.SendAsync(
                    "Browser.grantPermissions",
                    new Dictionary<string, object?>
                    {
                        ["origin"] = origin,
                        ["permissions"] = new[] { "audioCapture" },
                    },
                    cancellationToken,
                    CommandTimeout);
                if (reply.TryGetProperty("error", out var error))
                {
                    _log?.Invoke($"discord-web microphone permission refused for {origin}: {error.GetRawText()}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log?.Invoke($"discord-web microphone permission failed for {origin}: {exception.Message}");
            }
        }
    }

    private async Task<JsonElement?> EvaluateHelperAsync(
        SteamCdpSession session,
        string expression,
        CancellationToken cancellationToken)
    {
        var script = $"({PageHelperScript}) && ({KeyboardHelperScript}) && {expression}";
        var result = await session.EvaluateAsync(script, awaitPromise: false, cancellationToken, CommandTimeout);
        if (!result.Success)
        {
            if (!session.IsOpen)
            {
                await DropSessionAsync();
            }

            return null;
        }

        return result.Value as JsonElement?;
    }

    private static async Task DispatchMouseAsync(
        SteamCdpSession session,
        string type,
        double x,
        double y,
        string button,
        int clickCount,
        CancellationToken cancellationToken)
    {
        await session.SendAsync(
            "Input.dispatchMouseEvent",
            new Dictionary<string, object?>
            {
                ["type"] = type,
                ["x"] = x,
                ["y"] = y,
                ["button"] = button,
                ["buttons"] = type == "mousePressed" ? (button == "right" ? 2 : 1) : 0,
                ["clickCount"] = clickCount,
                ["pointerType"] = "mouse",
            },
            cancellationToken,
            CommandTimeout);
    }

    private static async Task DispatchKeyAsync(
        SteamCdpSession session,
        DiscordWebKey key,
        CancellationToken cancellationToken)
    {
        foreach (var type in new[] { "rawKeyDown", "keyUp" })
        {
            await session.SendAsync(
                "Input.dispatchKeyEvent",
                new Dictionary<string, object?>
                {
                    ["type"] = type,
                    ["key"] = key.Key,
                    ["code"] = key.Code,
                    ["windowsVirtualKeyCode"] = key.VirtualKeyCode,
                    ["nativeVirtualKeyCode"] = key.VirtualKeyCode,
                    ["modifiers"] = key.Modifiers,
                },
                cancellationToken,
                CommandTimeout);
        }
    }

    private async Task DropSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        _session = null;
        session.EventReceived -= OnSessionEvent;
        await session.DisposeAsync();
    }

    /// <summary>Runs on the session's receive loop: never blocks it.</summary>
    private void OnSessionEvent(string method, JsonElement parameters)
    {
        var kind = ParseKeyboardRequest(method, parameters);
        if (kind is not null)
        {
            _ = ForwardKeyboardAsync(kind);
        }
    }

    /// <summary>"show" / "hide" from the page binding, anything else is ignored.</summary>
    internal static string? ParseKeyboardRequest(string method, JsonElement parameters)
    {
        if (!string.Equals(method, "Runtime.bindingCalled", StringComparison.Ordinal) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var name) ||
            !string.Equals(name.GetString(), KeyboardBindingName, StringComparison.Ordinal) ||
            !parameters.TryGetProperty("payload", out var payload))
        {
            return null;
        }

        return payload.GetString() switch
        {
            "show" => "show",
            "hide" => "hide",
            _ => null
        };
    }

    /// <summary>
    /// Opens or closes Steam's keyboard through tfs-discord in SharedJSContext
    /// (it owns the BrowserView and its keyboard reference).
    /// </summary>
    private async Task ForwardKeyboardAsync(string kind)
    {
        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            var target = await _devToolsClient.GetSharedJsContextTargetAsync(timeout.Token);
            if (target is null || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
            {
                return;
            }

            var result = await _devToolsClient.EvaluateAsync(
                target.WebSocketDebuggerUrl,
                $"Boolean(globalThis.ToolsForSteamDiscord?.keyboard?.({JsonSerializer.Serialize(kind)}))",
                timeout.Token);
            if (!result.Success)
            {
                _log?.Invoke($"discord-web keyboard {kind} failed: {result.ErrorMessage}");
            }
        }
        catch (Exception exception)
        {
            _log?.Invoke($"discord-web keyboard {kind} failed: {exception.Message}");
        }
    }

    private static string SafePath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : string.Empty;

    private static bool IsSnowflake(string value) =>
        value.Length is >= 16 and <= 32 && value.All(char.IsAsciiDigit);

    private static double ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static int ReadInt(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : 0;

    private static bool? ReadNullableBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;
}

internal sealed record DiscordWebKey(string Key, string Code, int VirtualKeyCode, int Modifiers);

public sealed record DiscordWebState(
    bool Running,
    bool? SignedIn,
    bool VoiceConnected,
    bool? Muted,
    bool? Deafened,
    string GuildId,
    int Overlays,
    string Path)
{
    public static DiscordWebState NotRunning { get; } = new(false, null, false, null, null, string.Empty, 0, string.Empty);
}

public sealed record DiscordWebActionResult(bool Success, string? Message)
{
    public static DiscordWebActionResult NotRunning { get; } = new(false, "Discord Web is not open.");
}

public sealed record DiscordWebBackResult(bool Handled, int Overlays);
