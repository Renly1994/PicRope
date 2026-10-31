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
        static bool phaseReview;
        static bool saveReview;
        [STAThread]
        static void Main(string[] arguments)
        {
            exportReview = arguments.Contains("--export-review");
            phaseReview = arguments.Contains("--phase-review");
            saveReview = arguments.Contains("--save-review");
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
            if (phaseReview || saveReview) { await RunPhases(); return; }
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

        static async Task RunPhases()
        {
            const int width = 2048, height = 1152;
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "editor-phase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] pixels = new byte[width * height * 4];
            new Random(20261031).NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            string path = Path.Combine(root, "texture.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            string fingerprint = saveReview ? ImageContent.Read(path).Fingerprint : null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 96 * 1024 * 1024 };
            editor = new ImageEditorWindow(path, new StateStore(root), delegate { return Task.FromResult(0); }, delegate { });
            editor.ShowActivated = false; editor.Show();
            if (await Task.WhenAny(editor.Ready, Task.Delay(20000)) != editor.Ready) throw new Exception("Phase editor initialization timed out");
            await editor.Ready;
            object opened = Sample();
            if (saveReview)
            {
                await RunSaveReview(path, fingerprint, opened);
                GC.KeepAlive(pixels); GC.KeepAlive(bitmap); return;
            }
            string render = await editor.Browser.ExecuteScriptAsync("(()=>{" +
                "const proto=Object.getPrototypeOf(canvasStage),original=proto._toKonvaCanvas;let renderMs=0,encodeMs=0;" +
                "proto._toKonvaCanvas=function(...args){let t=performance.now();const result=original.apply(this,args);renderMs+=performance.now()-t;" +
                "const encode=result.toDataURL;result.toDataURL=function(...args){const t=performance.now();try{return encode.apply(this,args);}finally{encodeMs+=performance.now()-t;}};return result;};" +
                "try{window.phaseOutput=editor.getCurrentImgData({extension:'png'},1).imageData;}finally{proto._toKonvaCanvas=original;}" +
                "return{renderMilliseconds:renderMs,encodeMilliseconds:encodeMs,width:phaseOutput.width,height:phaseOutput.height,base64Characters:phaseOutput.imageBase64.length};})()");
            object afterExport = Sample();
            var clock = Stopwatch.StartNew();
            string json = await editor.Browser.ExecuteScriptAsync("window.phaseOutput.imageBase64");
            string data = serializer.Deserialize<string>(json);
            clock.Stop(); long transferMilliseconds = clock.ElapsedMilliseconds;
            object afterTransfer = Sample();
            clock.Restart(); byte[] png = await Task.Run(() => ImageEditFiles.Decode(data));
            clock.Stop(); long decodeMilliseconds = clock.ElapsedMilliseconds;
            object afterDecode = Sample();
            clock.Restart(); await Task.Run(() => ImageEditFiles.Save(Path.Combine(root, "saved.png"), png));
            clock.Stop(); long saveMilliseconds = clock.ElapsedMilliseconds;
            object afterSave = Sample();
            await editor.Browser.ExecuteScriptAsync("window.phaseOutput=null");
            editor.CloseWithoutPrompt(); editor = null;
            await Task.Delay(7000);
            string report = serializer.Serialize(new { width, height, fixture = "opaque seeded RGB noise", opened,
                renderAndEncode = serializer.DeserializeObject(render), afterExport,
                transferMilliseconds, afterTransfer, pngBytes = png.Length, decodeMilliseconds, afterDecode,
                saveMilliseconds, afterSave, closedAfterSevenSeconds = Sample(),
                caveat = "Transfer uses ExecuteScriptAsync JSON return, not WebMessageReceived. Phase outputs remain rooted during sampling; snapshots are not allocation peaks." });
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "performance"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "editor-phases.json"), report); Console.WriteLine(report);
            GC.KeepAlive(json); GC.KeepAlive(data); GC.KeepAlive(png); GC.KeepAlive(pixels); GC.KeepAlive(bitmap);
        }

        static async Task RunSaveReview(string path, string fingerprint, object opened)
        {
            var ids = ProcessTree().ToArray();
            string runtime = editor.Browser.CoreWebView2.Environment.BrowserVersionString;
            var samples = new List<object>();
            var clock = Stopwatch.StartNew();
            await editor.Browser.ExecuteScriptAsync("window.probeSaved=null;window.probeSaveMilliseconds=null;" +
                "const previous=window.completeAction,start=performance.now();window.completeAction=result=>{" +
                "previous(result);window.probeSaveMilliseconds=performance.now()-start;window.probeSaved=result;};window.submit('save');");
            while (await editor.Browser.ExecuteScriptAsync("window.probeSaved!==null") != "true")
            {
                if (clock.ElapsedMilliseconds > 30000) throw new Exception("Actual save probe timed out");
                samples.Add(Sample(ids)); await Task.Delay(10);
            }
            samples.Add(Sample(ids));
            string result = await editor.Browser.ExecuteScriptAsync("({success:probeSaved.success,saved:probeSaved.saved,milliseconds:probeSaveMilliseconds})");
            var serializer = new JavaScriptSerializer();
            var saved = serializer.Deserialize<Dictionary<string, object>>(result);
            if (!(bool)saved["success"] || !(bool)saved["saved"]) throw new Exception("Actual save failed: " + result);
            if (ImageContent.Read(path).Fingerprint != fingerprint) throw new Exception("Actual save changed fixture pixels");
            editor.CloseWithoutPrompt(); editor = null; await Task.Delay(7000);
            string report = serializer.Serialize(new { fixture = "2048x1152 opaque seeded RGB noise", runtime,
                opened, result = saved, samples, pngBytes = new FileInfo(path).Length,
                closedAfterSevenSeconds = Sample(),
                caveat = "Actual submit/host/save acknowledgement. In-flight snapshots track processes known at ready; newly spawned processes and transient peaks may be missed. No forced GC." });
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "performance"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "editor-save.json"), report); Console.WriteLine(report);
        }

        static object Sample()
        {
            return Sample(ProcessTree());
        }

        static HashSet<int> ProcessTree()
        {
            int root = Process.GetCurrentProcess().Id;
            var parents = new Dictionary<int, int>();
            using (var query = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
            using (var results = query.Get())
                foreach (ManagementObject row in results) { parents[Convert.ToInt32(row["ProcessId"])] = Convert.ToInt32(row["ParentProcessId"]); row.Dispose(); }
            var ids = new HashSet<int> { root }; int previous;
            do { previous = ids.Count; foreach (var pair in parents) if (ids.Contains(pair.Value)) ids.Add(pair.Key); } while (ids.Count != previous);
            return ids;
        }

        static object Sample(IEnumerable<int> ids)
        {
            long working = 0, privateBytes = 0; int count = 0;
            foreach (int id in ids)
                try { using (var process = Process.GetProcessById(id)) { working += process.WorkingSet64; privateBytes += process.PrivateMemorySize64; count++; } }
                catch (ArgumentException) { }
            return new { processes = count, workingSetMB = Math.Round(working / 1048576.0, 1), privateMB = Math.Round(privateBytes / 1048576.0, 1) };
        }
    }
}
