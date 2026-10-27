using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class EditorResourceProbe
    {
        static ImageEditorWindow editor;
        [STAThread]
        static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Run(); }
                catch (Exception error) { Console.WriteLine("编辑器资源验证失败：" + error.Message); Environment.ExitCode = 1; }
                finally { if (editor != null) editor.CloseWithoutPrompt(); app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "editor-resource-" + Guid.NewGuid().ToString("N"));
            var store = new StateStore(root); Directory.CreateDirectory(root);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 1280, 720));
            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            string path = Path.Combine(root, "sample.png"); File.WriteAllBytes(path, ImageContent.EncodeClipboard(bitmap));
            object before = Sample();
            editor = new ImageEditorWindow(path, store, delegate { return Task.FromResult(0); }, delegate { });
            editor.Title = "图绳 · 编辑器资源验证"; editor.Show();
            Task ready = editor.Ready;
            if (await Task.WhenAny(ready, Task.Delay(20000)) != ready) throw new Exception("编辑器加载超时");
            await ready; await Task.Delay(1000); object opened = Sample();
            editor.CloseWithoutPrompt(); editor = null; await Task.Delay(7000); object closed = Sample();
            var json = new JavaScriptSerializer(); string report = json.Serialize(new { before, opened, closedAfterSevenSeconds = closed });
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "performance"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "editor-review.json"), report); Console.WriteLine(report);
        }

        static object Sample()
        {
            int root = Process.GetCurrentProcess().Id;
            var parents = new Dictionary<int, int>();
            using (var query = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
            using (var results = query.Get())
                foreach (ManagementObject row in results) { parents[Convert.ToInt32(row["ProcessId"])] = Convert.ToInt32(row["ParentProcessId"]); row.Dispose(); }
            var ids = new HashSet<int> { root }; int previous;
            do { previous = ids.Count; foreach (var pair in parents) if (ids.Contains(pair.Value)) ids.Add(pair.Key); } while (ids.Count != previous);
            long working = 0, privateBytes = 0; int count = 0;
            foreach (int id in ids)
                try { using (var process = Process.GetProcessById(id)) { working += process.WorkingSet64; privateBytes += process.PrivateMemorySize64; count++; } }
                catch (ArgumentException) { }
            return new { processes = count, workingSetMB = Math.Round(working / 1048576.0, 1), privateMB = Math.Round(privateBytes / 1048576.0, 1) };
        }
    }
}
