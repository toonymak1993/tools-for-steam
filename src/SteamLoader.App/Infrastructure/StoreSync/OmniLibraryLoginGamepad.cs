using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Controller navigation for the OmniLibrary sign-in window.
///
/// Store sign-in pages are made for mouse and keyboard. When Steam Input
/// does not turn the controller into a mouse for this window (Xbox pads with
/// Steam Input off, TFS's own virtual pad), XInput reaches us directly, and
/// this class drives the page: D-pad / left stick move between fields and
/// buttons, A selects, B goes back, X opens the on-screen keyboard, Start
/// submits the form, LB/RB tab, the right stick scrolls. While Steam Input
/// maps the pad to mouse and keyboard, XInput stays silent and nothing here
/// fires twice.
/// </summary>
internal sealed class OmniLibraryLoginGamepad : IDisposable
{
    /// <summary>
    /// Injected into every document. Spatial navigation over the page's own
    /// focusable elements, with a visible focus ring. Returns short status
    /// strings to the host.
    /// </summary>
    public const string NavigationScript = """
        (() => {
          if (window.__tfsLoginPad) return;
          const ringId = "tfs-login-pad-style";
          const selector = [
            "a[href]", "button", "input:not([type=hidden])", "select", "textarea",
            "[tabindex]:not([tabindex='-1'])", "[role=button]", "[role=link]",
            "[role=checkbox]", "[role=tab]", "[contenteditable=true]"
          ].join(",");
          function ensureStyle() {
            if (document.getElementById(ringId) || !document.head) return;
            const style = document.createElement("style");
            style.id = ringId;
            style.textContent = "[data-tfs-pad-focus]{outline:3px solid #1a9fff !important;outline-offset:3px !important;box-shadow:0 0 0 6px rgba(26,159,255,.25) !important;border-radius:4px;}";
            document.head.appendChild(style);
          }
          function visible(el) {
            if (!el || el.disabled || el.getAttribute("aria-hidden") === "true") return false;
            const r = el.getBoundingClientRect();
            if (r.width < 2 || r.height < 2) return false;
            const cs = getComputedStyle(el);
            return cs.visibility !== "hidden" && cs.display !== "none" && Number(cs.opacity) > 0.05;
          }
          function candidates() {
            return [...document.querySelectorAll(selector)].filter(visible);
          }
          function current() {
            const el = document.activeElement;
            return el && el !== document.body && el !== document.documentElement && visible(el) ? el : null;
          }
          function mark(el) {
            ensureStyle();
            for (const old of document.querySelectorAll("[data-tfs-pad-focus]")) old.removeAttribute("data-tfs-pad-focus");
            if (!el) return;
            el.setAttribute("data-tfs-pad-focus", "");
            try { el.focus({ preventScroll: true }); } catch { el.focus(); }
            el.scrollIntoView({ block: "nearest", inline: "nearest" });
          }
          function isText(el) {
            if (!el) return false;
            if (el.isContentEditable || el.tagName === "TEXTAREA") return true;
            if (el.tagName !== "INPUT") return false;
            const type = (el.getAttribute("type") || "text").toLowerCase();
            return ["text", "email", "password", "search", "tel", "url", "number"].includes(type);
          }
          function first() {
            const list = candidates();
            const text = list.find((el) => isText(el) && !el.value);
            return text || list.find(isText) || list[0] || null;
          }
          function move(dir) {
            const from = current();
            if (!from) { const f = first(); mark(f); return f ? "focus" : "none"; }
            const a = from.getBoundingClientRect();
            const ax = a.left + a.width / 2, ay = a.top + a.height / 2;
            let best = null, bestScore = Infinity;
            for (const el of candidates()) {
              if (el === from || from.contains(el) || el.contains(from)) continue;
              const b = el.getBoundingClientRect();
              const bx = b.left + b.width / 2, by = b.top + b.height / 2;
              const dx = bx - ax, dy = by - ay;
              let main, cross;
              if (dir === "down") { if (b.top < a.bottom - 4 && dy <= 4) continue; main = dy; cross = Math.abs(dx); }
              else if (dir === "up") { if (b.bottom > a.top + 4 && dy >= -4) continue; main = -dy; cross = Math.abs(dx); }
              else if (dir === "right") { if (dx <= 4) continue; main = dx; cross = Math.abs(dy); }
              else { if (dx >= -4) continue; main = -dx; cross = Math.abs(dy); }
              if (main <= 0) continue;
              const score = main + cross * 2.5;
              if (score < bestScore) { bestScore = score; best = el; }
            }
            if (best) { mark(best); return "focus"; }
            if (dir === "down" || dir === "up") {
              window.scrollBy({ top: (dir === "down" ? 1 : -1) * innerHeight * 0.4, behavior: "smooth" });
              return "scroll";
            }
            return "none";
          }
          function tab(step) {
            const list = candidates();
            if (!list.length) return "none";
            const index = list.indexOf(current());
            const next = list[(index + step + list.length) % list.length];
            mark(next);
            return "focus";
          }
          function activate() {
            let el = current();
            if (!el) { el = first(); mark(el); if (!el) return "none"; }
            mark(el);
            if (isText(el)) return "text";
            if (el.tagName === "SELECT") {
              const step = el.selectedIndex + 1 < el.options.length ? 1 : -el.selectedIndex;
              el.selectedIndex += step;
              el.dispatchEvent(new Event("change", { bubbles: true }));
              return "select";
            }
            el.click();
            return "click";
          }
          function submit() {
            const el = current();
            const form = el?.form || el?.closest?.("form") || document.querySelector("form");
            if (form) {
              const button = form.querySelector("button[type=submit],input[type=submit],button:not([type])");
              if (button && visible(button)) { button.click(); return "submit"; }
              if (typeof form.requestSubmit === "function") { form.requestSubmit(); return "submit"; }
            }
            if (el && !isText(el)) { el.click(); return "click"; }
            return "none";
          }
          function scroll(dy) { window.scrollBy({ top: dy, behavior: "auto" }); return "scroll"; }
          function focusedIsText() { return isText(current()); }
          // When Steam Input already turns the pad into keys (desktop layout),
          // the same press arrives as a key too. Skip ours if it did.
          let lastKey = { key: "", at: 0 };
          addEventListener("keydown", (e) => { lastKey = { key: e.key, at: performance.now() }; }, true);
          const twins = {
            up: ["ArrowUp"], down: ["ArrowDown"], left: ["ArrowLeft"], right: ["ArrowRight"],
            tab: ["Tab"], backtab: ["Tab"], a: ["Enter", " "], b: ["Escape"], start: ["Enter"]
          };
          function arrivedAsKey(action) {
            const keys = twins[action];
            return !!keys && performance.now() - lastKey.at < 300 && keys.includes(lastKey.key);
          }
          function run(action) {
            if (arrivedAsKey(action)) return "skipped";
            switch (action) {
              case "up": case "down": case "left": case "right": return move(action);
              case "tab": return tab(1);
              case "backtab": return tab(-1);
              case "a": return activate();
              case "start": return submit();
              case "b": return "back";
              default:
                if (action.startsWith("scroll:")) return scroll(Number(action.slice(7)) || 0);
                return "none";
            }
          }
          window.__tfsLoginPad = { run, move, tab, activate, submit, scroll, focusedIsText, first: () => { const f = first(); mark(f); return f ? "focus" : "none"; } };
        })();
        """;

    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort DpadRight = 0x0008;
    private const ushort StartButton = 0x0010;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort ButtonA = 0x1000;
    private const ushort ButtonB = 0x2000;
    private const ushort ButtonX = 0x4000;
    private const short StickThreshold = 16000;
    private const uint ErrorSuccess = 0;
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(130);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isActive;
    private readonly Action<string> _onAction;
    private ushort _previous;
    private ushort _repeatMask;
    private DateTime _repeatAt;
    private bool _xinputMissing;

