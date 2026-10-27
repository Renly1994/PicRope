using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class CaptureWindowTests
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string type, string title);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        static int checks;

        [STAThread]
        static void Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            if (args.Contains("--fixture")) { app.Startup += delegate { Fixture(app); }; app.Run(); return; }
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("截图窗口：{0} 项验证通过", checks); }
                catch (Exception error) { Console.WriteLine("截图验证失败：" + error); Environment.ExitCode = 1; }
                finally { app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static void Fixture(Application app)
        {
            var screen = Forms.Screen.PrimaryScreen.Bounds;
            var lower = new Window { Title = "图绳截图测试窗口甲", WindowStyle = WindowStyle.None, Background = Brushes.CornflowerBlue, Topmost = true, ShowInTaskbar = false };
            var upper = new Window { Title = "图绳截图测试窗口乙", WindowStyle = WindowStyle.None, Background = Brushes.Coral, Topmost = true, ShowInTaskbar = false };
            lower.Show(); upper.Show();
            WindowsNative.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(lower).Handle, new IntPtr(-1), screen.Left + 100, screen.Top + 100, 300, 220, 0x10);
            WindowsNative.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(upper).Handle, new IntPtr(-1), screen.Left + 220, screen.Top + 150, 300, 220, 0x10);
            upper.Closed += delegate { lower.Close(); app.Shutdown(); };
        }

        static async Task Run()
        {
            var cursor = WindowsNative.Cursor(); IntPtr foreground = WindowsNative.GetForegroundWindow();
            var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--fixture") { UseShellExecute = false });
            CaptureWindow capture = null;
            try
            {
                for (int i = 0; i < 60 && FindWindow(null, "图绳截图测试窗口乙") == IntPtr.Zero; i++) await Task.Delay(50);
                await Task.Delay(150);
                var screen = Forms.Screen.PrimaryScreen;
                var candidates = CaptureWindows.Snapshot(screen, 0);
                var work = screen.WorkingArea;
                var desktop = new Rect(work.Left - screen.Bounds.Left, work.Top - screen.Bounds.Top, work.Width, work.Height);
                Check(candidates.Count > 0 && candidates[candidates.Count - 1] == desktop, "桌面工作区作为窗口识别的最后候选项");
                var desktopOnly = new[] { new Rect(10, 10, 100, 100), desktop };
                Check(CaptureWindows.At(desktopOnly, new Point(desktop.Right - 2, desktop.Bottom - 2)) == desktop, "空白桌面选择桌面区域");
                Check(CaptureWindows.At(desktopOnly, new Point(20, 20)) == desktopOnly[0], "桌面区域不会遮盖前面的窗口");
                WindowsNative.Rectangle taskbar = new WindowsNative.Rectangle();
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                Check(taskbarHandle != IntPtr.Zero && WindowsNative.GetWindowRect(taskbarHandle, out taskbar), "找到主屏任务栏");
                var taskbarBounds = new Rect(taskbar.Left - screen.Bounds.Left, taskbar.Top - screen.Bounds.Top,
                    taskbar.Right - taskbar.Left, taskbar.Bottom - taskbar.Top);
                var taskbarPoint = new Point(taskbarBounds.Left + taskbarBounds.Width / 2, taskbarBounds.Top + taskbarBounds.Height / 2);
                Check(CaptureWindows.At(candidates, taskbarPoint) == taskbarBounds, "识别实际任务栏边界");
                var lower = CaptureWindows.At(candidates, new Point(130, 130));
                var upper = CaptureWindows.At(candidates, new Point(250, 180));
                Check(lower.Left >= 100 && lower.Left < 120 && lower.Top == 100 && lower.Width > 250 && lower.Height > 180, "识别被部分遮挡的窗口边界");
                Check(upper.Left >= 220 && upper.Left < 240 && upper.Top == 150 && upper.Width > 250 && upper.Height > 180, "重叠位置优先识别前方窗口");
                Check(CaptureWindows.At(new[] { new Rect(-20, -10, 50, 50), new Rect(0, 0, 100, 100) }, new Point(10, 10)) == new Rect(-20, -10, 50, 50), "按窗口叠放顺序选择");
                int w = screen.Bounds.Width, h = screen.Bounds.Height;
                var pixels = new byte[w * h * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 185; pixels[i + 1] = 211; pixels[i + 2] = 225; pixels[i + 3] = 255; }
                var picture = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4); picture.Freeze();
                var narrow = new CaptureSurface { Picture = picture, Selection = new Rect(0, 0, 100, 100) };
                narrow.Marks.Add(new CaptureMark { Tool = "马赛克", Start = new Point(10, 10), End = new Point(12, 90), Size = 3 });
                Check(narrow.Export().PixelWidth == 100, "窄条马赛克可正常导出");
                capture = new CaptureWindow(screen, picture); var completion = capture.PickAsync(); await Task.Delay(200);
                SetCursorPos(screen.Bounds.Left + 250, screen.Bounds.Top + 180); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
                Check(capture.Selection == upper, "单击自动识别的窗口并建立选区");
                Check(capture.Surface.Export().PixelWidth == (int)upper.Width && capture.Surface.Export().PixelHeight == (int)upper.Height, "自动选区导出尺寸正确");
                Check(capture.Tools.Values.All(button => button.Content is Viewbox && button.ToolTip != null), "编辑工具使用图标并提供提示");
                SetCursorPos(screen.Bounds.Left + 130, screen.Bounds.Top + 130); await Task.Delay(40);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check(capture.Selection == lower, "点击其他窗口可以切换选区");
                SetCursorPos(screen.Bounds.Left + 480, screen.Bounds.Top + 180); await Task.Delay(40);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check(capture.Selection == upper, "切换窗口时保留前后顺序");
                capture.Tools["矩形"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SetCursorPos(screen.Bounds.Left + 260, screen.Bounds.Top + 190); await Task.Delay(40); mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(screen.Bounds.Left + 360, screen.Bounds.Top + 260); await Task.Delay(80); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check(capture.Surface.Marks.Count == 1 && capture.Surface.Marks[0].Tool == "矩形", "图标工具可以绘制标注");
                capture.Undo(); Check(capture.Surface.Marks.Count == 0, "撤销图标对应操作正常");
                capture.Redo(); Check(capture.Surface.Marks.Count == 1, "重做图标对应操作正常");
                capture.Select(new Rect(200, 180, 680, 400));
                capture.UpdateLayout();
                var visual = new RenderTargetBitmap((int)capture.ActualWidth, (int)capture.ActualHeight, 96, 96, PixelFormats.Pbgra32); visual.Render(capture);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(visual));
                using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture-toolbar-check.png"))) encoder.Save(file);
                capture.Tools["完成"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var result = await completion;
                Check(result != null && result.PixelWidth == 680 && result.PixelHeight == 400, "完成截图返回编辑后的选区");
                capture = null;
                capture = new CaptureWindow(screen, picture); capture.Show(); await Task.Delay(100);
                SetCursorPos(screen.Bounds.Left + 50, screen.Bounds.Top + 400); await Task.Delay(40); mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(screen.Bounds.Left + 190, screen.Bounds.Top + 510); await Task.Delay(80); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                Check(capture.Selection == new Rect(50, 400, 140, 110), "拖拽仍可自由框选");
                capture.Close();
                capture = new CaptureWindow(screen, picture); capture.Show(); await Task.Delay(100);
                SetCursorPos(screen.Bounds.Left + (int)taskbarPoint.X, screen.Bounds.Top + (int)taskbarPoint.Y); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                Check(capture.Selection == taskbarBounds, "单击任务栏即可选中完整区域");
            }
            finally
            {
                if (capture != null) capture.Close();
                if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(1500)) process.Kill(); }
                SetCursorPos(cursor.X, cursor.Y); if (foreground != IntPtr.Zero) WindowsNative.ActivateEditor(foreground);
                process.Dispose();
            }
        }

        static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
    }
}
