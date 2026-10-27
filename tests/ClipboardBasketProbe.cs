using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenLine.Tests
{
    internal static class ClipboardBasketProbe
    {
        [STAThread]
        static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("当前微信图片的入篮流程验证通过。"); }
                catch (Exception error) { Console.WriteLine("微信图片入篮验证失败：" + error.Message); Environment.ExitCode = 1; }
                finally { app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Run()
        {
            uint sequence = WindowsNative.GetClipboardSequenceNumber();
            var data = Clipboard.GetDataObject();
            string source = WindowsNative.ClipboardOwnerName();
            if (data == null || !ClipboardRules.IsImage(data, source)) throw new Exception("当前剪贴板不是可识别的图片");
            string parent = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data"));
            string root = Path.GetFullPath(Path.Combine(parent, "clipboard-probe-" + Guid.NewGuid().ToString("N")));
            if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("测试目录无效");
            Directory.CreateDirectory(root);
            CapturePipeline pipeline = null;
            try
            {
                var store = new StateStore(Path.Combine(root, "profile"));
                store.State.CollectClipboard = true; store.State.AutoHang = false;
                var gallery = new Gallery(store);
                pipeline = new CapturePipeline(gallery, Application.Current.Dispatcher, Path.Combine(root, "watched"));
                await pipeline.Start();
                typeof(CapturePipeline).GetField("clipboardSequence", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pipeline, sequence == 0 ? 1u : sequence - 1);
                pipeline.ClipboardChanged();
                for (int i = 0; i < 80 && gallery.Collected.Count == 0; i++) await Task.Delay(50);
                if (WindowsNative.GetClipboardSequenceNumber() != sequence) throw new Exception("验证期间剪贴板内容发生变化");
                if (gallery.Collected.Count != 1 || gallery.Items.Count != 0 || !File.Exists(gallery.Collected[0].Path))
                    throw new Exception("图片未进入篮子");
            }
            finally
            {
                if (pipeline != null) pipeline.Dispose();
                Directory.Delete(root, true);
            }
        }
    }
}