    /// <param name="isActive">True while the sign-in window is in the foreground.</param>
    /// <param name="onAction">
    /// Receives "up", "down", "left", "right", "tab", "backtab", "a", "b",
    /// "x", "start" and "scroll:&lt;pixels&gt;".
    /// </param>
    public OmniLibraryLoginGamepad(Dispatcher dispatcher, Func<bool> isActive, Action<string> onAction)
    {
        _isActive = isActive;
        _onAction = onAction;
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        if (_xinputMissing)
        {
            return;
        }

        ushort buttons = 0;
        short rightY = 0;
        try
        {
            for (uint index = 0; index < 4; index++)
            {
                if (XInputGetState(index, out var state) != ErrorSuccess)
                {
                    continue;
                }

                var pad = state.Gamepad;
                buttons |= pad.Buttons;
                if (pad.ThumbLY > StickThreshold) buttons |= DpadUp;
                if (pad.ThumbLY < -StickThreshold) buttons |= DpadDown;
                if (pad.ThumbLX > StickThreshold) buttons |= DpadRight;
                if (pad.ThumbLX < -StickThreshold) buttons |= DpadLeft;
                if (Math.Abs((int)pad.ThumbRY) > Math.Abs((int)rightY)) rightY = pad.ThumbRY;
            }
        }
        catch (DllNotFoundException)
        {
            _xinputMissing = true;
            return;
        }
        catch (EntryPointNotFoundException)
        {
            _xinputMissing = true;
            return;
        }

        if (!_isActive())
        {
            // Buttons held while another window had focus must not fire on return.
            _previous = buttons;
            _repeatMask = 0;
            return;
        }

        var pressed = (ushort)(buttons & ~_previous);
        _previous = buttons;
        var now = DateTime.UtcNow;

        foreach (var (mask, action) in DirectionalActions)
        {
            if ((pressed & mask) != 0)
            {
                _onAction(action);
                _repeatMask = mask;
                _repeatAt = now + RepeatDelay;
            }
        }

        if (_repeatMask != 0)
        {
            if ((buttons & _repeatMask) == 0)
            {
                _repeatMask = 0;
            }
            else if (now >= _repeatAt)
            {
                _onAction(DirectionalActions.First(entry => entry.Mask == _repeatMask).Action);
                _repeatAt = now + RepeatInterval;
            }
        }

        if ((pressed & ButtonA) != 0) _onAction("a");
        if ((pressed & ButtonB) != 0) _onAction("b");
        if ((pressed & ButtonX) != 0) _onAction("x");
        if ((pressed & StartButton) != 0) _onAction("start");

        if (Math.Abs((int)rightY) > StickThreshold)
        {
            // Proportional scroll, up to ~28 px per 16 ms tick.
            var pixels = (int)(-rightY / 1170.0);
            _onAction($"scroll:{pixels}");
        }
    }

