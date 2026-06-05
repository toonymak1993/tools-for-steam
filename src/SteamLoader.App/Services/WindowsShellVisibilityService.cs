using System.Runtime.InteropServices;

namespace SteamLoader.App.Services;

public sealed class WindowsShellVisibilityService
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    private readonly object _gate = new();
    private readonly HashSet<nint> _hiddenHandles = [];

    public bool IsHidden
    {
        get
        {
            lock (_gate)
            {
                return _hiddenHandles.Count > 0;
            }
        }
    }

    public void HideShellChrome()
    {
        lock (_gate)
        {
            foreach (var handle in FindShellChromeHandles())
            {
                if (handle == 0 || !IsWindowVisible(handle))
                {
                    continue;
                }

                ShowWindow(handle, SwHide);
                _hiddenHandles.Add(handle);
            }
        }
    }

    public void RestoreShellChrome()
    {
        lock (_gate)
        {
            foreach (var handle in _hiddenHandles.Concat(FindShellChromeHandles()).Distinct())
            {
                if (handle != 0)
                {
                    ShowWindow(handle, SwShow);
                }
            }

            _hiddenHandles.Clear();
        }
    }

    private static IEnumerable<nint> FindShellChromeHandles()
    {
        var handles = new List<nint>();

        AddIfValid(handles, FindWindow("Shell_TrayWnd", null));

        nint secondaryTaskbar = 0;
        while (true)
        {
            secondaryTaskbar = FindWindowEx(0, secondaryTaskbar, "Shell_SecondaryTrayWnd", null);
            if (secondaryTaskbar == 0)
            {
                break;
            }

            AddIfValid(handles, secondaryTaskbar);
        }

        foreach (var desktopHandle in FindDesktopIconHandles())
        {
            AddIfValid(handles, desktopHandle);
        }

        return handles.Distinct();
    }

    private static IEnumerable<nint> FindDesktopIconHandles()
    {
        var handles = new List<nint>();
        AddDesktopIconHandlesFromParent(handles, FindWindow("Progman", null));

        nint worker = 0;
        while (true)
        {
            worker = FindWindowEx(0, worker, "WorkerW", null);
            if (worker == 0)
            {
                break;
            }

            AddDesktopIconHandlesFromParent(handles, worker);
        }

        return handles;
    }

    private static void AddDesktopIconHandlesFromParent(List<nint> handles, nint parent)
    {
        if (parent == 0)
        {
            return;
        }

        var shellView = FindWindowEx(parent, 0, "SHELLDLL_DefView", null);
        if (shellView == 0)
        {
            return;
        }

        AddIfValid(handles, shellView);
        AddIfValid(handles, FindWindowEx(shellView, 0, "SysListView32", "FolderView"));
    }

    private static void AddIfValid(List<nint> handles, nint handle)
    {
        if (handle != 0)
        {
            handles.Add(handle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint parentHandle, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);
}
