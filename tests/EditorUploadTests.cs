using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class EditorUploadTests
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static int checks;
        [STAThread]
        static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async delegate
            {
                try { await Run(); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                finally { app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }
        static async Task Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "editor-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "binary.png");
            var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0, 80, 140, 240, 255 }, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
            string data = Convert.ToBase64String(File.ReadAllBytes(path));
            int saved = 0; TaskCompletionSource<int> blocker = null;
            var window = new ImageEditorWindow(path, new StateStore(root), p => { saved++; return blocker == null ? Task.FromResult(0) : blocker.Task; }, delegate { });
            window.ShowActivated = false; window.Show();
            try
            {
                if (await Task.WhenAny(window.Ready, Task.Delay(20000)) != window.Ready) throw new Exception("Upload editor timed out");
                await window.Ready;
                var field = typeof(ImageEditorWindow).GetField("exportToken", BindingFlags.NonPublic | BindingFlags.Instance);
                string token = field == null ? "missing" : (string)field.GetValue(window);
                await window.Browser.ExecuteScriptAsync("window.uploadCompletion=null;const previous=window.completeAction;window.completeAction=r=>{window.uploadCompletion=r;previous(r);};");
                Check(await Upload(window, data, token, "save", "POST", "image/png", "false") == 202, "valid PNG POST accepted");
                await Wait(window, "window.uploadCompletion?.success===true&&window.uploadCompletion?.saved===true");
                Check(saved == 1, "accepted PNG reaches real save acknowledgement exactly once");
                Check(ImageContent.Read(path).PixelWidth == 2, "binary save retains source dimensions");
                Check(await Upload(window, data, "wrong", "save", "POST", "image/png", "false") == 403, "wrong per-window token rejected");
                Check(await Upload(window, data, "", "save", "POST", "image/png", "false") == 403, "missing token rejected");
                Check(await Upload(window, data, token, "delete", "POST", "image/png", "false") == 403, "unknown action rejected");
                Check(await Upload(window, data, token, "save", "GET", "image/png", "false") == 403, "wrong method rejected");
                Check(await Upload(window, data, token, "save", "POST", "text/plain", "false") == 403, "wrong content type rejected");
                Check(await Upload(window, data, token, "save", "POST", "image/png", "invalid") == 403, "invalid close flag rejected");
                Check(await Upload(window, "AQID", token, "save", "POST", "image/png", "false") == 400, "invalid PNG body rejected");
                Check(saved == 1, "rejected uploads never invoke save");
                blocker = new TaskCompletionSource<int>();
                await window.Browser.ExecuteScriptAsync("window.uploadCompletion=null");
                Check(await Upload(window, data, token, "save", "POST", "image/png", "false") == 202, "first in-flight upload accepted");
                Check(await Upload(window, data, token, "save", "POST", "image/png", "false") == 409, "concurrent upload rejected");
                blocker.SetResult(0); blocker = null;
                await Wait(window, "window.uploadCompletion?.success===true");
                var pending = new TaskCompletionSource<bool>();
                var pendingField = typeof(ImageEditorWindow).GetField("saveForClose", BindingFlags.NonPublic | BindingFlags.Instance);
                pendingField.SetValue(window, pending);
                Task<bool> firstClose = window.ConfirmClose(), secondClose = window.ConfirmClose();
                Check(!firstClose.IsCompleted && !secondClose.IsCompleted, "concurrent close callers wait for the same in-flight save");
                pending.SetResult(false);
                Check(!await firstClose && !await secondClose, "both close callers receive failed save result");
                pendingField.SetValue(window, null);
                pending = new TaskCompletionSource<bool>();
                typeof(ImageEditorWindow).GetField("saveForClose", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, pending);
                await window.Browser.ExecuteScriptAsync("window.uploadOriginalExport=editor.getCurrentImgData;editor.getCurrentImgData=()=>{throw new Error('injected export failure');};window.submit('save');");
                Check(await Task.WhenAny(pending.Task, Task.Delay(3000)) == pending.Task && !await pending.Task, "JS export failure settles pending save-on-close");
                await window.Browser.ExecuteScriptAsync("editor.getCurrentImgData=window.uploadOriginalExport");
                pending = new TaskCompletionSource<bool>();
                typeof(ImageEditorWindow).GetField("saveForClose", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, pending);
                blocker = new TaskCompletionSource<int>();
                Check(await Upload(window, data, token, "save", "POST", "image/png", "false") == 202, "upload accepted before closing during save");
                var closingClock = System.Diagnostics.Stopwatch.StartNew();
                while (saved != 3)
                {
                    if (closingClock.ElapsedMilliseconds > 3000) throw new Exception("Save callback did not start");
                    await Task.Delay(10);
                }
                window.CloseWithoutPrompt();
                Check(pending.Task.IsCompleted && !pending.Task.Result, "closure during save settles pending save-on-close");
                blocker.SetResult(0); blocker = null; await Task.Delay(50);
            }
            finally { if (blocker != null) blocker.TrySetResult(0); window.CloseWithoutPrompt(); }
            Console.WriteLine("Editor upload: {0} passed", checks);
        }
        static async Task<int> Upload(ImageEditorWindow window, string data, string token, string action, string method, string type, string close)
        {
            string options = Json.Serialize(new { data, token, action, method, type, close });
            await window.Browser.ExecuteScriptAsync("window.uploadStatus=null;window.uploadError=null;(async()=>{try{const p=" + options + ";" +
                "const bytes=Uint8Array.from(atob(p.data),c=>c.charCodeAt(0));const headers={'Content-Type':p.type,'X-PicRope-Token':p.token,'X-PicRope-Action':p.action,'X-PicRope-Close':p.close};" +
                "const response=await fetch('https://screenline-editor.invalid/export',{method:p.method,headers,body:p.method==='GET'?undefined:new Blob([bytes],{type:p.type})});" +
                "window.uploadStatus=response.status;}catch(error){window.uploadError=String(error);}})();");
            await Wait(window, "window.uploadStatus!==null||window.uploadError!==null");
            string error = await window.Browser.ExecuteScriptAsync("window.uploadError");
            if (error != "null") throw new Exception("Upload fetch failed: " + error);
            return Int32.Parse(await window.Browser.ExecuteScriptAsync("window.uploadStatus"));
        }
        static async Task Wait(ImageEditorWindow window, string expression)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (await window.Browser.ExecuteScriptAsync(expression) != "true")
            {
                if (clock.ElapsedMilliseconds > 5000) throw new Exception("Timed out: " + expression);
                await Task.Delay(25);
            }
        }
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++; Console.WriteLine("PASS " + name);
        }
    }
}
