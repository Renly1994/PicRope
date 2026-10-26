using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace ScreenLine.Tests
{
    internal static class ScreenshotTests
    {
        static int checks;

        [STAThread]
        public static void Main()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "screenshot-" + Guid.NewGuid().ToString("N"));
            try
            {
                Run(root);
                Console.WriteLine("截图测试：{0} 项通过", checks);
            }
            catch (Exception error)
            {
                Console.WriteLine("截图测试失败：" + error);
                Environment.ExitCode = 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static void Run(string root)
        {
            var store = new StateStore(root);
            string path = ScreenshotCapture.CreatePath(store);
            Check(FileRules.IsImage(path) && Path.GetFileName(path).StartsWith("截图_")
                && path.StartsWith(store.Inbox, StringComparison.OrdinalIgnoreCase), "截图保存到收集目录并使用 PNG 文件名");

            Rectangle bounds = ScreenshotCapture.ToPhysicalBounds(new Rectangle(-1920, 0, 1920, 1080), 1.5,
                new Rect(10, 20, 100, 50));
            Check(bounds == new Rectangle(-1905, 30, 150, 75), "框选坐标按显示器 DPI 换算");

            Rectangle clamped = ScreenshotCapture.ToPhysicalBounds(new Rectangle(0, 0, 100, 100), 2,
                new Rect(-20, -30, 200, 300));
            Check(clamped == new Rectangle(0, 0, 100, 100), "框选范围不会超出当前显示器");

            byte[] png = ScreenshotCapture.Capture(new Rectangle(0, 0, 32, 24));
            Check(png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
                "区域截图返回 PNG 数据");
            using (var stream = new MemoryStream(png))
            using (var image = Image.FromStream(stream))
                Check(image.Width == 32 && image.Height == 24, "区域截图尺寸与框选一致");
        }

        static void Check(bool condition, string title)
        {
            if (!condition) throw new Exception(title);
            checks++;
            Console.WriteLine("通过：" + title);
        }
    }
}
