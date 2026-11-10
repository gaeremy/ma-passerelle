// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MaPasserelle.Capture;

/// <summary>Un écran branché. <see cref="Index"/> commence à 1, l'écran principal en premier.</summary>
public sealed record ScreenInfo(int Index, nint Handle, string Device, int X, int Y, int Width, int Height, bool Primary);

/// <summary>Une fenêtre que l'on peut partager.</summary>
public sealed record WindowInfo(nint Handle, string Title, string Process, int Width, int Height, bool Minimized);

/// <summary>Liste les écrans et les fenêtres partageables.</summary>
public static class DesktopCatalog
{
    /// <summary>À appeler une fois au démarrage : sans cela Windows renvoie des tailles mises à l'échelle.</summary>
    public static void UseRealPixels() => SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2

    public static IReadOnlyList<ScreenInfo> Screens()
    {
        var found = new List<(nint Handle, MonitorInfoEx Info)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info)) found.Add((monitor, info));
            return true;
        }, 0);

        return [.. found
            .OrderByDescending(m => (m.Info.Flags & 1) != 0)
            .ThenBy(m => m.Info.Monitor.Left).ThenBy(m => m.Info.Monitor.Top)
            .Select((m, i) => new ScreenInfo(i + 1, m.Handle, m.Info.Device,
                m.Info.Monitor.Left, m.Info.Monitor.Top,
                m.Info.Monitor.Right - m.Info.Monitor.Left, m.Info.Monitor.Bottom - m.Info.Monitor.Top,
                (m.Info.Flags & 1) != 0))];
    }

    /// <summary>Les fenêtres principales visibles, comme dans Alt+Tab.</summary>
    public static IReadOnlyList<WindowInfo> Windows()
    {
        var result = new List<WindowInfo>();
        int self = Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            if (GetWindow(window, 4 /* GW_OWNER */) != 0) return true;
            long exStyle = GetWindowLongPtr(window, -20 /* GWL_EXSTYLE */);
            if ((exStyle & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            // Fenêtres « masquées » par Windows : autres bureaux virtuels, applications suspendues.
            if (DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            int length = GetWindowTextLength(window);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);

            GetWindowThreadProcessId(window, out int pid);
            if (pid == self) return true;
            string process = "";
            try { process = Process.GetProcessById(pid).ProcessName; } catch (Exception) { }

            GetWindowRect(window, out Rect r);
            result.Add(new WindowInfo(window, title.ToString(), process, r.Right - r.Left, r.Bottom - r.Top, IsIconic(window)));
            return true;
        }, 0);
        return result;
    }

    public static bool IsWindow(nint handle) => IsWindowNative(handle);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);
    private delegate bool WindowEnumProc(nint window, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool IsWindowNative(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
