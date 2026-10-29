using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class EditorQualityTests
    {
        [STAThread]
        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
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
            const int width = 1600, height = 1000;
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    byte shade = (byte)((x + y) % 2 == 0 ? 0 : 255);
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
                    pixels[offset + 3] = 255;
                }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            string path = Path.Combine(root, "pixel-grid.png");
            File.WriteAllBytes(path, ImageContent.EncodeClipboard(bitmap));
            var window = new ImageEditorWindow(path, new StateStore(root), p => Task.FromResult(0), delegate { });
            window.ShowActivated = false;
            window.Show();
            try
            {
                if (await Task.WhenAny(window.Ready, Task.Delay(20000)) != window.Ready) throw new Exception("编辑器加载超时");
                await window.Ready;
                // Exercise the same export and save path as the editor's Save button.
                await window.Browser.ExecuteScriptAsync("window.submit('save')");
                for (int attempt = 0; ; attempt++)
                {
                    if (await window.Browser.ExecuteScriptAsync("!busy") == "true") break;
                    if (attempt == 100) throw new Exception("编辑器保存超时");
                    await Task.Delay(100);
                }
                if (await window.Browser.ExecuteScriptAsync("document.getElementById('status').textContent") != "\"已保存\"")
                    throw new Exception("编辑器没有完成保存");
                BitmapSource result;
                using (var stream = File.OpenRead(path))
                    result = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if (result.PixelWidth != width || result.PixelHeight != height) throw new Exception("编辑后图片尺寸发生变化");
                byte[] actual = new byte[pixels.Length];
                new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0).CopyPixels(actual, width * 4, 0);
                int changed = actual.Where((value, index) => value != pixels[index]).Count();
                Console.WriteLine("原尺寸导出：{0} x {1}，变化的像素通道：{2}/{3}", width, height, changed, pixels.Length);
                if (changed != 0) throw new Exception("编辑器导出损坏了原图的单像素细节或透明通道");
                // A neutral filter exercises Filerobot's required filter-cache path.
                string filtered = new JavaScriptSerializer().Deserialize<string>(await window.Browser.ExecuteScriptAsync("(()=>{canvasStage.findOne('#FIE_original-image').filters([function(){}]);return editor.getCurrentImgData({extension:'png'},1).imageData.imageBase64;})()"));
                byte[] filteredPng = ImageEditFiles.Decode(filtered);
                string filteredPath = Path.Combine(root, "filtered.png");
                File.WriteAllBytes(filteredPath, filteredPng);
                if (ImageContent.Read(filteredPath).Fingerprint != ImageContent.Read(path).Fingerprint)
                    throw new Exception("滤镜缓存损坏了原始像素");
                Console.WriteLine("滤镜缓存保留原始像素测试通过。");
                Console.WriteLine("编辑器原始像素保存测试通过。");
            }
            finally { window.CloseWithoutPrompt(); }
        }
    }
}
