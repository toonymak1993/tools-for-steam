using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

/// <summary>
/// Emulates the Steam Deck's dedicated STEAM and Quick Access buttons on
/// handhelds/controllers that only expose a standard Xbox-style layout (e.g. the
/// MSI Claw, which has no extra Steam/QAM keys). The controller buttons behave
/// button behaves differently depending on what is in the foreground:
/// <list type="bullet">
/// <item>in Steam Big Picture: short press opens the left STEAM menu, hold opens the right Quick Access menu, and</item>
/// <item>in a non-Steam game: only MENU / Start hold is handled, which raises the Steam overlay via Shift+Tab while short presses stay untouched for the game itself.</item>
/// </list>
/// Big Picture continues to use the regular XInput BACK button path because Steam
/// already responds correctly there. In games, a separate Raw Input HID monitor
/// supplies the held-state for the MENU / Start button so Tools for Steam can
/// react without stealing ordinary in-game presses.
/// </summary>
public sealed class ControllerShortcutService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    // How long BACK must be held before it counts as a "hold" (Ctrl+2) instead
    // of a "short press" (Ctrl+1). Tune to taste.
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan InGameQuickAccessHoldThreshold = TimeSpan.FromMilliseconds(1100);

    // XInput button mask for BACK / View (XINPUT_GAMEPAD_BACK).
    private const ushort XinputGamepadBack = 0x0020;

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
    private readonly Func<Task<bool>> _openSteamMenuAsync;
    private readonly Func<Task<bool>> _openQuickAccessMenuAsync;
    private readonly Func<int, Task> _sendControlDigitAsync;
    private readonly Action<string>? _diagnosticLog;
    private readonly SemaphoreSlim _shortcutGate = new(1, 1);

    private ShortcutContext _context;
    private bool _backWasDown;
    private DateTime _backPressedAtUtc;
    private bool _holdActionFired;
    private bool _inGameQuickAccessFired;

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
    public ControllerShortcutService(
        Func<bool> isEnabled,
        Func<bool> isBigPictureForeground,
        Func<bool> isGameInForeground,
        Func<bool> isHidMenuButtonDown,
        Func<Task<bool>> openSteamMenuAsync,
        Func<Task<bool>> openQuickAccessMenuAsync,
        Func<int, Task> sendControlDigitAsync,
        Action<string>? diagnosticLog = null,
        Func<bool>? isHidBackButtonDown = null)
    {
        _isEnabled = isEnabled;
        _isBigPictureForeground = isBigPictureForeground;
        _isGameInForeground = isGameInForeground;
        _isHidMenuButtonDown = isHidMenuButtonDown;
        _isHidBackButtonDown = isHidBackButtonDown ?? (() => false);
        _openSteamMenuAsync = openSteamMenuAsync;
        _openQuickAccessMenuAsync = openQuickAccessMenuAsync;
        _sendControlDigitAsync = sendControlDigitAsync;
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

        var context = _isBigPictureForeground()
            ? ShortcutContext.BigPicture
            : _isGameInForeground()
                ? ShortcutContext.InGameOverlay
                : ShortcutContext.None;
        var backDown = context == ShortcutContext.BigPicture
            ? TryReadBackButtonDown() || _isHidBackButtonDown()
            : context == ShortcutContext.InGameOverlay && _isHidMenuButtonDown();
        var nowUtc = DateTime.UtcNow;

        if (context != _context)
        {
            Log($"context previous={_context} current={context} inputDown={backDown}");
            _context = context;
            _backWasDown = backDown;
            _backPressedAtUtc = nowUtc;
            _holdActionFired = false;
            _inGameQuickAccessFired = false;
            return;
        }

        if (backDown && !_backWasDown)
        {
            // Rising edge: BACK just went down. Start timing the press.
            Log($"button-down context={context}");
            _backPressedAtUtc = nowUtc;
            _holdActionFired = false;
            _inGameQuickAccessFired = false;
        }
        else if (backDown && _backWasDown)
        {
            // Still held: once we cross the hold threshold, open Quick Access
            // exactly once and suppress the short-press action on release.
            if (!_holdActionFired && nowUtc - _backPressedAtUtc >= HoldThreshold)
            {
                _holdActionFired = true;
                Log($"hold-detected context={context} durationMs={(nowUtc - _backPressedAtUtc).TotalMilliseconds:F0}");
                TriggerShortcut(
                    _context == ShortcutContext.BigPicture
                        ? ShortcutIntent.QuickAccess
                        : ShortcutIntent.Overlay);
            }

            if (_context == ShortcutContext.InGameOverlay &&
                _holdActionFired &&
                !_inGameQuickAccessFired &&
                nowUtc - _backPressedAtUtc >= InGameQuickAccessHoldThreshold)
            {
                _inGameQuickAccessFired = true;
                Log($"extended-hold-detected context={context} durationMs={(nowUtc - _backPressedAtUtc).TotalMilliseconds:F0}");
                TriggerShortcut(ShortcutIntent.InGameQuickAccess);
            }
        }
        else if (!backDown && _backWasDown)
        {
            Log($"button-up context={context} durationMs={(nowUtc - _backPressedAtUtc).TotalMilliseconds:F0} holdFired={_holdActionFired}");
            // Falling edge: released. If we never reached the hold threshold,
            // this was a short press -> open the STEAM menu.
            if (_context == ShortcutContext.BigPicture &&
                !_holdActionFired &&
                nowUtc - _backPressedAtUtc < HoldThreshold)
            {
                TriggerShortcut(ShortcutIntent.SteamMenu);
            }

            _holdActionFired = false;
            _inGameQuickAccessFired = false;
        }

        _backWasDown = backDown;
    }

    private void ResetState()
    {
        _context = ShortcutContext.None;
        _backWasDown = false;
        _holdActionFired = false;
        _inGameQuickAccessFired = false;
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
        if (intent == ShortcutIntent.Overlay)
        {
            var result = await TrySendKeyboardChordAsync(ShiftVirtualKey, TabVirtualKey);
            Log($"overlay-send success={result.Success} sentInputs={result.SentInputs}/4 win32Error={result.ErrorCode}");
            return;
        }

        if (intent == ShortcutIntent.InGameQuickAccess)
        {
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

    private static bool TryReadBackButtonDown()
    {
        // Read the first connected XInput pad. The Claw presents as a single
        // XInput device, so index 0 is the handheld's built-in controller.
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetState(index, out var state) == ErrorSuccess &&
                (state.Gamepad.wButtons & XinputGamepadBack) != 0)
            {
                return true;
            }
        }

        return false;
    }

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
