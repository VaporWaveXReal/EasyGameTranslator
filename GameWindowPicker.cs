using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EasyGameTranslator;

public sealed record GameWindowInfo(IntPtr Handle, string Title)
{
    public bool TryGetBounds(out System.Drawing.Rectangle bounds)
    {
        if (!IsWindow(Handle) || IsIconic(Handle) || !GetWindowRect(Handle, out var rect))
        {
            bounds = default;
            return false;
        }

        bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 100 && bounds.Height > 100;
    }

    public static IReadOnlyList<GameWindowInfo> GetOpenWindows()
    {
        var currentProcess = Environment.ProcessId;
        var results = new List<GameWindowInfo>();
        EnumWindows((handle, lParam) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero)
                return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == currentProcess)
                return true;

            var length = GetWindowTextLength(handle);
            if (length == 0)
                return true;

            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (TryGetBoundsStatic(handle, out _))
                results.Add(new GameWindowInfo(handle, title.ToString()));
            return true;
        }, IntPtr.Zero);

        return results.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool TryGetBoundsStatic(IntPtr handle, out System.Drawing.Rectangle bounds)
    {
        if (!GetWindowRect(handle, out var rect))
        {
            bounds = default;
            return false;
        }
        bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 100 && bounds.Height > 100;
    }

    private const uint GwOwner = 4;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
