using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class CoreTests
    {
        static int passed, failed;
        static string root;

        [STAThread]
        public static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Run("弹簧在不同帧率下有一致轨迹", delegate
            {
                var slow = new SpringMotion(0.55, 0.78, 0) { Target = 172 };
                var fast = new SpringMotion(0.55, 0.78, 0) { Target = 172 };
                for (int i = 0; i < 12; i++) slow.Step(1.0 / 30);
                for (int i = 0; i < 58; i++) fast.Step(0.4 / 58);
                Check(Math.Abs(slow.Position - fast.Position) < 0.000001 && Math.Abs(slow.Velocity - fast.Velocity) < 0.000001);
            });
            Run("动画改向时保留当前位置与速度", delegate
            {
                var motion = new SpringMotion(0.42, 0.82, -240) { Target = 0 };
                motion.Step(0.08);
                double position = motion.Position, velocity = motion.Velocity;
                motion.Target = -240;
                Check(motion.Position == position && motion.Velocity == velocity);
                motion.Step(0.001);
                Check(Math.Abs(motion.Position - position) < 3);
                motion.Step(3);
                Check(motion.Position == -240 && motion.Velocity == 0);
            });
            Run("管理目录包含其子目录", delegate { Check(FileRules.IsInside(Path.Combine(root, "captures", "a.png"), root)); });
            Run("同名前缀目录不属于管理目录", delegate { Check(!FileRules.IsInside(root + "-other\\a.png", root)); });
            Run("父目录跳转不能绕过目录边界", delegate { Check(!FileRules.IsInside(Path.Combine(root, "..", "outside.png"), root)); });
            Run("图片扩展名不区分大小写", delegate { Check(FileRules.IsImage("截图.PNG") && !FileRules.IsImage("截图.txt")); });
            Run("纯图片剪贴板可收集", delegate { Check(ClipboardRules.IsPlainImage(new[] { "Bitmap", "PNG", "DeviceIndependentBitmap" })); });
            Run("MIME PNG 剪贴板可收集", delegate { Check(ClipboardRules.IsPlainImage(new[] { "image/png" })); });
            Run("仅原生 DIB 位图的剪贴板可收集", delegate { Check(ClipboardRules.IsPlainImage(new[] { "DeviceIndependentBitmap" })); });
            Run("微信 Alt+A 的实际格式组合可收集", delegate
            {
                Check(ClipboardRules.IsPlainImage(new[] {
                    "DeviceIndependentBitmap", "application/x-capturer-status", "WeChatScreenshotFormat",
                    "image/png", "image/bmp", "image/jpeg", "image/jpg", "image/pbm", "image/pgm",
                    "image/ppm", "image/webp", "image/xbm", "image/xpm"
                }));
            });
            Run("文档与网页复制不误收集", delegate
            {
                foreach (string type in new[] { "HTML Format", "UnicodeText", "Text", "FileDrop", "Rich Text Format", "System.String" })
                    Check(!ClipboardRules.IsPlainImage(new[] { "Bitmap", type }));
            });
            Run("文字剪贴板不收集", delegate { Check(!ClipboardRules.IsPlainImage(new[] { "UnicodeText" })); });
            Run("同尺寸不同图片有不同指纹", delegate { Check(ImageContent.HashPixels(Picture(20)) != ImageContent.HashPixels(Picture(80))); });
            Run("同图片重新编码后指纹不变", delegate
            {
                var image = Picture(30);
                Check(ImageContent.HashPixels(image) == ImageContent.Decode(ImageContent.EncodeClipboard(image)).Fingerprint);
            });
            Run("图片编码保留局部透明像素", delegate
            {
                byte[] pixels = { 20, 40, 60, 0, 30, 50, 70, 128, 40, 60, 80, 255, 50, 70, 90, 255 };
                var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                Check(ImageContent.HashPixels(image) == ImageContent.Decode(ImageContent.EncodeClipboard(image)).Fingerprint);
            });
            Run("全零透明通道的 Windows 位图仍按不透明图片保存", delegate
            {
                byte[] pixels = { 20, 40, 60, 0, 30, 50, 70, 0 };
                var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                byte[] encoded = ImageContent.EncodeClipboard(image);
                using (var stream = new MemoryStream(encoded))
                {
                    var decoded = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
                    var actual = new byte[8]; decoded.CopyPixels(actual, 8, 0);
                    Check(actual[0] == 20 && actual[3] == 255 && actual[4] == 30 && actual[7] == 255);
                }
            });
            Run("同一次截图的两个来源只收一次", delegate
            {
                var ledger = new CaptureReceipts();
                DateTime now = DateTime.UtcNow;
                Check(ledger.Accept("a", CaptureSource.Folder, now));
                Check(!ledger.Accept("a", CaptureSource.Clipboard, now.AddMilliseconds(100)));
            });
            Run("连续相同截图不会被尺寸或内容误过滤", delegate
            {
                var ledger = new CaptureReceipts();
                DateTime now = DateTime.UtcNow;
                Check(ledger.Accept("a", CaptureSource.Clipboard, now));
                Check(ledger.Accept("a", CaptureSource.Clipboard, now.AddMilliseconds(100)));
                Check(!ledger.Accept("a", CaptureSource.Folder, now.AddMilliseconds(200)));
                Check(!ledger.Accept("a", CaptureSource.Folder, now.AddMilliseconds(300)));
            });
            Run("不同内容跨来源都能收集", delegate
            {
                var ledger = new CaptureReceipts();
                Check(ledger.Accept("a", CaptureSource.Folder, DateTime.UtcNow));
                Check(ledger.Accept("b", CaptureSource.Clipboard, DateTime.UtcNow));
            });
            Run("去重记录过期后可重新收集", delegate
            {
                var ledger = new CaptureReceipts();
                DateTime now = DateTime.UtcNow;
                Check(ledger.Accept("a", CaptureSource.Folder, now));
                Check(ledger.Accept("a", CaptureSource.Clipboard, now.AddSeconds(6)));
            });
            Run("损坏图片会被拒绝", delegate
            {
                bool rejected = false;
                try { ImageContent.Decode(new byte[] { 1, 2, 3, 4 }); }
                catch (NotSupportedException) { rejected = true; }
                catch (IOException) { rejected = true; }
                Check(rejected);
            });
            Run("读取缩略图后文件仍可移动", delegate
            {
                string file = WritePicture("可移动.png", 50);
                var image = ImageContent.Read(file);
                File.Move(file, Path.Combine(root, "移动后.png"));
                Check(image.Thumbnail.PixelWidth > 0);
            });
            Run("取下外部图片不会删除原文件", delegate
            {
                var store = new StateStore(Path.Combine(root, "detach-profile"));
                var gallery = new Gallery(store);
                string file = WritePicture("外部图片.png", 70);
                gallery.Add(file, ImageContent.Read(file));
                gallery.Remove(gallery.Items[0]);
                Check(File.Exists(file) && gallery.Items.Count == 0);
            });
            Run("相同文件不会重复挂上", delegate
            {
                var gallery = new Gallery(new StateStore(Path.Combine(root, "duplicate-profile")));
                string file = WritePicture("同一文件.png", 90);
                var image = ImageContent.Read(file);
                gallery.Add(file, image);
                gallery.Add(file, image);
                Check(gallery.Items.Count == 1);
            });
            Run("只收集的图片不会挂上，取下后仍在图片篓", delegate
            {
                var store = new StateStore(Path.Combine(root, "basket-profile"));
                var gallery = new Gallery(store);
                string file = WritePicture("待挂.png", 100);
                var item = gallery.Collect(file, ImageContent.Read(file));
                Check(gallery.Items.Count == 0 && gallery.Collected.Single() == item);
                gallery.Add(file, item.Image);
                Check(gallery.Items.Single() == item && gallery.Collected.Count == 1);
                gallery.Remove(item);
                Check(gallery.Items.Count == 0 && gallery.Collected.Single() == item && File.Exists(file));
                var restored = new StateStore(store.Root);
                Check(restored.State.Images.Count == 0 && restored.State.Collection.Single() == file);
            });
            Run("绳子达到上限后仍保留所有收集记录", delegate
            {
                var gallery = new Gallery(new StateStore(Path.Combine(root, "basket-limit-profile")));
                for (int i = 0; i < 52; i++)
                {
                    string file = WritePicture("收集_" + i + ".png", (byte)i);
                    gallery.Add(file, ImageContent.Read(file));
                }
                Check(gallery.Items.Count == 50 && gallery.Collected.Count == 52);
                gallery.Clear();
                Check(gallery.Items.Count == 0 && gallery.Collected.Count == 52);
            });
            Run("重新挂上已编辑的图片时使用最新内容", delegate
            {
                var gallery = new Gallery(new StateStore(Path.Combine(root, "basket-edited-profile")));
                string file = WritePicture("编辑后重挂.png", 40);
                gallery.Collect(file, ImageContent.Read(file));
                File.WriteAllBytes(file, ImageContent.EncodeClipboard(Picture(150)));
                var edited = ImageContent.Read(file);
                gallery.Add(file, edited);
                Check(gallery.Items.Single().Image.Fingerprint == edited.Fingerprint);
            });
            Run("删除本地文件时同时清理挂图和收集记录", delegate
            {
                var gallery = new Gallery(new StateStore(Path.Combine(root, "basket-prune-profile")));
                string file = WritePicture("已删除.png", 120);
                gallery.Add(file, ImageContent.Read(file));
                File.Delete(file);
                gallery.Prune();
                Check(gallery.Items.Count == 0 && gallery.Collected.Count == 0);
            });
            Run("自动挂图开关保存后可恢复，旧版配置默认开启", delegate
            {
                var store = new StateStore(Path.Combine(root, "auto-hang-profile"));
                Check(store.State.AutoHang);
                store.State.AutoHang = false;
                store.Save();
                Check(!new StateStore(store.Root).State.AutoHang);
                File.WriteAllText(Path.Combine(store.Root, "state.json"), "{\"Images\":[\"旧截图.png\"]}");
                Check(new StateStore(store.Root).State.AutoHang);
            });
            Run("设置与中文路径可持久化并原子替换", delegate
            {
                string profile = Path.Combine(root, "state-profile");
                var store = new StateStore(profile);
                store.State.Images.Add(Path.Combine(root, "中文截图.png"));
                store.Save();
                store.State.CollectClipboard = true;
                store.Save();
                var restored = new StateStore(profile);
                Check(restored.State.CollectClipboard && restored.State.Images.Single().EndsWith("中文截图.png"));
                Check(!File.Exists(Path.Combine(profile, "state.json.tmp")));
            });
            Run("旧版配置保留截图与收集设置，并使用经典主题", delegate
            {
                string profile = Path.Combine(root, "legacy-theme-profile");
                Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "state.json"), "{\"CollectClipboard\":true,\"Images\":[\"旧截图.png\"]}");
                var restored = new StateStore(profile);
                Check(restored.State.Theme == "classic" && restored.State.CollectClipboard && restored.State.Images.Single() == "旧截图.png");
            });
            Run("五套主题保存后可恢复，截图与收集设置保持不变", delegate
            {
                string profile = Path.Combine(root, "theme-profile");
                var store = new StateStore(profile);
                store.State.Images.Add("保留截图.png"); store.State.CollectClipboard = true;
                foreach (string theme in new[] { "classic", "linen", "porcelain", "color", "graphite" })
                {
                    store.State.Theme = theme; store.Save();
                    var restored = new StateStore(profile);
                    Check(restored.State.Theme == theme && restored.State.CollectClipboard && restored.State.Images.Single() == "保留截图.png");
                }
            });
            Run("短暂读取占用结束后仍能保存设置", delegate
            {
                string profile = Path.Combine(root, "state-lock-profile");
                var store = new StateStore(profile);
                store.Save();
                var reader = new FileStream(Path.Combine(profile, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = System.Threading.Tasks.Task.Run(delegate { System.Threading.Thread.Sleep(60); reader.Dispose(); });
                store.State.CollectClipboard = true;
                try { store.Save(); }
                finally { release.Wait(); }
                Check(new StateStore(profile).State.CollectClipboard);
            });
            Run("文字收集忽略空白，不创建空记录", delegate
            {
                var texts = new TextCollection(new StateStore(Path.Combine(root, "empty-texts")));
                Check(texts.Add(null) == null && texts.Add(" \r\n\t") == null && texts.Items.Count == 0);
            });
            Run("收下文字完整保留中文、缩进和换行，重复收下只保留一条", delegate
            {
                var texts = new TextCollection(new StateStore(Path.Combine(root, "exact-texts")));
                string content = "  拍摄清单\r\n\t领夹麦 🔋\n第二段  ";
                var first = texts.Add(content);
                var second = texts.Add("另一段文字");
                Check(texts.Add(content) == first && texts.Items.Count == 2 && texts.Items[0] == second && first.Content == content);
                var restored = new TextCollection(new StateStore(texts.Store.Root));
                Check(restored.Items[1].Content == content && restored.Items[1].Id == first.Id && Math.Abs((restored.Items[1].CreatedUtc - first.CreatedUtc).TotalMilliseconds) < 1);
            });
            Run("删除文字持久化，保留其他文字、截图和主题设置", delegate
            {
                var store = new StateStore(Path.Combine(root, "delete-texts"));
                store.State.Images.Add("原有图片.png"); store.State.Collection.Add("待挂截图.png"); store.State.Theme = "linen";
                var texts = new TextCollection(store);
                var removed = texts.Add("删除这段");
                var retained = texts.Add("保留这段");
                texts.Remove(removed);
                var restored = new StateStore(store.Root);
                Check(restored.State.Texts.Count == 1 && restored.State.Texts[0].Id == retained.Id && restored.State.Images.Single() == "原有图片.png"
                    && restored.State.Collection.Single() == "待挂截图.png" && restored.State.Theme == "linen");
            });
            Run("旧版配置可直接收下文字，大段文字重启后仍完整", delegate
            {
                string profile = Path.Combine(root, "large-texts"); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "state.json"), "{\"Images\":[\"保留截图.png\"],\"Texts\":null}");
                var texts = new TextCollection(new StateStore(profile));
                string content = new string('中', 2200000);
                texts.Add(content);
                var restored = new StateStore(profile);
                Check(restored.State.Texts.Single().Content == content && restored.State.Images.Single() == "保留截图.png");
            });
            Run("显示屏幕选择可保存，旧版配置默认跟随鼠标", delegate
            {
                string profile = Path.Combine(root, "display-preference"); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "state.json"), "{\"Images\":[\"保留截图.png\"],\"Theme\":\"linen\"}");
                var store = new StateStore(profile);
                Check(store.State.DisplayDevice == null && store.State.Images.Single() == "保留截图.png" && store.State.Theme == "linen");
                store.State.DisplayDevice = @"\\.\DISPLAY2";
                store.Save();
                var restored = new StateStore(store.Root);
                Check(restored.State.DisplayDevice == @"\\.\DISPLAY2");
                restored.State.DisplayDevice = null;
                restored.Save();
                Check(new StateStore(store.Root).State.DisplayDevice == null);
            });
            Run("缩略图不保留原文件流，像素指纹保持兼容", delegate
            {
                var original = Picture(42);
                var content = ImageContent.Decode(ImageContent.EncodeClipboard(original));
                Check(!(content.Thumbnail is BitmapImage));
                Check(content.Fingerprint == ImageContent.HashPixels(original));
            });
            Run("历史截图只读取尺寸，缩略图按需载入且缓存有界", delegate
            {
                var images = new System.Collections.Generic.List<ImageContent>();
                var field = typeof(ImageContent).GetField("thumbnail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                for (int i = 0; i < 80; i++) images.Add(ImageContent.Read(WritePicture("缓存" + i + ".png", (byte)i), false));
                Check(images.All(image => field.GetValue(image) == null));
                foreach (var image in images) Check(image.Thumbnail.IsFrozen && image.Thumbnail.PixelWidth == 8);
                Check(images.Count(image => field.GetValue(image) != null) <= 64 && field.GetValue(images[0]) == null);
                Check(images[0].Thumbnail.PixelHeight == 4);
            });
            Console.WriteLine("核心测试：{0} 通过，{1} 失败", passed, failed);
            Environment.ExitCode = failed == 0 ? 0 : 1;
        }

        internal static BitmapSource Picture(byte color)
        {
            byte[] pixels = new byte[8 * 4 * 3];
            for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = color; pixels[i + 1] = 180; pixels[i + 2] = 240; }
            var image = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Rgb24, null, pixels, 24);
            image.Freeze();
            return image;
        }

        static string WritePicture(string name, byte color)
        {
            string path = Path.Combine(root, name);
            File.WriteAllBytes(path, ImageContent.EncodeClipboard(Picture(color)));
            return path;
        }

        static void Check(bool condition) { if (!condition) throw new Exception("断言未通过"); }

        static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("通过：" + name); }
            catch (Exception error) { failed++; Console.WriteLine("失败：" + name + " " + error.Message); }
        }
    }
}
