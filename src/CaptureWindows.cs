using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal static class CaptureWindows
    {
        delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visitor, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out WindowsNative.Rectangle value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        internal static List<Rect> Snapshot(Forms.Screen screen, uint excludedProcess)
        {
            var result = new List<Rect>();
            var bounds = screen.Bounds;
            var screenRect = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint process;
                GetWindowThreadProcessId(window, out process);
                if (process == excludedProcess || !IsWindowVisible(window) || IsIconic(window) || (GetWindowLong(window, -20) & 0x20) != 0) return true;
                var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
                string type = name.ToString();
                if (type == "Progman" || type == "WorkerW" || (type.StartsWith("Shell_") && type != "Shell_TrayWnd" && type != "Shell_SecondaryTrayWnd")) return true;
                int cloaked;
                if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                WindowsNative.Rectangle frame;
                if (DwmGetWindowAttribute(window, 9, out frame, Marshal.SizeOf(typeof(WindowsNative.Rectangle))) != 0 && !WindowsNative.GetWindowRect(window, out frame)) return true;
                if (frame.Right <= frame.Left || frame.Bottom <= frame.Top) return true;
                var region = new Rect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
                region.Intersect(screenRect);
                if (region.IsEmpty || region.Width < 4 || region.Height < 4) return true;
                region.Offset(-bounds.Left, -bounds.Top); result.Add(region);
                return true;
            }, IntPtr.Zero);
            var work = screen.WorkingArea;
            var desktop = new Rect(work.Left, work.Top, work.Width, work.Height);
            desktop.Intersect(screenRect);
            if (!desktop.IsEmpty) { desktop.Offset(-bounds.Left, -bounds.Top); result.Add(desktop); }
            return result;
        }

        internal static List<Rect> Snapshot(Forms.Screen screen) { return Snapshot(screen, (uint)Process.GetCurrentProcess().Id); }
        internal static Rect At(IList<Rect> windows, Point point)
        {
            foreach (var region in windows) if (region.Contains(point)) return region;
            return Rect.Empty;
        }
    }
}
