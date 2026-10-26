using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    internal sealed class SavedState
    {
        public bool CollectClipboard { get; set; }
        public bool CollectTextClipboard { get; set; }
        public bool AutoHang { get; set; }
        public string Theme { get; set; }
        public string DisplayDevice { get; set; }
        public string LastUpdateNotification { get; set; }
        public List<string> Images { get; set; }
        public List<string> Collection { get; set; }
        public List<TextItem> Texts { get; set; }

        public SavedState() { Images = new List<string>(); Collection = new List<string>(); Texts = new List<TextItem>(); Theme = "classic"; AutoHang = true; }
    }

    internal sealed class StateStore
    {
        public static string DefaultRoot { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"); } }
        public readonly string Root;
        public readonly string Inbox;
        public readonly SavedState State;
        readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly string file;

        public StateStore(string root)
        {
            Root = Path.GetFullPath(root);
            Inbox = Path.Combine(Root, "Captures");
            file = Path.Combine(Root, "state.json");
            State = new SavedState();
            if (!File.Exists(file)) return;
            try
            {
                State = serializer.Deserialize<SavedState>(File.ReadAllText(file, Encoding.UTF8)) ?? new SavedState();
                if (State.Images == null) State.Images = new List<string>();
                if (State.Collection == null) State.Collection = new List<string>();
                if (State.Texts == null) State.Texts = new List<TextItem>();
            }
            catch (ArgumentException ex) { Log(ex); }
            catch (InvalidOperationException ex) { Log(ex); }
            catch (IOException ex) { Log(ex); }
        }

        public void Save()
        {
            Directory.CreateDirectory(Root);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, serializer.Serialize(State), new UTF8Encoding(false));
            FileRules.Commit(temporary, file);
        }

        public void Log(Exception error)
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(Path.Combine(Root, "error.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
        }
    }

    internal sealed class TextItem
    {
        public string Id { get; set; }
        public string Content { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    internal sealed class TextCollection
    {
        public readonly StateStore Store;
        public List<TextItem> Items { get { return Store.State.Texts; } }
        public event Action Changed;

        public TextCollection(StateStore store) { Store = store; }

        public bool CollectClipboard
        {
            get { return Store.State.CollectTextClipboard; }
            set
            {
                bool previous = CollectClipboard;
                if (previous == value) return;
                Store.State.CollectTextClipboard = value;
                try { Store.Save(); }
                catch { Store.State.CollectTextClipboard = previous; throw; }
                if (Changed != null) Changed();
            }
        }

        public TextItem Add(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            var existing = Items.Find(item => item.Content == content);
            if (existing != null) return existing;
            var added = new TextItem { Id = Guid.NewGuid().ToString("N"), Content = content, CreatedUtc = DateTime.UtcNow };
            Items.Insert(0, added);
            try { Store.Save(); }
            catch { Items.Remove(added); throw; }
            if (Changed != null) Changed();
            return added;
        }

        public void Remove(TextItem item)
        {
            int index = Items.IndexOf(item);
            if (index < 0) return;
            Items.RemoveAt(index);
            try { Store.Save(); }
            catch { Items.Insert(index, item); throw; }
            if (Changed != null) Changed();
        }
    }

    internal static class FileRules
    {
        static readonly string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

        public static void Commit(string temporary, string destination)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(destination)) File.Replace(temporary, destination, null);
                    else File.Move(temporary, destination);
                    return;
                }
                catch (IOException error)
                {
                    int code = error.HResult & 65535;
                    if (attempt == 4 || (code != 32 && code != 33 && code != 1175)) throw;
                    // 索引或杀毒扫描短暂占用时重试，仍以原子替换保护已有文件。
                    System.Threading.Thread.Sleep(30);
                }
            }
        }

        public static bool IsImage(string path)
        {
            return extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsInside(string path, string directory)
        {
            string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool SameFile(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class ClipboardRules
    {
        static readonly string[] excluded = { "HTML Format", "UnicodeText", "Text", "System.String", "FileDrop", "Rich Text Format" };

        public static bool IsPlainImage(IEnumerable<string> formats)
        {
            var values = new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);
            return !values.Overlaps(excluded) && (values.Contains("PNG") || values.Contains("image/png")
                || values.Contains("Bitmap") || values.Contains("DeviceIndependentBitmap"));
        }
    }

    internal sealed class ImageContent
    {
        public readonly BitmapSource Thumbnail;
        public readonly string Fingerprint;
        public readonly int PixelWidth, PixelHeight;

        ImageContent(BitmapSource thumbnail, string fingerprint, int width, int height)
        {
            Thumbnail = thumbnail;
            Fingerprint = fingerprint;
            PixelWidth = width;
            PixelHeight = height;
        }

        public static ImageContent Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length == 0 || stream.Length > 64L * 1024 * 1024) throw new InvalidDataException("Image file size is invalid");
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return Decode(memory.ToArray());
                }
            }
        }

        public static ImageContent Decode(byte[] bytes)
        {
            using (var memory = new MemoryStream(bytes))
            {
                var full = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                if (full.PixelWidth <= 0 || full.PixelHeight <= 0 || (long)full.PixelWidth * full.PixelHeight > 80000000)
                    throw new InvalidDataException("Image dimensions are invalid");
                string fingerprint = HashPixels(full);
                var thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.StreamSource = new MemoryStream(bytes);
                if (full.PixelWidth >= full.PixelHeight) thumbnail.DecodePixelWidth = Math.Min(240, full.PixelWidth);
                else thumbnail.DecodePixelHeight = Math.Min(240, full.PixelHeight);
                thumbnail.EndInit();
                thumbnail.Freeze();
                return new ImageContent(thumbnail, fingerprint, full.PixelWidth, full.PixelHeight);
            }
        }

        public static byte[] EncodeClipboard(BitmapSource image)
        {
            // 部分 Windows 位图的透明通道全为零，截图应按不透明图像保存。
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            byte[] pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            bool emptyAlpha = true;
            for (int i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 0) { emptyAlpha = false; break; }
            if (emptyAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var memory = new MemoryStream()) { encoder.Save(memory); return memory.ToArray(); }
        }

        public static string HashPixels(BitmapSource image)
        {
            var color = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = color.PixelWidth * 4;
            byte[] pixels = new byte[stride * color.PixelHeight + 8];
            BitConverter.GetBytes(color.PixelWidth).CopyTo(pixels, 0);
            BitConverter.GetBytes(color.PixelHeight).CopyTo(pixels, 4);
            color.CopyPixels(pixels, stride, 8);
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(pixels));
        }
    }

    internal enum CaptureSource { Folder, Clipboard }

    internal sealed class CaptureReceipts
    {
        sealed class Receipt
        {
            public string Hash;
            public CaptureSource Source;
            public DateTime Time;
            public bool Paired;
        }

        readonly List<Receipt> recent = new List<Receipt>();

        public bool Accept(string hash, CaptureSource source, DateTime time)
        {
            recent.RemoveAll(r => time - r.Time > TimeSpan.FromSeconds(5));
            // 只合并文件夹与剪贴板对同一次截图的通知；连续截取相同内容仍可保留。
            var pair = recent.FirstOrDefault(r => !r.Paired && r.Hash == hash && r.Source != source);
            if (pair != null) { pair.Paired = true; return false; }
            recent.Add(new Receipt { Hash = hash, Source = source, Time = time });
            return true;
        }
    }

    internal sealed class CaptureItem
    {
        public readonly string Path;
        public ImageContent Image;
        public DateTime Written;

        public CaptureItem(string path, ImageContent image)
        {
            Path = System.IO.Path.GetFullPath(path);
            Image = image;
            Written = File.GetLastWriteTimeUtc(Path);
        }
    }

    internal sealed class Gallery
    {
        public readonly List<CaptureItem> Items = new List<CaptureItem>();
        public readonly List<CaptureItem> Collected = new List<CaptureItem>();
        public readonly StateStore Store;
        public event Action Changed;

        public Gallery(StateStore store) { Store = store; }

        public bool Contains(string path) { return Items.Any(i => FileRules.SameFile(i.Path, path)); }

        public CaptureItem Collect(string path, ImageContent image)
        {
            var item = Collected.FirstOrDefault(i => FileRules.SameFile(i.Path, path));
            if (item != null) return item;
            item = new CaptureItem(path, image);
            Collected.Add(item);
            Save();
            Notify();
            return item;
        }

        public void Add(string path, ImageContent image)
        {
            if (Contains(path)) return;
            var item = Collected.FirstOrDefault(i => FileRules.SameFile(i.Path, path));
            if (item == null) { item = new CaptureItem(path, image); Collected.Add(item); }
            else if (item.Image != image) { item.Image = image; item.Written = File.GetLastWriteTimeUtc(path); }
            Items.Add(item);
            if (Items.Count > 50) Items.RemoveAt(0);
            Save();
            Notify();
        }

        public void Remove(CaptureItem item)
        {
            Items.Remove(item);
            Save();
            Notify();
        }

        public void RemoveRange(IEnumerable<CaptureItem> items)
        {
            bool removed = false;
            foreach (var item in items.ToList()) removed |= Items.Remove(item);
            if (!removed) return;
            Save();
            Notify();
        }

        public void Clear()
        {
            Items.Clear();
            Save();
            Notify();
        }

        public void Prune()
        {
            int removed = Items.RemoveAll(i => !File.Exists(i.Path));
            removed += Collected.RemoveAll(i => !File.Exists(i.Path));
            if (removed == 0) return;
            Save();
            Notify();
        }

        public void Notify() { if (Changed != null) Changed(); }

        void Save()
        {
            Store.State.Images = Items.Select(i => i.Path).ToList();
            Store.State.Collection = Collected.Select(i => i.Path).ToList();
            Store.Save();
        }
    }
}
