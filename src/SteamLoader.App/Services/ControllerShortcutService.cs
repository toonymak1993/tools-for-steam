using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

/// <summary>
/// Emulates the Steam Deck's dedicated STEAM and Quick Access buttons on
/// handhelds/controllers that only expose a standard Xbox-style layout (e.g. the
/// MSI Claw, which has no extra Steam/QAM keys). The controller BACK / View
/// button becomes:
/// <list type="bullet">
/// <item>a short press -> Ctrl+1 (open the left STEAM menu in Big Picture), and</item>
/// <item>a hold -> Ctrl+2 (open the right Quick Access menu / QAM).</item>
/// </list>
/// The BACK button is read (not consumed) via XInput. Rather than injecting an
/// OS-level keystroke - which only reaches Steam while Big Picture holds keyboard
/// focus - the shortcut is delivered straight into Steam's Big Picture UI through
/// the CEF debugger, so it triggers Steam's own built-in shortcut handler
/// reliably (see <c>SteamDevToolsClient.SendControlDigitShortcutAsync</c>).
/// </summary>
public sealed class ControllerShortcutService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    // How long BACK must be held before it counts as a "hold" (Ctrl+2) instead
    // of a "short press" (Ctrl+1). Tune to taste.
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(350);

    // XInput button mask for BACK / View (XINPUT_GAMEPAD_BACK).
    private const ushort XinputGamepadBack = 0x0020;

    private const int SteamMenuDigit = 1;    // Ctrl+1 -> STEAM menu (left)
    private const int QuickAccessDigit = 2;  // Ctrl+2 -> Quick Access menu (right)

    private readonly Func<bool> _isEnabled;
    private readonly Func<int, Task> _sendControlDigitAsync;

    private bool _backWasDown;
    private DateTime _backPressedAtUtc;
    private bool _holdActionFired;

    /// <param name="isEnabled">Runtime gate so the feature can be toggled without restarting the host.</param>
    /// <param name="sendControlDigitAsync">
    /// Delivers a Ctrl+&lt;digit&gt; shortcut into Steam's Big Picture UI. Wired to
    /// the DevTools client in the background host.
    /// </param>
    public ControllerShortcutService(Func<bool> isEnabled, Func<int, Task> sendControlDigitAsync)
    {
        _isEnabled = isEnabled;
        _sendControlDigitAsync = sendControlDigitAsync;
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
                catch
                {
                    // A transient controller/input hiccup must never take the
                    // polling loop (or the background host) down.
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

        var backDown = TryReadBackButtonDown();
        var nowUtc = DateTime.UtcNow;

        if (backDown && !_backWasDown)
        {
            // Rising edge: BACK just went down. Start timing the press.
            _backPressedAtUtc = nowUtc;
            _holdActionFired = false;
        }
        else if (backDown && _backWasDown)
        {
            // Still held: once we cross the hold threshold, open Quick Access
            // exactly once and suppress the short-press action on release.
            if (!_holdActionFired && nowUtc - _backPressedAtUtc >= HoldThreshold)
            {
                _holdActionFired = true;
                TriggerShortcut(QuickAccessDigit);
            }
        }
        else if (!backDown && _backWasDown)
        {
            // Falling edge: released. If we never reached the hold threshold,
            // this was a short press -> open the STEAM menu.
            if (!_holdActionFired && nowUtc - _backPressedAtUtc < HoldThreshold)
            {
                TriggerShortcut(SteamMenuDigit);
            }

            _holdActionFired = false;
        }

        _backWasDown = backDown;
    }

    private void ResetState()
    {
        _backWasDown = false;
        _holdActionFired = false;
    }

    private void TriggerShortcut(int digit)
    {
        // Fire-and-forget: the DevTools round-trip must not stall the poll loop.
        _ = InvokeSafelyAsync(digit);
    }

    private async Task InvokeSafelyAsync(int digit)
    {
        try
        {
            await _sendControlDigitAsync(digit);
        }
        catch
        {
            // Delivery failures (e.g. Big Picture not running) are non-fatal.
        }
    }

    private static bool TryReadBackButtonDown()
    {
        // Read the first connected XInput pad. The Claw presents as a single
        // XInput device, so index 0 is the handheld's built-in controller.
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetState(index, out var state) == ErrorSuccess)
            {
                return (state.Gamepad.wButtons & XinputGamepadBack) != 0;
            }
        }

        return false;
    }

    private const uint ErrorSuccess = 0;

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
#pragma warning restore CS0649

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XinputState pState);
}
