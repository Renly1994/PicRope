using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenLine
{
    internal sealed class ClipboardPicture
    {
        public byte[] Bytes;
        public string Extension;
    }

    internal sealed class CapturePipeline : IDisposable
    {
        readonly Gallery gallery;
        readonly Dispatcher dispatcher;
        readonly string folder;
        readonly CaptureReceipts receipts = new CaptureReceipts();
        readonly Dictionary<string, int> pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> processing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        readonly DateTime launched = DateTime.UtcNow;
        FileSystemWatcher watcher;
        uint clipboardSequence;
        DateTime clipboardDue = DateTime.MaxValue;
        bool clipboardBusy, disposed;
        Point? clipboardOrigin;
        int ticks;
        public event Action<CaptureItem, Point?> Arrived;
        public event Action<string> Failed;

        public CapturePipeline(Gallery gallery, Dispatcher dispatcher, string folder)
        {
            this.gallery = gallery;
            this.dispatcher = dispatcher;
            this.folder = folder;
            clipboardSequence = WindowsNative.GetClipboardSequenceNumber();
            timer.Tick += Tick;
        }

        public bool CollectClipboard
        {
            get { return gallery.Store.State.CollectClipboard; }
            set
            {
                gallery.Store.State.CollectClipboard = value;
                gallery.Store.Save();
                clipboardSequence = WindowsNative.GetClipboardSequenceNumber();
                clipboardDue = DateTime.MaxValue;
                gallery.Notify();
            }
        }

        public bool AutoHang
        {
            get { return gallery.Store.State.AutoHang; }
            set
            {
                gallery.Store.State.AutoHang = value;
                gallery.Store.Save();
                gallery.Notify();
            }
        }

        public void IgnoreCurrentClipboard()
        {
            clipboardSequence = WindowsNative.GetClipboardSequenceNumber();
            clipboardDue = DateTime.MaxValue;
        }

        public async Task Start()
        {
            AttachWatcher();
            timer.Start();
            var saved = gallery.Store.State.Images.ToArray();
            var collected = saved.Concat(gallery.Store.State.Collection);
            if (Directory.Exists(gallery.Store.Inbox))
                collected = collected.Concat(Directory.EnumerateFiles(gallery.Store.Inbox).Where(FileRules.IsImage));
            // 恢复图片篮子时不触发自动挂图，也不重新计入截图去重记录。
            foreach (string path in collected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            {
                if (disposed) return;
                if (!File.Exists(path)) continue;
                try
                {
                    ImageContent image = await Task.Run(() => ImageContent.Read(path, false));
                    if (disposed) return;
                    if (saved.Contains(path, StringComparer.OrdinalIgnoreCase)) gallery.Add(path, image);
                    else gallery.Collect(path, image);
                }
                catch (IOException) { }
                catch (NotSupportedException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        void AttachWatcher()
        {
            if (watcher != null || !Directory.Exists(folder)) return;
            watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Created += delegate(object sender, FileSystemEventArgs args) { Queue(args.FullPath); };
            watcher.Changed += delegate(object sender, FileSystemEventArgs args) { Queue(args.FullPath); };
            watcher.Renamed += delegate(object sender, RenamedEventArgs args) { Queue(args.FullPath); };
            watcher.EnableRaisingEvents = true;
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (File.GetCreationTimeUtc(path) < launched) seen.Add(path);
                else Queue(path);
            }
        }

        void Queue(string path)
        {
            if (!FileRules.IsImage(path) || disposed) return;
            dispatcher.BeginInvoke(new Action(delegate { if (!disposed && !seen.Contains(path)) pending[path] = 0; }));
        }

        public void ClipboardChanged()
        {
            if (CollectClipboard && WindowsNative.GetClipboardSequenceNumber() != clipboardSequence)
            {
                var cursor = WindowsNative.Cursor();
                clipboardOrigin = new Point(cursor.X, cursor.Y);
                clipboardDue = DateTime.UtcNow.AddMilliseconds(160);
            }
        }

        void Tick(object sender, EventArgs args)
        {
            AttachWatcher();
            foreach (var candidate in pending.ToArray())
                if (!processing.Contains(candidate.Key)) ProcessFile(candidate.Key);
            if (!clipboardBusy && DateTime.UtcNow >= clipboardDue)
            {
                clipboardDue = DateTime.MaxValue;
                ReadClipboard();
            }
            if (++ticks % 10 == 0)
            {
                gallery.Prune();
                foreach (var item in gallery.Collected.ToArray())
                    if (!processing.Contains(item.Path) && File.GetLastWriteTimeUtc(item.Path) != item.Written) Refresh(item);
            }
        }

        async void ProcessFile(string path)
        {
            processing.Add(path);
            bool complete = await Import(path, CaptureSource.Folder, true);
            processing.Remove(path);
            if (disposed) return;
            if (complete) { seen.Add(path); pending.Remove(path); }
            else if (pending.ContainsKey(path) && ++pending[path] >= 20) pending.Remove(path);
        }

        public async Task<bool> Import(string path, CaptureSource? source, bool announce)
        {
            if (source.HasValue ? gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)) : gallery.Contains(path)) return true;
            string copy = null;
            try
            {
                ImageContent image = await Task.Run(() => ImageContent.Read(path));
                if (disposed) return false;
                if (source.HasValue && !receipts.Accept(image.Fingerprint, source.Value, DateTime.UtcNow)) return true;
                if (!source.HasValue && !FileRules.IsInside(path, gallery.Store.Inbox))
                {
                    copy = await Task.Run(() => CopyToInbox(path));
                    if (disposed) return false;
                    path = copy;
                }
                if (source.HasValue && !AutoHang) gallery.Collect(path, image);
                else
                {
                    gallery.Add(path, image);
                    if (announce && Arrived != null)
                    {
                        var cursor = WindowsNative.Cursor();
                        Arrived(gallery.Items[gallery.Items.Count - 1], source.HasValue ? new Point?(new Point(cursor.X, cursor.Y)) : null);
                    }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                if (copy != null && !gallery.Collected.Any(i => FileRules.SameFile(i.Path, copy)) && File.Exists(copy)) File.Delete(copy);
            }
        }

        public string CopyToInbox(string path)
        {
            Directory.CreateDirectory(gallery.Store.Inbox);
            string name = Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(path);
            string copy = Path.Combine(gallery.Store.Inbox, name);
            try { File.Copy(path, copy); }
            catch { if (File.Exists(copy)) File.Delete(copy); throw; }
            return copy;
        }

        public void Hang(CaptureItem item)
        {
            if (disposed || gallery.Contains(item.Path)) return;
            if (!File.Exists(item.Path)) { gallery.Prune(); return; }
            gallery.Add(item.Path, item.Image);
            if (Arrived != null) Arrived(item, null);
        }

        public async Task<string> AcceptCapture(BitmapSource picture)
        {
            byte[] png = await Task.Run(() => ImageContent.EncodeClipboard(picture));
            ImageContent image = await Task.Run(() => ImageContent.Decode(png));
            string path = Path.Combine(gallery.Store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            Directory.CreateDirectory(gallery.Store.Inbox);
            await Task.Run(() => File.WriteAllBytes(path, png));
            if (disposed) return path;
            receipts.Accept(image.Fingerprint, CaptureSource.Clipboard, DateTime.UtcNow);
            if (AutoHang) { gallery.Add(path, image); if (Arrived != null) Arrived(gallery.Items.Last(), null); }
            else gallery.Collect(path, image);
            var data = new DataObject(); data.SetImage(picture); data.SetData("PNG", new MemoryStream(png)); data.SetData(ClipboardRules.OwnFormat, "1");
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            await WindowsClipboard.Write(data); IgnoreCurrentClipboard();
            return path;
        }

        async void Refresh(CaptureItem item)
        {
            processing.Add(item.Path);
            try
            {
                ImageContent image = await Task.Run(() => ImageContent.Read(item.Path));
                if (!disposed && gallery.Collected.Contains(item))
                {
                    item.Image = image;
                    item.Written = File.GetLastWriteTimeUtc(item.Path);
                    gallery.Notify();
                }
            }
            catch (IOException) { }
            catch (NotSupportedException) { }
            finally { processing.Remove(item.Path); }
        }

        async void ReadClipboard()
        {
            clipboardBusy = true;
            uint sequence = WindowsNative.GetClipboardSequenceNumber();
            Point? origin = clipboardOrigin;
            try
            {
                if (!CollectClipboard || sequence == clipboardSequence) return;
                clipboardSequence = sequence;
                ClipboardPicture picture = await ReadClipboardImage(sequence);
                if (picture == null || disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                ImageContent content = await Task.Run(() => ImageContent.Decode(picture.Bytes));
                if (disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                if (!receipts.Accept(content.Fingerprint, CaptureSource.Clipboard, DateTime.UtcNow)) return;
                Directory.CreateDirectory(gallery.Store.Inbox);
                string path = Path.Combine(gallery.Store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + picture.Extension);
                await Task.Run(() => File.WriteAllBytes(path, picture.Bytes));
                if (disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) { File.Delete(path); return; }
                if (!AutoHang) gallery.Collect(path, content);
                else
                {
                    gallery.Add(path, content);
                    if (Arrived != null) Arrived(gallery.Items[gallery.Items.Count - 1], origin);
                }
            }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                if (Failed != null) Failed("这张图片暂时无法读取，请重试。");
            }
            finally { clipboardBusy = false; }
        }

        internal static Task<ClipboardPicture> ReadClipboardImage(uint sequence)
        {
            return WindowsClipboard.ReadOnSta(delegate
            {
                IDataObject data = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                    try { data = Clipboard.GetDataObject(); break; }
                    catch (ExternalException) { System.Threading.Thread.Sleep(50); }
                }
                if (data == null || WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                string source = WindowsNative.ClipboardOwnerName();
                if (WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                return ReadImage(data, source).GetAwaiter().GetResult();
            });
        }

        internal static async Task<ClipboardPicture> ReadImage(IDataObject data, string source)
        {
            string imageFile;
            if (!ClipboardRules.IsImage(data, source, out imageFile)) return null;
            if (imageFile != null)
            {
                byte[] original = await Task.Run(delegate
                {
                    using (var stream = new FileStream(imageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length == 0 || stream.Length > 64L * 1024 * 1024) throw new InvalidDataException("图片文件大小无效");
                        return WindowsClipboard.ReadImageBytes(stream);
                    }
                });
                return new ClipboardPicture { Bytes = original, Extension = Path.GetExtension(imageFile) };
            }
            string extension;
            byte[] bytes = WindowsClipboard.ReadEncodedImage(data, out extension);
            if (bytes == null)
            {
                BitmapSource image = data.GetData(DataFormats.Bitmap) as BitmapSource;
                if (image == null) return null;
                image.Freeze(); bytes = await Task.Run(() => ImageContent.EncodeClipboard(image)); extension = ".png";
            }
            return bytes.Length > 64L * 1024 * 1024 ? null : new ClipboardPicture { Bytes = bytes, Extension = extension };
        }

        public void Dispose()
        {
            disposed = true;
            timer.Stop();
            if (watcher != null) watcher.Dispose();
        }
    }
}