    private static readonly (ushort Mask, string Action)[] DirectionalActions =
    {
        (DpadUp, "up"),
        (DpadDown, "down"),
        (DpadLeft, "left"),
        (DpadRight, "right"),
        (LeftShoulder, "backtab"),
        (RightShoulder, "tab"),
    };

    /// <summary>
    /// Shows the Windows touch keyboard (TabTip) when it is not already
    /// open; falls back to the classic on-screen keyboard. Returns whether a
    /// keyboard was asked to open.
    /// </summary>
    public static bool ShowTouchKeyboard(bool toggle = false)
    {
        try
        {
            if (!toggle && IsTouchKeyboardVisible())
            {
                return true;
            }

            EnsureTabTipRunning();
            var type = Type.GetTypeFromCLSID(new Guid("4ce576fa-83dc-4f88-951c-9d0782b4e376"));
            if (type is not null && Activator.CreateInstance(type) is ITipInvocation invocation)
            {
                try
                {
                    invocation.Toggle(GetDesktopWindow());
                    return true;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(invocation);
                }
            }
        }
        catch
        {
            // Fall through to the classic keyboard.
        }

        try
        {
            Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Closes the touch keyboard if it is open. Returns whether it was.</summary>
    public static bool HideTouchKeyboard()
    {
        if (!IsTouchKeyboardVisible())
        {
            return false;
        }

        return ShowTouchKeyboard(toggle: true);
    }

    private static void EnsureTabTipRunning()
    {
        if (Process.GetProcessesByName("TabTip").Length > 0)
        {
            return;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "microsoft shared",
            "ink",
            "TabTip.exe");
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Windows 10 draws the touch keyboard in "IPTip_Main_Window"; Windows 11
    /// in a CoreWindow titled "Microsoft Text Input Application" that stays
    /// alive but cloaked while hidden.
    /// </summary>
    private static bool IsTouchKeyboardVisible()
    {
        var legacy = FindWindow("IPTip_Main_Window", null);
        if (legacy != IntPtr.Zero && IsWindowVisible(legacy) && !IsCloaked(legacy))
        {
            return true;
        }

        var visible = false;
        EnumWindows(
            (handle, _) =>
            {
                var className = new StringBuilder(64);
                GetClassName(handle, className, className.Capacity);
                if (!className.ToString().Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
                {
                    return true;
                }

                var title = new StringBuilder(128);
                GetWindowText(handle, title, title.Capacity);
                if (title.ToString().Contains("Text Input", StringComparison.OrdinalIgnoreCase) &&
                    IsWindowVisible(handle) &&
                    !IsCloaked(handle))
                {
                    visible = true;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return visible;
    }

    private static bool IsCloaked(IntPtr handle)
    {
        try
        {
            return DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    [ComImport]
    [Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr windowHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
