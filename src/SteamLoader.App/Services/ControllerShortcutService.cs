using System.Runtime.InteropServices;
using SteamLoader.App.Models;

namespace SteamLoader.App.Services;

/// <summary>
/// Emulates the Steam Deck's dedicated STEAM and Quick Access buttons on
/// handhelds/controllers that only expose a standard Xbox-style layout (e.g. the
/// MSI Claw, which has no extra Steam/QAM keys). The controller buttons behave
/// button behaves differently depending on what is in the foreground:
/// <list type="bullet">
/// <item>in Steam Big Picture: independent one-to-three-button combinations open the left STEAM menu or the right Quick Access menu, and</item>
/// <item>in a game: independent held combinations open the overlay or Quick Access. Store Sync games without an injected overlay open Big Picture Quick Access in front of the game; all other games retain the Steam overlay shortcuts.</item>
/// </list>
/// XInput supplies every configurable digital button. A separate Raw Input HID
/// monitor reinforces View / Back and Menu / Start while games are in front so
/// Tools for Steam can react without stealing ordinary in-game presses.
/// </summary>
public sealed class ControllerShortcutService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ContextRefreshInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SettingsRefreshInterval = TimeSpan.FromMilliseconds(500);

    private const int SteamMenuDigit = 1;    // Ctrl+1 -> STEAM menu (left)
    private const int QuickAccessDigit = 2;  // Ctrl+2 -> Quick Access menu (right)
    private const ushort ShiftVirtualKey = 0x10;
    private const ushort ControlVirtualKey = 0x11;
    private const ushort TabVirtualKey = 0x09;
    private const ushort Digit2VirtualKey = 0x32;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isBigPictureForeground;
    private readonly Func<bool> _isGameInForeground;
    private readonly Func<bool> _isHidMenuButtonDown;
    private readonly Func<bool> _isHidBackButtonDown;
    private readonly Func<IReadOnlyList<ushort>> _hidControllerButtonMasksProvider;
    private readonly Func<Task<bool>> _openSteamMenuAsync;
    private readonly Func<Task<bool>> _openQuickAccessMenuAsync;
    private readonly Func<Task<bool>> _openInGameOverlayAsync;
    private readonly Func<Task<bool>> _openInGameQuickAccessAsync;
    private readonly Func<int, Task> _sendControlDigitAsync;
    private readonly Func<Task<bool>> _tryOpenExternalGameQuickAccessAsync;
    private readonly Func<ControllerShortcutSettingsSnapshot> _settingsProvider;
    private readonly Action<string>? _diagnosticLog;
    private readonly SemaphoreSlim _shortcutGate = new(1, 1);

    private ShortcutContext _context;
    private ShortcutContext _detectedContext;
    private readonly ShortcutPressState _steamMenuPress = new();
    private readonly ShortcutPressState _steamQuickAccessPress = new();
    private readonly ShortcutPressState _inGameOverlayPress = new();
    private readonly ShortcutPressState _inGameQuickAccessPress = new();
    private DateTime _nextSettingsRefreshAtUtc;
    private DateTime _nextContextRefreshAtUtc;
    private ControllerShortcutSettingsSnapshot _settings = ControllerShortcutSettingsSnapshot.Default;
    private string _settingsSignature = string.Empty;

    /// <param name="isEnabled">Runtime gate so the feature can be toggled without restarting the host.</param>
    /// <param name="isBigPictureForeground">Returns true while Steam Big Picture owns the foreground window.</param>
    /// <param name="isGameInForeground">Returns true while a non-Steam game owns the foreground window.</param>
    /// <param name="isHidMenuButtonDown">Held-state from the background Raw Input / HID monitor used only in games.</param>
    /// <param name="openSteamMenuAsync">Best-effort direct opener for the left STEAM menu.</param>
    /// <param name="openQuickAccessMenuAsync">Best-effort direct opener for the right Quick Access menu.</param>
    /// <param name="sendControlDigitAsync">
    /// Delivers a Ctrl+&lt;digit&gt; shortcut into Steam's Big Picture UI. Wired to
    /// the DevTools client in the background host as the final compatibility
    /// fallback.
    /// </param>
    /// <param name="tryOpenExternalGameQuickAccessAsync">
    /// Attempts the Store Sync fallback before sending in-game Steam shortcuts.
    /// Returns false when the game has a native Steam overlay or is not managed by Store Sync.
    /// </param>
    /// <param name="settingsProvider">Returns the current persisted button and hold-time configuration.</param>
    public ControllerShortcutService(
        Func<bool> isEnabled,
        Func<bool> isBigPictureForeground,
        Func<bool> isGameInForeground,
        Func<bool> isHidMenuButtonDown,
        Func<Task<bool>> openSteamMenuAsync,
        Func<Task<bool>> openQuickAccessMenuAsync,
        Func<int, Task> sendControlDigitAsync,
        Action<string>? diagnosticLog = null,
        Func<bool>? isHidBackButtonDown = null,
        Func<Task<bool>>? tryOpenExternalGameQuickAccessAsync = null,
        Func<ControllerShortcutSettingsSnapshot>? settingsProvider = null,
        Func<IReadOnlyList<ushort>>? hidControllerButtonMasksProvider = null,
        Func<Task<bool>>? openInGameOverlayAsync = null,
        Func<Task<bool>>? openInGameQuickAccessAsync = null)
    {
        _isEnabled = isEnabled;
        _isBigPictureForeground = isBigPictureForeground;
        _isGameInForeground = isGameInForeground;
        _isHidMenuButtonDown = isHidMenuButtonDown;
        _isHidBackButtonDown = isHidBackButtonDown ?? (() => false);
        _hidControllerButtonMasksProvider = hidControllerButtonMasksProvider ?? (() => []);
        _openSteamMenuAsync = openSteamMenuAsync;
        _openQuickAccessMenuAsync = openQuickAccessMenuAsync;
        _openInGameOverlayAsync = openInGameOverlayAsync ?? (() => Task.FromResult(false));
        _openInGameQuickAccessAsync = openInGameQuickAccessAsync ?? (() => Task.FromResult(false));
        _sendControlDigitAsync = sendControlDigitAsync;
        _tryOpenExternalGameQuickAccessAsync = tryOpenExternalGameQuickAccessAsync ?? (() => Task.FromResult(false));
        _settingsProvider = settingsProvider ?? (() => ControllerShortcutSettingsSnapshot.Default);
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
                    Poll();
                }
                catch (Exception exception)
                {
                    // A transient controller/input hiccup must never take the
                    // polling loop (or the background host) down.
                    Log($"poll-error type={exception.GetType().Name} message={exception.Message}");
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Poll()
    {
        if (!_isEnabled())
        {
            ResetState();
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc >= _nextContextRefreshAtUtc)
        {
            _nextContextRefreshAtUtc = nowUtc + ContextRefreshInterval;
            _detectedContext = _isBigPictureForeground()
                ? ShortcutContext.BigPicture
                : _isGameInForeground()
                    ? ShortcutContext.InGameOverlay
                    : ShortcutContext.None;
        }
        var context = _detectedContext;
        var settings = GetSettings(nowUtc);
        var controllerButtonMasks = ReadConnectedControllerButtonMasks().ToList();
        foreach (var hidMask in _hidControllerButtonMasksProvider())
        {
            if (!controllerButtonMasks.Contains(hidMask))
            {
                controllerButtonMasks.Add(hidMask);
            }
        }
        var steamMenuDown = context == ShortcutContext.BigPicture &&
            IsCombinationDown(settings.SteamMenuButtons, controllerButtonMasks);
        var steamQuickAccessDown = context == ShortcutContext.BigPicture &&
            IsCombinationDown(settings.SteamQuickAccessButtons, controllerButtonMasks);
        var inGameOverlayDown = context == ShortcutContext.InGameOverlay &&
            IsCombinationDown(settings.InGameOverlayButtons, controllerButtonMasks);
        var inGameQuickAccessDown = context == ShortcutContext.InGameOverlay &&
            IsCombinationDown(settings.InGameQuickAccessButtons, controllerButtonMasks);

        if (context != _context)
        {
            Log(
                $"context previous={_context} current={context} " +
                $"steamMenuDown={steamMenuDown} steamQuickAccessDown={steamQuickAccessDown} " +
                $"inGameOverlayDown={inGameOverlayDown} inGameQuickAccessDown={inGameQuickAccessDown}");
            _context = context;
            _steamMenuPress.EnterContext(steamMenuDown, nowUtc);
            _steamQuickAccessPress.EnterContext(steamQuickAccessDown, nowUtc);
            _inGameOverlayPress.EnterContext(inGameOverlayDown, nowUtc);
            _inGameQuickAccessPress.EnterContext(inGameQuickAccessDown, nowUtc);
            return;
        }

        if (context == ShortcutContext.BigPicture)
        {
            PollShortPress(
                _steamMenuPress,
                steamMenuDown,
                nowUtc,
                settings.SteamHoldMilliseconds,
                ShortcutIntent.SteamMenu,
                "steam-menu");
            PollHoldPress(
                _steamQuickAccessPress,
                steamQuickAccessDown,
                nowUtc,
                settings.SteamHoldMilliseconds,
                ShortcutIntent.QuickAccess,
                "steam-quick-access");
        }
        else if (context == ShortcutContext.InGameOverlay)
        {
            PollHoldPress(
                _inGameOverlayPress,
                inGameOverlayDown,
                nowUtc,
                settings.InGameOverlayHoldMilliseconds,
                ShortcutIntent.Overlay,
                "in-game-overlay");
            PollHoldPress(
                _inGameQuickAccessPress,
                inGameQuickAccessDown,
                nowUtc,
                settings.InGameQuickAccessHoldMilliseconds,
                ShortcutIntent.InGameQuickAccess,
                "in-game-quick-access");
        }
    }

    private void ResetState()
    {
        _context = ShortcutContext.None;
        _detectedContext = ShortcutContext.None;
        _nextContextRefreshAtUtc = DateTime.MinValue;
        _steamMenuPress.Reset();
        _steamQuickAccessPress.Reset();
        _inGameOverlayPress.Reset();
        _inGameQuickAccessPress.Reset();
    }

    private void PollShortPress(
        ShortcutPressState press,
        bool isDown,
        DateTime nowUtc,
        int maximumDurationMilliseconds,
        ShortcutIntent intent,
        string action)
    {
        if (isDown && !press.WasDown)
        {
            press.Begin(nowUtc);
            Log($"combination-down action={action}");
        }
        else if (!isDown && press.WasDown)
        {
            var duration = nowUtc - press.PressedAtUtc;
            Log(
                $"combination-up action={action} durationMs={duration.TotalMilliseconds:F0} " +
                $"startedInContext={press.StartedInContext}");
            if (press.StartedInContext && duration < TimeSpan.FromMilliseconds(maximumDurationMilliseconds))
            {
                TriggerShortcut(intent);
            }

            press.End();
        }

        press.WasDown = isDown;
    }

    private void PollHoldPress(
        ShortcutPressState press,
        bool isDown,
        DateTime nowUtc,
        int holdMilliseconds,
        ShortcutIntent intent,
        string action)
    {
        if (isDown && !press.WasDown)
        {
            press.Begin(nowUtc);
            Log($"combination-down action={action}");
        }
        else if (isDown &&
            press.WasDown &&
            press.StartedInContext &&
            !press.ActionFired &&
            nowUtc - press.PressedAtUtc >= TimeSpan.FromMilliseconds(holdMilliseconds))
        {
            press.ActionFired = true;
            Log(
                $"combination-hold action={action} " +
                $"durationMs={(nowUtc - press.PressedAtUtc).TotalMilliseconds:F0}");
            TriggerShortcut(intent);
        }
        else if (!isDown && press.WasDown)
        {
            Log(
                $"combination-up action={action} " +
                $"durationMs={(nowUtc - press.PressedAtUtc).TotalMilliseconds:F0} fired={press.ActionFired}");
            press.End();
        }

        press.WasDown = isDown;
    }

    private ControllerShortcutSettingsSnapshot GetSettings(DateTime nowUtc)
    {
        if (nowUtc < _nextSettingsRefreshAtUtc)
        {
            return _settings;
        }

        _nextSettingsRefreshAtUtc = nowUtc + SettingsRefreshInterval;
        try
        {
            var candidate = _settingsProvider();
            _settings = ControllerShortcutSettingsSnapshot.Normalize(
                candidate.SteamMenuButtons,
                candidate.SteamQuickAccessButtons,
                candidate.InGameOverlayButtons,
                candidate.InGameQuickAccessButtons,
                candidate.SteamButton,
                candidate.InGameButton,
                candidate.SteamHoldMilliseconds,
                candidate.InGameOverlayHoldMilliseconds,
                candidate.InGameQuickAccessHoldMilliseconds);

            var signature = BuildSettingsSignature(_settings);
            if (!string.Equals(signature, _settingsSignature, StringComparison.Ordinal))
            {
                _settingsSignature = signature;
                Log($"settings {signature}");
            }
        }
        catch (Exception exception)
        {
            Log($"settings-refresh-error type={exception.GetType().Name} message={exception.Message}");
        }

        return _settings;
    }

    private bool IsCombinationDown(IReadOnlyList<string> buttonIds, IReadOnlyList<ushort> controllerButtonMasks)
    {
        if (buttonIds.Count == 0)
        {
            return false;
        }

        if (IsXInputCombinationDown(buttonIds, controllerButtonMasks))
        {
            return true;
        }

        // Some handheld stacks expose View/Menu through Raw HID while the
        // remaining controls arrive through the virtual XInput pad. Requiring
        // the complete chord from XInput made combinations such as View + LB
        // impossible on exactly those devices. Let HID satisfy only the two
        // system buttons, while every remaining button must still be present
        // together on one XInput controller.
        return IsHybridCombinationDown(
            buttonIds,
            controllerButtonMasks,
            _isHidBackButtonDown(),
            _isHidMenuButtonDown());
    }

    internal static bool IsXInputCombinationDown(
        IReadOnlyList<string> buttonIds,
        IReadOnlyList<ushort> controllerButtonMasks)
    {
        ushort combinationMask = 0;
        foreach (var buttonId in buttonIds)
        {
            combinationMask |= GetXInputButtonMask(buttonId);
        }

        return combinationMask != 0 &&
            controllerButtonMasks.Any(mask => (mask & combinationMask) == combinationMask);
    }

    internal static bool IsHybridCombinationDown(
        IReadOnlyList<string> buttonIds,
        IReadOnlyList<ushort> controllerButtonMasks,
        bool isHidBackDown,
        bool isHidMenuDown)
    {
        if (buttonIds.Count == 0)
        {
            return false;
        }

        ushort remainingMask = 0;
        foreach (var buttonId in buttonIds)
        {
            if (string.Equals(buttonId, "back", StringComparison.Ordinal) && isHidBackDown)
            {
                continue;
            }

            if (string.Equals(buttonId, "start", StringComparison.Ordinal) && isHidMenuDown)
            {
                continue;
            }

            remainingMask |= GetXInputButtonMask(buttonId);
        }

        if (remainingMask == 0)
        {
            return true;
        }

        return controllerButtonMasks.Any(mask => (mask & remainingMask) == remainingMask);
    }

    internal static IReadOnlyList<string> ReadPressedButtonIds(
        IReadOnlyList<ushort> controllerButtonMasks,
        bool isHidBackDown,
        bool isHidMenuDown)
    {
        // The settings recorder targets one controller. Prefer the pad with
        // the most simultaneously held buttons instead of merging independent
        // controllers into an accidental chord.
        ushort selectedMask = 0;
        var selectedCount = -1;
        foreach (var mask in controllerButtonMasks)
        {
            var count = CountSetBits(mask);
            if (count > selectedCount)
            {
                selectedMask = mask;
                selectedCount = count;
            }
        }

        var buttons = ControllerButtonDefinitions
            .Where(definition => (selectedMask & definition.Mask) == definition.Mask)
            .Select(definition => definition.Id)
            .ToList();

        if (isHidBackDown && !buttons.Contains("back", StringComparer.Ordinal))
        {
            buttons.Insert(0, "back");
        }

        if (isHidMenuDown && !buttons.Contains("start", StringComparer.Ordinal))
        {
            var insertionIndex = buttons.Count > 0 && buttons[0] == "back" ? 1 : 0;
            buttons.Insert(insertionIndex, "start");
        }

        return buttons;
    }

    private void TriggerShortcut(ShortcutIntent intent)
    {
        // Fire-and-forget: the DevTools round-trip must not stall the poll loop.
        _ = InvokeSafelyAsync(intent);
    }

    private async Task InvokeSafelyAsync(ShortcutIntent intent)
    {
        try
        {
            await _shortcutGate.WaitAsync();
            try
            {
                await TriggerShortcutCoreAsync(intent);
            }
            finally
            {
                _shortcutGate.Release();
            }
        }
        catch (Exception exception)
        {
            // Delivery failures (e.g. Big Picture not running) are non-fatal.
            Log($"delivery-error intent={intent} type={exception.GetType().Name} message={exception.Message}");
        }
    }

    private async Task TriggerShortcutCoreAsync(ShortcutIntent intent)
    {
        if ((intent is ShortcutIntent.Overlay or ShortcutIntent.InGameQuickAccess) &&
            await _tryOpenExternalGameQuickAccessAsync())
        {
            Log($"external-game-quick-access handled intent={intent}");
            return;
        }

        if (intent == ShortcutIntent.Overlay)
        {
            if (await _openInGameOverlayAsync())
            {
                Log("overlay-open native-controller=true");
                return;
            }

            var result = await TrySendKeyboardChordAsync(ShiftVirtualKey, TabVirtualKey);
            Log($"overlay-send success={result.Success} sentInputs={result.SentInputs}/4 win32Error={result.ErrorCode}");
            return;
        }

        if (intent == ShortcutIntent.InGameQuickAccess)
        {
            if (await _openInGameQuickAccessAsync())
            {
                Log("quick-access-open native-controller=true");
                return;
            }

            var result = await TrySendKeyboardChordAsync(ControlVirtualKey, Digit2VirtualKey);
            Log($"quick-access-send success={result.Success} sentInputs={result.SentInputs}/4 win32Error={result.ErrorCode}");
            return;
        }

        if (await TryOpenSteamPanelDirectlyAsync(intent))
        {
            return;
        }

        await _sendControlDigitAsync(intent == ShortcutIntent.QuickAccess ? QuickAccessDigit : SteamMenuDigit);
    }

    internal static async Task SendControlDigitKeyboardAsync(
        int digit,
        Action<string>? diagnosticLog = null)
    {
        var virtualKey = digit == QuickAccessDigit
            ? Digit2VirtualKey
            : (ushort)0x31;
        var result = await TrySendKeyboardChordAsync(ControlVirtualKey, virtualKey);
        diagnosticLog?.Invoke(
            $"steam-panel-keyboard-fallback digit={digit} success={result.Success} " +
            $"sentInputs={result.SentInputs}/4 win32Error={result.ErrorCode}");
    }

    private Task<bool> TryOpenSteamPanelDirectlyAsync(ShortcutIntent intent)
    {
        return intent == ShortcutIntent.QuickAccess
            ? _openQuickAccessMenuAsync()
            : _openSteamMenuAsync();
    }

    private static async Task<KeyboardChordResult> TrySendKeyboardChordAsync(ushort modifierVirtualKey, ushort keyVirtualKey)
    {
        uint sentInputs = 0;
        try
        {
            if (!TrySendKeyboardInput(modifierVirtualKey, keyUp: false, ref sentInputs, out var errorCode))
            {
                return new KeyboardChordResult(false, sentInputs, errorCode);
            }

            // Steam expects the modifier to already be held when the second
            // key arrives. A zero-duration batch can be missed by its hooks.
            await Task.Delay(45);
            if (!TrySendKeyboardInput(keyVirtualKey, keyUp: false, ref sentInputs, out errorCode))
            {
                _ = TrySendKeyboardInput(modifierVirtualKey, keyUp: true, ref sentInputs, out _);
                return new KeyboardChordResult(false, sentInputs, errorCode);
            }

            await Task.Delay(80);
            var keyUp = TrySendKeyboardInput(keyVirtualKey, keyUp: true, ref sentInputs, out errorCode);
            await Task.Delay(25);
            var modifierUp = TrySendKeyboardInput(modifierVirtualKey, keyUp: true, ref sentInputs, out var modifierUpError);
            return new KeyboardChordResult(
                keyUp && modifierUp && sentInputs == 4,
                sentInputs,
                keyUp ? modifierUpError : errorCode);
        }
        catch (Exception exception)
        {
            return new KeyboardChordResult(false, sentInputs, exception.HResult);
        }
    }

    private static bool TrySendKeyboardInput(
        ushort virtualKey,
        bool keyUp,
        ref uint sentInputs,
        out int errorCode)
    {
        var inputs = new[] { CreateKeyboardInput(virtualKey, keyUp) };
        var sent = SendInput(1, inputs, Marshal.SizeOf<Input>());
        sentInputs += sent;
        errorCode = sent == 1 ? 0 : Marshal.GetLastWin32Error();
        return sent == 1;
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

    internal static IReadOnlyList<ushort> ReadConnectedControllerButtonMasks()
    {
        var buttonMasks = new List<ushort>(4);

        try
        {
            // Read all connected XInput pads. The supported handheld normally
            // presents as index 0, while docked controllers can use any slot.
            for (uint index = 0; index < 4; index++)
            {
                if (XInputGetState(index, out var state) == ErrorSuccess)
                {
                    buttonMasks.Add(state.Gamepad.wButtons);
                }
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return buttonMasks;
    }

    private static ushort GetXInputButtonMask(string buttonId)
    {
        return buttonId switch
        {
            "dpad-up" => (ushort)0x0001,
            "dpad-down" => (ushort)0x0002,
            "dpad-left" => (ushort)0x0004,
            "dpad-right" => (ushort)0x0008,
            "start" => (ushort)0x0010,
            "back" => (ushort)0x0020,
            "left-stick" => (ushort)0x0040,
            "right-stick" => (ushort)0x0080,
            "left-bumper" => (ushort)0x0100,
            "right-bumper" => (ushort)0x0200,
            "a" => (ushort)0x1000,
            "b" => (ushort)0x2000,
            "x" => (ushort)0x4000,
            "y" => (ushort)0x8000,
            _ => (ushort)0
        };
    }

    private static int CountSetBits(ushort value)
    {
        var count = 0;
        while (value != 0)
        {
            value = (ushort)(value & (value - 1));
            count++;
        }

        return count;
    }

    private static string BuildSettingsSignature(ControllerShortcutSettingsSnapshot settings) =>
        $"steamMenu=[{string.Join('+', settings.SteamMenuButtons)}] " +
        $"steamQuickAccess=[{string.Join('+', settings.SteamQuickAccessButtons)}] " +
        $"inGameOverlay=[{string.Join('+', settings.InGameOverlayButtons)}] " +
        $"inGameQuickAccess=[{string.Join('+', settings.InGameQuickAccessButtons)}] " +
        $"holdsMs={settings.SteamHoldMilliseconds}/{settings.InGameOverlayHoldMilliseconds}/{settings.InGameQuickAccessHoldMilliseconds}";

    private static readonly (string Id, ushort Mask)[] ControllerButtonDefinitions =
    [
        ("back", 0x0020),
        ("start", 0x0010),
        ("left-bumper", 0x0100),
        ("right-bumper", 0x0200),
        ("left-stick", 0x0040),
        ("right-stick", 0x0080),
        ("a", 0x1000),
        ("b", 0x2000),
        ("x", 0x4000),
        ("y", 0x8000),
        ("dpad-up", 0x0001),
        ("dpad-down", 0x0002),
        ("dpad-left", 0x0004),
        ("dpad-right", 0x0008)
    ];

    private const uint ErrorSuccess = 0;

    private static Input CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
    }

    private sealed class ShortcutPressState
    {
        public bool WasDown { get; set; }

        public DateTime PressedAtUtc { get; private set; }

        public bool StartedInContext { get; private set; }

        public bool ActionFired { get; set; }

        public void EnterContext(bool isDown, DateTime nowUtc)
        {
            WasDown = isDown;
            PressedAtUtc = nowUtc;
            StartedInContext = false;
            ActionFired = false;
        }

        public void Begin(DateTime nowUtc)
        {
            PressedAtUtc = nowUtc;
            StartedInContext = true;
            ActionFired = false;
        }

        public void End()
        {
            StartedInContext = false;
            ActionFired = false;
        }

        public void Reset()
        {
            WasDown = false;
            PressedAtUtc = default;
            StartedInContext = false;
            ActionFired = false;
        }
    }

    private enum ShortcutIntent
    {
        SteamMenu,
        QuickAccess,
        Overlay,
        InGameQuickAccess
    }

    private enum ShortcutContext
    {
        None,
        BigPicture,
        InGameOverlay
    }

    private readonly record struct KeyboardChordResult(bool Success, uint SentInputs, int ErrorCode);

#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    private struct XinputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XinputState
    {
        public uint dwPacketNumber;
        public XinputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        // INPUT contains a union whose native size is determined by
        // MOUSEINPUT (32 bytes on x64), even when we only send keyboard data.
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
#pragma warning restore CS0649

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XinputState pState);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] inputs, int size);
}
