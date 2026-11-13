using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    internal static class ImageEditFiles
    {
        public static byte[] ReadExport(Stream stream)
        {
            if (stream == null || !stream.CanRead) throw new InvalidDataException("缺少编辑结果。");
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + count > 64L * 1024 * 1024) throw new InvalidDataException("编辑结果过大。");
                    memory.Write(buffer, 0, count);
                }
                byte[] bytes = memory.ToArray();
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                if (bytes.Length < signature.Length) throw new InvalidDataException("编辑结果不是 PNG 图片。");
                for (int i = 0; i < signature.Length; i++)
                    if (bytes[i] != signature[i]) throw new InvalidDataException("编辑结果不是 PNG 图片。");
                ImageContent.Decode(bytes);
                return bytes;
            }
        }

        public static byte[] ReadPng(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(image);
                using (var memory = new MemoryStream()) { encoder.Save(memory); return memory.ToArray(); }
            }
        }

        public static byte[] Decode(string data)
        {
            const string prefix = "data:image/png;base64,";
            if (data == null || !data.StartsWith(prefix, StringComparison.Ordinal) || data.Length > 90 * 1024 * 1024)
                throw new InvalidDataException("编辑结果不是有效的 PNG 图片。");
            byte[] bytes = Convert.FromBase64String(data.Substring(prefix.Length));
            if (bytes.Length < 8 || bytes.Length > 64 * 1024 * 1024 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71)
                throw new InvalidDataException("编辑结果不是有效的 PNG 图片。");
            ImageContent.Decode(bytes);
            return bytes;
        }

        public static void Save(string path, byte[] png)
        {
            BitmapEncoder encoder;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": encoder = new PngBitmapEncoder(); break;
                case ".jpg": case ".jpeg": encoder = new JpegBitmapEncoder { QualityLevel = 95 }; break;
                case ".bmp": encoder = new BmpBitmapEncoder(); break;
                case ".gif": encoder = new GifBitmapEncoder(); break;
                case ".tif": case ".tiff": encoder = new TiffBitmapEncoder(); break;
                default: throw new NotSupportedException("请选择 PNG、JPG、BMP、GIF 或 TIFF 格式。");
            }
            using (var stream = new MemoryStream(png))
                encoder.Frames.Add(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) encoder.Save(stream);
                FileRules.Commit(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
