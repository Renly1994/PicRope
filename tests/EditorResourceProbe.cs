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
        static bool exportReview;
        [STAThread]
        static void Main(string[] arguments)
        {
            exportReview = arguments.Contains("--export-review");
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
            int width = exportReview ? 3840 : 1280, height = exportReview ? 2160 : 720;
            using (var context = drawing.RenderOpen()) context.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            string path = Path.Combine(root, "sample.png"); File.WriteAllBytes(path, ImageContent.EncodeClipboard(bitmap));
            object before = Sample();
            var openingClock = Stopwatch.StartNew();
            editor = new ImageEditorWindow(path, store, delegate { return Task.FromResult(0); }, delegate { });
            editor.Title = "图绳 · 编辑器资源验证"; editor.ShowActivated = false; editor.Show();
            Task ready = editor.Ready;
            if (await Task.WhenAny(ready, Task.Delay(20000)) != ready) throw new Exception("编辑器加载超时");
            await ready; openingClock.Stop(); await Task.Delay(1000); object opened = Sample();
            var exports = new List<object>();
            var exportMilliseconds = new List<long>();
            object afterExportCollection = null;
            var clock = Stopwatch.StartNew();
            if (exportReview)
            {
                for (int iteration = 0; iteration < 6; iteration++)
                {
                    var exportClock = Stopwatch.StartNew();
                    string dimensions = await editor.Browser.ExecuteScriptAsync("(()=>{" +
                        (iteration == 5 ? "canvasStage.findOne('#FIE_original-image').filters([function(){}]);" : "") +
                        "const c=editor.getCurrentImgData({extension:'png'},1).imageData.imageCanvas;return[c.width,c.height];})()");
                    exportClock.Stop();
                    int[] size = new JavaScriptSerializer().Deserialize<int[]>(dimensions);
                    if (size == null || size.Length != 2 || size[0] != width || size[1] != height)
                        throw new Exception("Export did not return source-sized canvas: " + dimensions);
                    exportMilliseconds.Add(exportClock.ElapsedMilliseconds);
                    exports.Add(Sample());
                }
            }
            clock.Stop();
            if (exportReview)
            {
                await editor.Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("HeapProfiler.collectGarbage", "{}");
                await Task.Delay(100);
                afterExportCollection = Sample();
            }
            editor.CloseWithoutPrompt(); editor = null; await Task.Delay(7000); object closed = Sample();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var json = new JavaScriptSerializer(); string report = json.Serialize(new { width, height, before, openingMilliseconds = openingClock.ElapsedMilliseconds, opened, exports, exportMilliseconds, exportAndSamplingMilliseconds = clock.ElapsedMilliseconds, afterExportCollection, closedAfterSevenSeconds = closed, closedAfterCollection = Sample() });
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
