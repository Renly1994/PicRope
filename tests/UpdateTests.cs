using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class UpdateTests
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string type, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);
        static int checks;
        static string root;
        static byte[] package;
        static AppUpdates.ReleaseData data;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();

        [STAThread]
        public static void Main(string[] arguments)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(application);
            application.Startup += async delegate
            {
                try { await Run(arguments.Contains("--live")); Console.WriteLine("更新测试：{0} 项通过", checks); }
                catch (Exception error) { Console.WriteLine("更新测试失败：" + error); Environment.ExitCode = 1; }
                finally { if (root != null && Directory.Exists(root)) Directory.Delete(root, true); application.Shutdown(Environment.ExitCode); }
            };
            application.Run();
        }

        static async Task Run(bool live)
        {
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "updates-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string fixture = Path.Combine(root, "fixture.exe");
            using (var compiler = CodeDomProvider.CreateProvider("CSharp"))
            {
                var parameters = new CompilerParameters { GenerateExecutable = true, OutputAssembly = fixture, CompilerOptions = "/target:winexe" };
                var result = compiler.CompileAssemblyFromSource(parameters,
                    "[assembly:System.Reflection.AssemblyInformationalVersion(\"1.3.0\")] class Fixture { static void Main() { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"started.txt\"), \"ok\"); } }");
                if (result.Errors.HasErrors) throw new Exception(result.Errors[0].ToString());
            }
            package = MakePackage(fixture, null);
            data = new AppUpdates.ReleaseData { tag_name = "v1.3.0", body = "改进图片篮子\n修复截图选择问题", assets = new[] {
                new AppUpdates.AssetData { name = "PicRope-1.3.0-source.zip" },
                new AppUpdates.AssetData { name = "PicRope-1.3.0-windows-x64.zip", size = package.Length,
                    browser_download_url = AppUpdates.Repository + "/releases/download/v1.3.0/PicRope-1.3.0-windows-x64.zip", digest = Digest(package) }
            } };
            Check(Parse().Version == "1.3.0" && Parse().DownloadUrl.EndsWith("windows-x64.zip"), "只选择较新的 Windows x64 安装包，不误取源码包");
            Check(AppUpdates.Parse(json.Serialize(data), "1.3.0") == null && AppUpdates.Parse(json.Serialize(data), "1.4.0") == null, "同版与旧版不会提示升级或降级");
            data.prerelease = true; Check(Parse() == null, "预发布版本不参与自动更新"); data.prerelease = false;
            data.draft = true; Check(Parse() == null, "草稿版本不参与更新"); data.draft = false;
            var asset = data.assets[1]; string original = asset.digest; asset.digest = null;
            Reject(delegate { Parse(); }, "缺少 SHA256 校验信息时拒绝安装"); asset.digest = original;
            string url = asset.browser_download_url; asset.browser_download_url = "https://example.com/update.zip";
            Reject(delegate { Parse(); }, "拒绝仓库以外的安装包地址"); asset.browser_download_url = url;
            string tag = data.tag_name; data.tag_name = "invalid"; Reject(delegate { Parse(); }, "拒绝无效版本号"); data.tag_name = tag;
            var release = Parse();
            var handler = new Handler(package, json.Serialize(data));
            using (var updates = new AppUpdates(handler))
            {
                Check((await updates.CheckAsync(CancellationToken.None)).Version == "1.3.0", "检查更新能读取服务返回的新版本");
                string work = await updates.DownloadAsync(release, new Progress<int>(), CancellationToken.None);
                Check(File.Exists(Path.Combine(work, "contents", "app", "PicRope.exe")), "下载、大小校验、SHA256 校验及解压完整通过");
                string target = Path.Combine(root, "installed"); Directory.CreateDirectory(Path.Combine(target, "data", "Captures"));
                File.Copy(fixture, Path.Combine(target, "PicRope.exe")); File.WriteAllText(Path.Combine(target, "data", "state.json"), "用户设置");
                File.WriteAllText(Path.Combine(target, "data", "Captures", "用户图片.png"), "用户图片");
                int exit = await RunInstaller(work, target);
                Check(exit == 0 && File.Exists(Path.Combine(target, "started.txt")) && File.Exists(Path.Combine(target, "editor", "index.html")), "安装器替换文件并自动重启更新后的程序");
                Check(File.ReadAllText(Path.Combine(target, "data", "state.json")) == "用户设置" && File.ReadAllText(Path.Combine(target, "data", "Captures", "用户图片.png")) == "用户图片", "安装更新后图片和设置完整保留");
                Check(!Directory.Exists(work), "成功安装后清理本次下载缓存");
                release.Sha256 = new string('0', 64);
                await RejectAsync(async delegate { await updates.DownloadAsync(release, new Progress<int>(), CancellationToken.None); }, "损坏或被篡改的安装包不能进入安装流程");
                release = Parse();
                var window = new UpdateWindow(release, updates, delegate { return Task.FromResult(false); }) { ShowActivated = false };
                window.Show(); window.UpdateLayout();
                var shot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); shot.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(shot));
                using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-window.png"))) encoder.Save(file);
                var install = (Button)typeof(UpdateWindow).GetField("install", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                int downloads = handler.Downloads;
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => install.IsEnabled && (string)install.Content == "重启并安装");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => install.IsEnabled);
                Check(handler.Downloads == downloads + 1, "用户取消关闭未保存图片后可继续安装，不重复下载");
                window.Close();
                work = await updates.DownloadAsync(release, new Progress<int>(), CancellationToken.None);
                File.WriteAllText(Path.Combine(target, "editor", "index.html"), "原版本文件");
                File.WriteAllText(Path.Combine(target, "zzz-locked.dll"), "旧文件");
                File.WriteAllText(Path.Combine(work, "contents", "app", "zzz-locked.dll"), "新文件");
                using (var locked = new FileStream(Path.Combine(target, "zzz-locked.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) exit = await RunInstaller(work, target);
                Check(exit != 0 && File.ReadAllText(Path.Combine(target, "editor", "index.html")) == "原版本文件", "安装途中遇到文件占用时恢复已经替换的旧文件");
                AppUpdates.Discard(work);
            }
            string archive = Path.Combine(root, "invalid.zip");
            File.WriteAllBytes(archive, MakePackage(fixture, "../escape.txt"));
            Reject(delegate { AppUpdates.Extract(archive, Path.Combine(root, "extract"), "1.3.0"); }, "拒绝 ZIP 路径穿越");
            File.WriteAllBytes(archive, MakePackage(fixture, "app/data/state.json"));
            Reject(delegate { AppUpdates.Extract(archive, Path.Combine(root, "extract-data"), "1.3.0"); }, "拒绝包含用户数据目录的更新包");
            File.WriteAllBytes(archive, package);
            Reject(delegate { AppUpdates.Extract(archive, Path.Combine(root, "wrong-version"), "9.0.0"); }, "安装前校验程序实际版本与发布版本一致");
            using (var canceled = new CancellationTokenSource())
            using (var updates = new AppUpdates(new Handler(package, json.Serialize(data))))
            {
                canceled.Cancel();
                try { await updates.DownloadAsync(Parse(), new Progress<int>(), canceled.Token); throw new Exception("取消无效"); }
                catch (OperationCanceledException) { Check(true, "取消下载时不会启动安装器"); }
            }
            if (live) using (var updates = new AppUpdates()) { await updates.CheckAsync(CancellationToken.None); Check(true, "真实 GitHub Release 接口连接与版本检查通过"); }
        }

        static UpdateRelease Parse() { return AppUpdates.Parse(json.Serialize(data), "1.1.0"); }
        static string Digest(byte[] bytes) { using (var hash = SHA256.Create()) return "sha256:" + BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static byte[] MakePackage(string fixture, string extra)
        {
            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    zip.CreateEntryFromFile(fixture, "app/PicRope.exe");
                    zip.CreateEntryFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.ps1"), "app/update.ps1");
                    using (var text = new StreamWriter(zip.CreateEntry("app/editor/index.html").Open())) text.Write("更新后的文件");
                    if (extra != null) using (var text = new StreamWriter(zip.CreateEntry(extra).Open())) text.Write("无效内容");
                }
                return output.ToArray();
            }
        }
        static async Task<int> RunInstaller(string work, string target)
        {
            using (var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(work, "apply-update.ps1") + "\" -WorkPath \"" + work + "\" -TargetPath \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.GetEncoding(936) }))
            {
                for (int i = 0; i < 300 && !process.HasExited; i++)
                {
                    var dialog = FindWindow(null, "图绳更新"); if (dialog != IntPtr.Zero) SendMessage(dialog, 0x10, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(100);
                }
                if (!process.HasExited) throw new Exception("安装器未按时完成");
                await Task.Delay(300);
                if (process.ExitCode != 0) Console.WriteLine(process.StandardError.ReadToEnd());
                return process.ExitCode;
            }
        }
        static async Task Until(Func<bool> condition) { for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(50); } throw new Exception("操作超时"); }
        static void Reject(Action action, string title) { try { action(); } catch (InvalidDataException) { Check(true, title); return; } throw new Exception(title); }
        static async Task RejectAsync(Func<Task> action, string title) { try { await action(); } catch (InvalidDataException) { Check(true, title); return; } throw new Exception(title); }
        static void Check(bool condition, string title) { if (!condition) throw new Exception(title); checks++; Console.WriteLine("通过：" + title); }
        sealed class Handler : HttpMessageHandler
        {
            readonly byte[] bytes; readonly string release;
            public int Downloads;
            public Handler(byte[] bytes, string release) { this.bytes = bytes; this.release = release; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
                if (request.RequestUri.Host == "api.github.com") response.Content = new StringContent(release);
                else { Downloads++; response.Content = new ByteArrayContent(bytes); }
                return Task.FromResult(response);
            }
        }
    }
}
