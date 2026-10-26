using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal static class WindowsNative
    {
        public const int ShowMessage = 0x8001;
        public const int ExitMessage = 0x8002;
        public const string MessageWindow = "ScreenLine.Windows.Messages";
        public static readonly IntPtr MessageParent = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct Rectangle { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folder, uint flags, IntPtr token, out IntPtr path);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string className, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint target, bool attach);

        public static void ActivateEditor(IntPtr window)
        {
            uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
            try { SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(current, foreground, false); }
        }

        public static Point Cursor() { Point point; GetCursorPos(out point); return point; }

        public static string WindowTitle(IntPtr window)
        {
            var text = new StringBuilder(1024);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        public static Forms.Screen[] Screens { get { return Forms.Screen.AllScreens; } }

        public static Forms.Screen ScreenAtPointer()
        {
            var point = Cursor();
            return Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
        }

        public static double MonitorScale(Forms.Screen screen)
        {
            var point = new Point { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 };
            uint x, y;
            return GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out x, out y) == 0 ? x / 96.0 : 1;
        }

        public static void PrepareOverlay(IntPtr window)
        {
            // 透明动效覆盖屏幕时仍是叠加层，避免任务栏将其当作全屏应用。
            SetProp(window, "NonRudeHWND", new IntPtr(1));
            SetWindowLong(window, -20, GetWindowLong(window, -20) | 0x08000000 | 0x80);
        }

        public static void PassMouse(IntPtr window, bool pass)
        {
            int before = GetWindowLong(window, -20);
            int after = pass ? before | 0x20 : before & ~0x20;
            if (after != before) SetWindowLong(window, -20, after);
        }

        public static bool FullScreen(Forms.Screen screen, IntPtr overlay)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == overlay || foreground == GetShellWindow()) return false;
            var name = new StringBuilder(128);
            GetClassName(foreground, name, name.Capacity);
            string type = name.ToString();
            if (type == "WorkerW" || type == "Progman" || type.StartsWith("Shell_")) return false;
            Rectangle rectangle;
            if (!GetWindowRect(foreground, out rectangle)) return false;
            var bounds = screen.Bounds;
            return rectangle.Left <= bounds.Left && rectangle.Top <= bounds.Top && rectangle.Right >= bounds.Right && rectangle.Bottom >= bounds.Bottom;
        }

        public static string ScreenshotFolder()
        {
            IntPtr path;
            if (SHGetKnownFolderPath(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"), 0, IntPtr.Zero, out path) == 0)
            {
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }

        public static void ShowExisting()
        {
            PostMessage(FindWindowEx(MessageParent, IntPtr.Zero, null, MessageWindow), ShowMessage, IntPtr.Zero, IntPtr.Zero);
        }

        public static void RequestExit()
        {
            PostMessage(FindWindowEx(MessageParent, IntPtr.Zero, null, MessageWindow), ExitMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal sealed class WindowsEvents : IDisposable
    {
        readonly HwndSource source;
        public readonly bool ShortcutAvailable;
        public readonly bool ScreenshotShortcutAvailable;
        public event Action ClipboardChanged;
        public event Action ToggleRequested;
        public event Action ScreenshotRequested;
        public event Action ExitRequested;

        public WindowsEvents()
        {
            source = new HwndSource(new HwndSourceParameters(WindowsNative.MessageWindow) { ParentWindow = WindowsNative.MessageParent });
            source.AddHook(Receive);
            if (!WindowsNative.AddClipboardFormatListener(source.Handle)) throw new System.ComponentModel.Win32Exception();
            ShortcutAvailable = WindowsNative.RegisterHotKey(source.Handle, 1, 0x4003, (uint)'T');
            ScreenshotShortcutAvailable = WindowsNative.RegisterHotKey(source.Handle, 2, 0x4003, (uint)'S');
        }

        IntPtr Receive(IntPtr window, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if (message == 0x031D && ClipboardChanged != null) ClipboardChanged();
            if (message == 0x0312)
            {
                int id = w.ToInt32();
                if (id == 1 && ToggleRequested != null) ToggleRequested();
                else if (id == 2 && ScreenshotRequested != null) ScreenshotRequested();
            }
            if (message == WindowsNative.ShowMessage && ToggleRequested != null) ToggleRequested();
            if (message == WindowsNative.ExitMessage && ExitRequested != null) ExitRequested();
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            WindowsNative.RemoveClipboardFormatListener(source.Handle);
            if (ShortcutAvailable) WindowsNative.UnregisterHotKey(source.Handle, 1);
            if (ScreenshotShortcutAvailable) WindowsNative.UnregisterHotKey(source.Handle, 2);
            source.Dispose();
        }
    }
}
