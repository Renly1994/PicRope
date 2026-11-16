using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class ClipboardImageTests
    {
        static int checks;

        [STAThread]
        static void Main(string[] arguments)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                if (arguments.Contains("--current-clipboard")) CheckCurrentClipboard();
                else Run();
            }
            catch (Exception error) { Console.WriteLine("剪贴板识别验证失败：" + error.Message); Environment.ExitCode = 1; }
        }

        static void CheckCurrentClipboard()
        {
            uint sequence = WindowsNative.GetClipboardSequenceNumber();
            var data = Clipboard.GetDataObject();
            string source = WindowsNative.ClipboardOwnerName();
            string imageFile = null;
            Check(data != null && ClipboardRules.IsImage(data, source, out imageFile), "当前剪贴板图片未识别");
            var picture = CapturePipeline.ReadImage(data, source).GetAwaiter().GetResult();
            Check(picture != null && picture.Bytes.Length > 0 && picture.Bytes.Length <= 64 * 1024 * 1024, "当前剪贴板未提供有效图片数据");
            Check(imageFile != null && picture.Extension == Path.GetExtension(imageFile) && picture.Bytes.SequenceEqual(File.ReadAllBytes(imageFile)), "保留微信图片原文件的格式与内容");
            var image = ImageContent.Decode(picture.Bytes);
            Check(image.PixelWidth > 0 && image.PixelHeight > 0 && WindowsNative.GetClipboardSequenceNumber() == sequence, "当前剪贴板图片解码失败或内容已变化");
            Console.WriteLine("当前微信剪贴板图片识别和解码通过。");
        }

        static void Run()
        {
            Check(Image("PNG"), "纯 PNG 图片可收集");
            Check(Image("DeviceIndependentBitmap"), "纯 DIB 图片可收集");
            Check(ClipboardRules.IsPlainImage(new[] { "Format17" }), "DIBV5 位图可识别");
            foreach (string format in new[] { "image/jpeg", "image/jpg", "image/jpe", "image/bmp", "image/gif", "image/tiff", "image/tif" })
            {
                var encoded = EncodedFixture(format);
                var clip = new DataObject(); clip.SetData(format, new MemoryStream(encoded.Item2));
                Check(ClipboardRules.IsImage(clip), format + " 图片可识别");
                var result = CapturePipeline.ReadImage(clip, "OtherApp").GetAwaiter().GetResult();
                Check(result != null && result.Extension == encoded.Item1 && result.Bytes.SequenceEqual(encoded.Item2)
                    && ImageContent.Decode(result.Bytes).PixelWidth == 2, format + " 保留原始图片并可解码");
            }
            var mixedFiles = new DataObject(); mixedFiles.SetData("image/jpeg", new byte[] { 1 }); mixedFiles.SetData(DataFormats.FileDrop, new[] { "C:\\a.jpg" });
            Check(!ClipboardRules.IsImage(mixedFiles, "explorer"), "图片格式扩展后仍不自动收集资源管理器文件");
            var mixedText = new DataObject(); mixedText.SetData("image/jpeg", new byte[] { 1 }); mixedText.SetData(DataFormats.UnicodeText, "正文");
            Check(!ClipboardRules.IsImage(mixedText), "图片格式扩展后仍不误收图文");
            byte[] png = ImageContent.EncodeClipboard(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8));
            byte[] jpeg = EncodedFixture("image/jpeg").Item2;
            var choices = new DataObject(); choices.SetData("PNG", png); choices.SetData("image/jpeg", jpeg);
            var preferred = CapturePipeline.ReadImage(new GuardedDataObject(choices, "image/jpeg"), "OtherApp").GetAwaiter().GetResult();
            Check(preferred != null && preferred.Extension == ".png" && preferred.Bytes.SequenceEqual(png), "多格式图片优先无损 PNG，不读取其他格式");
            var brokenFirst = new DataObject(); brokenFirst.SetData("PNG", "not-bytes"); brokenFirst.SetData("image/jpeg", jpeg);
            var fallback = CapturePipeline.ReadImage(brokenFirst, "OtherApp").GetAwaiter().GetResult();
            Check(fallback != null && fallback.Extension == ".jpg" && fallback.Bytes.SequenceEqual(jpeg), "不可读取的格式不阻止其他编码图片");
            Check(Html("<img src=\"https://example.com/a.png\">", null), "网页单张图片可收集");
            Check(Html("<a href=\"https://example.com/\"><img src=\"https://example.com/a.png\"></a>", "https://example.com/a.png"), "带图片地址的复制可收集");
            Check(Html("Version:1.0\r\n<html><body><!--StartFragment--><img src='a.png'><!--EndFragment--><p>未选中的正文</p></body></html>", null), "只检查 HTML 剪贴板选中片段");
            Check(!Html("<p>正文<img src='a.png'></p>", null), "图文混排不自动收集");
            Check(!Html("<img src='a.png'>", "其他文字"), "附带不相关文字时不自动收集");
            Check(!Html("<img src='a.png'><img src='b.png'>", null), "多图混合内容不自动收集");
            Check(!Html("<script>alert(1)</script><img src='a.png'>", null), "不识别包含其他内容的片段");
            var htmlOnly = new DataObject(); htmlOnly.SetData(DataFormats.Html, "<img src='a.png'>");
            Check(!ClipboardRules.IsImage(htmlOnly), "只有图片链接而没有图片数据不收集");
            var file = new DataObject(); file.SetData("PNG", new byte[] { 1 }); file.SetData(DataFormats.FileDrop, new[] { "C:\\a.png" });
            Check(!ClipboardRules.IsImage(file), "文件复制和图绳自己的复制不自动收集");
            var wechat = new DataObject(); wechat.SetData(DataFormats.FileDrop, new[] { "C:\\image.jpg" });
            wechat.SetData("DeviceIndependentBitmap", new byte[] { 1 }); wechat.SetData("image/png", new byte[] { 1 });
            Check(ClipboardRules.IsImage(wechat, "Weixin"), "微信聊天图片带文件格式时仍收集图片内容");
            Check(ClipboardRules.IsImage(wechat, "WeChat"), "旧版微信的聊天图片可识别");
            Check(!ClipboardRules.IsImage(wechat, "explorer"), "资源管理器复制图片文件仍不自动收集");
            Check(!ClipboardRules.IsImage(wechat, null), "无法确认来源时不把文件当图片");
            var wechatMixed = new DataObject(); wechatMixed.SetData(DataFormats.FileDrop, new[] { "C:\\a.jpg" });
            wechatMixed.SetData("DeviceIndependentBitmap", new byte[] { 1 }); wechatMixed.SetData("image/png", new byte[] { 1 });
            wechatMixed.SetData(DataFormats.UnicodeText, "文字");
            Check(!ClipboardRules.IsImage(wechatMixed, "Weixin"), "微信图文混合内容不自动收集");
            wechatMixed.SetData(ClipboardRules.OwnFormat, "1");
            Check(!ClipboardRules.IsImage(wechatMixed, "Weixin"), "带自身标记的复制不会重复收集");
            var wechatFile = new DataObject(); wechatFile.SetData(DataFormats.FileDrop, new[] { "C:\\file.zip" });
            wechatFile.SetData("DeviceIndependentBitmap", new byte[] { 1 }); wechatFile.SetData("image/png", new byte[] { 1 });
            Check(!ClipboardRules.IsImage(wechatFile, "Weixin"), "微信普通文件不自动收集");
            var wechatMany = new DataObject(); wechatMany.SetData(DataFormats.FileDrop, new[] { "C:\\a.jpg", "C:\\b.jpg" });
            wechatMany.SetData("DeviceIndependentBitmap", new byte[] { 1 }); wechatMany.SetData("image/png", new byte[] { 1 });
            Check(!ClipboardRules.IsImage(wechatMany, "Weixin"), "多文件复制不误认为单张图片");
            string fixture = Path.Combine(Path.GetTempPath(), "picrope-clipboard-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                byte[] contents = png; File.WriteAllBytes(fixture, contents);
                var delayed = new DataObject(); delayed.SetData(DataFormats.FileDrop, new[] { fixture });
                delayed.SetData("DeviceIndependentBitmap", new byte[] { 1 }); delayed.SetData("image/png", new byte[] { 1 });
                var picture = CapturePipeline.ReadImage(new GuardedDataObject(delayed, "PNG", "image/png"), "Weixin").GetAwaiter().GetResult();
                Check(picture != null && picture.Extension == ".png" && picture.Bytes.SequenceEqual(contents), "微信图片直接读取缓存文件，不请求耗时的图片格式");
            }
            finally { File.Delete(fixture); }
            var own = new DataObject(); own.SetData("PNG", new byte[] { 1 }); own.SetData(ClipboardRules.OwnFormat, "1");
            Check(!ClipboardRules.IsImage(own), "图绳自身复制的纯图片不再重复收集");
            var rich = new DataObject(); rich.SetData("PNG", new byte[] { 1 }); rich.SetData(DataFormats.Html, "<img src='a.png'>"); rich.SetData(DataFormats.Rtf, "{\\rtf1 test}");
            Check(!ClipboardRules.IsImage(rich), "富文本内容不自动收集");
            var text = new DataObject(); text.SetData("PNG", new byte[] { 1 }); text.SetData(DataFormats.UnicodeText, "正文");
            Check(!ClipboardRules.IsImage(text), "图片和正文混合且无图片片段时不收集");
            var bytes = new DataObject(); bytes.SetData("image/png", new byte[] { 1, 2, 3 });
            string extension;
            Check(WindowsClipboard.ReadEncodedImage(bytes, out extension).SequenceEqual(new byte[] { 1, 2, 3 }) && extension == ".png", "读取 PNG 字节格式");
            bytes.SetData("image/png", png);
            var plain = CapturePipeline.ReadImage(bytes, "OtherApp").GetAwaiter().GetResult();
            Check(plain != null && plain.Bytes.SequenceEqual(png), "其他软件的 PNG 图片可读取");
            var streamData = new DataObject(); var stream = new MemoryStream(new byte[] { 4, 5, 6 }); stream.Position = 2; streamData.SetData("PNG", stream);
            Check(WindowsClipboard.ReadEncodedImage(streamData, out extension).SequenceEqual(new byte[] { 4, 5, 6 }), "从头读取 PNG 流");
            var bitmapData = new DataObject();
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
            bitmap.Freeze(); bitmapData.SetImage(bitmap);
            var converted = CapturePipeline.ReadImage(bitmapData, "OtherApp").GetAwaiter().GetResult();
            Check(converted != null && ImageContent.Decode(converted.Bytes).PixelWidth == 2, "其他软件只有位图时仍能正确收集");
            var corrupt = new DataObject(); corrupt.SetData("PNG", new byte[] { 1, 2, 3 }); corrupt.SetImage(bitmap);
            Check(CapturePipeline.ReadImage(corrupt, "QQ").GetAwaiter().GetResult().Content.PixelWidth == 2,
                "损坏 PNG 不阻断有效 Bitmap 兜底");
            var badFileFormat = new DataObject(); badFileFormat.SetData("PNG", new FailingImageStream(new FileFormatException("Malformed image fixture"))); badFileFormat.SetImage(bitmap);
            Check(CapturePipeline.ReadImage(badFileFormat, "QQ").GetAwaiter().GetResult().Content.PixelWidth == 2,
                "明确 FileFormatException 不阻断 Bitmap 兜底");
            var badFormat = new DataObject(); badFormat.SetData("PNG", new FailingImageStream(new FormatException("Malformed representation fixture"))); badFormat.SetData("image/jpeg", jpeg);
            Check(CapturePipeline.ReadImage(badFormat, "QQ").GetAwaiter().GetResult().Bytes.SequenceEqual(jpeg),
                "明确 FormatException 可回退其他编码格式");
            corrupt.SetData("image/jpeg", jpeg);
            Check(CapturePipeline.ReadImage(corrupt, "QQ").GetAwaiter().GetResult().Bytes.SequenceEqual(jpeg),
                "损坏 PNG 可回退其他编码格式");
            byte[] bmp = EncodedFixture("image/bmp").Item2;
            var dib = new DataObject(); dib.SetData(DataFormats.Dib, bmp.Skip(14).ToArray(), false);
            Check(CapturePipeline.ReadImage(dib, "QQ").GetAwaiter().GetResult().Content.PixelWidth == 2,
                "只有原生 DIB 时可读取，不依赖自动转换");
            foreach (byte alpha in new byte[] { 0, 128, 255 })
            {
                byte[] native = new byte[124 + 16];
                Buffer.BlockCopy(BitConverter.GetBytes(124), 0, native, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(2), 0, native, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(-2), 0, native, 8, 4);
                native[12] = 1; native[14] = 32; native[16] = 3;
                Buffer.BlockCopy(BitConverter.GetBytes(0x00ff0000), 0, native, 40, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(0x0000ff00), 0, native, 44, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(0x000000ff), 0, native, 48, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(unchecked((int)0xff000000)), 0, native, 52, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(0x73524742), 0, native, 56, 4); // LCS_sRGB
                byte[] pixels = new byte[16];
                for (int i = 0; i < 16; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = alpha; }
                Buffer.BlockCopy(pixels, 0, native, 124, pixels.Length);
                var nativeData = new DataObject(); nativeData.SetData("Format17", native, false);
                var nativePicture = CapturePipeline.ReadImage(nativeData, "QQ").GetAwaiter().GetResult();
                var equivalent = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                Check(nativePicture.Content.Fingerprint == ImageContent.Decode(ImageContent.EncodeClipboard(equivalent)).Fingerprint,
                    "DIBV5 alpha=" + alpha + " 与 Bitmap 路径保持一致");
            }
            var qq = new DataObject(); qq.SetData(DataFormats.FileDrop, new[] { "C:\\missing-qq-screenshot.png" }); qq.SetData("PNG", png);
            Check(ClipboardRules.IsImage(qq, "QQ") && ClipboardRules.IsImage(qq, "TIM"), "QQ/TIM 图片加文件表示可识别");
            Check(CapturePipeline.ReadImage(qq, "QQ").GetAwaiter().GetResult().Bytes.SequenceEqual(png), "缓存文件消失后回退剪贴板图片");
            Check(!ClipboardRules.IsImage(qq, "explorer") && !ClipboardRules.IsImage(qq, null), "仍拒绝文件复制和未知文件来源");
            qq.SetData(DataFormats.UnicodeText, "正文");
            Check(!ClipboardRules.IsImage(qq, "QQ"), "QQ 图文混合仍拒绝");
            Check(WindowsClipboard.ReadEncodedImage(new DataObject(), out extension) == null, "没有编码图片时可回退到位图");
            var large = new DataObject(); large.SetData("PNG", new OversizedStream());
            bool rejected = false; try { WindowsClipboard.ReadEncodedImage(large, out extension); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "过大的 PNG 流在读取前拒绝");
            Console.WriteLine("剪贴板图片识别：{0} 项验证通过", checks);
        }

        static bool Image(string format) { var data = new DataObject(); data.SetData(format, new byte[] { 1 }); return ClipboardRules.IsImage(data); }
        static Tuple<string, byte[]> EncodedFixture(string format)
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
            BitmapEncoder encoder = format == "image/bmp" ? (BitmapEncoder)new BmpBitmapEncoder()
                : format == "image/gif" ? new GifBitmapEncoder()
                : format == "image/tiff" || format == "image/tif" ? new TiffBitmapEncoder() : (BitmapEncoder)new JpegBitmapEncoder();
            string extension = format == "image/bmp" ? ".bmp" : format == "image/gif" ? ".gif"
                : format == "image/tiff" || format == "image/tif" ? ".tiff" : ".jpg";
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream()) { encoder.Save(stream); return Tuple.Create(extension, stream.ToArray()); }
        }
        static bool Html(string html, string text)
        {
            var data = new DataObject(); data.SetData("PNG", new byte[] { 1 }); data.SetData(DataFormats.Html, html);
            if (text != null) data.SetData(DataFormats.UnicodeText, text);
            return ClipboardRules.IsImage(data);
        }
        static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }

        sealed class FailingImageStream : MemoryStream
        {
            readonly Exception failure;
            public FailingImageStream(Exception failure) : base(new byte[] { 1, 2, 3 }) { this.failure = failure; }
            public override int Read(byte[] buffer, int offset, int count) { throw failure; }
        }

        sealed class OversizedStream : Stream
        {
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return 64L * 1024 * 1024 + 1; } }
            public override long Position { get; set; }
            public override int Read(byte[] buffer, int offset, int count) { throw new Exception("超大图片不应被读取"); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void Flush() { }
        }

        sealed class GuardedDataObject : IDataObject
        {
            readonly IDataObject inner;
            readonly string[] forbidden;
            public GuardedDataObject(IDataObject data, params string[] forbiddenFormats) { inner = data; forbidden = forbiddenFormats; }
            public object GetData(string format) { if (forbidden.Contains(format)) throw new Exception("不应请求其他图片格式"); return inner.GetData(format); }
            public object GetData(string format, bool autoConvert) { if (forbidden.Contains(format)) throw new Exception("不应请求其他图片格式"); return inner.GetData(format, autoConvert); }
            public object GetData(Type type) { return inner.GetData(type); }
            public bool GetDataPresent(string format) { return inner.GetDataPresent(format); }
            public bool GetDataPresent(string format, bool autoConvert) { return inner.GetDataPresent(format, autoConvert); }
            public bool GetDataPresent(Type type) { return inner.GetDataPresent(type); }
            public string[] GetFormats() { return inner.GetFormats(); }
            public string[] GetFormats(bool autoConvert) { return inner.GetFormats(autoConvert); }
            public void SetData(object data) { inner.SetData(data); }
            public void SetData(string format, object data) { inner.SetData(format, data); }
            public void SetData(Type type, object data) { inner.SetData(type, data); }
            public void SetData(string format, object data, bool autoConvert) { inner.SetData(format, data, autoConvert); }
        }
    }
}
