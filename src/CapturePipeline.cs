using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        public ImageContent Content;
    }

    internal sealed class CapturePipeline : IDisposable
    {
        readonly Gallery gallery;
        readonly Dispatcher dispatcher;
        readonly string folder;
        readonly CaptureReceipts receipts = new CaptureReceipts();
        readonly SemaphoreSlim importGate = new SemaphoreSlim(2, 2);
        readonly Dictionary<string, DateTime> pendingObserved = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> processing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        readonly DateTime launched = DateTime.UtcNow;
        FileSystemWatcher watcher;
        uint clipboardSequence, scheduledSequence;
        int clipboardRetries;
        DateTime clipboardObserved;
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
            DateTime observed = DateTime.UtcNow;
            dispatcher.BeginInvoke(new Action(delegate
            {
                if (disposed || seen.Contains(path) || pending.ContainsKey(path)) return;
                pending[path] = 0; pendingObserved[path] = observed;
            }));
        }

        public void ClipboardChanged()
        {
            uint sequence = WindowsNative.GetClipboardSequenceNumber();
            if (CollectClipboard && sequence != clipboardSequence)
            {
                if (scheduledSequence != sequence)
                {
                    scheduledSequence = sequence; clipboardObserved = DateTime.UtcNow; clipboardRetries = 0;
                }
                var cursor = WindowsNative.Cursor();
                clipboardOrigin = new Point(cursor.X, cursor.Y);
                clipboardDue = DateTime.UtcNow.AddMilliseconds(160);
            }
        }

        void Tick(object sender, EventArgs args)
        {
            AttachWatcher();
            foreach (var candidate in pending.ToArray())
                if (processing.Count < 2 && !processing.Contains(candidate.Key)) ProcessFile(candidate.Key);
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
            bool complete = false;
            try { complete = await Import(path, CaptureSource.Folder, true, pendingObserved[path]); }
            catch (Exception error) { gallery.Store.Log(error); }
            finally { processing.Remove(path); }
            if (disposed) return;
            if (complete) { seen.Add(path); pending.Remove(path); pendingObserved.Remove(path); }
            else if (pending.ContainsKey(path) && ++pending[path] >= 20) { pending.Remove(path); pendingObserved.Remove(path); }
        }

        public Task<bool> Import(string path, CaptureSource? source, bool announce)
        {
            return Import(path, source, announce, DateTime.UtcNow);
        }

        async Task<bool> Import(string path, CaptureSource? source, bool announce, DateTime observed)
        {
            await importGate.WaitAsync();
            try { return await ImportCore(path, source, announce, observed); }
            finally { importGate.Release(); }
        }

        async Task<bool> ImportCore(string path, CaptureSource? source, bool announce, DateTime observed)
        {
            if (disposed) return false;
            if (source.HasValue ? gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)) : gallery.Contains(path)) return true;
            string copy = null;
            ImageContent image = null;
            bool recorded = false, commitAttempted = false;
            try
            {
                image = await Task.Run(() => ImageContent.Read(path));
                if (disposed) return false;
                // Another import may have completed while this image was decoding.
                if (source.HasValue ? gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)) : gallery.Contains(path)) return true;
                if (!source.HasValue && !FileRules.IsInside(path, gallery.Store.Inbox))
                {
                    copy = await Task.Run(() => CopyToInbox(path));
                    if (disposed) return false;
                    path = copy;
                }
                if (source.HasValue && receipts.IsDuplicate(image.Fingerprint, source.Value, observed)) return true;
                bool added = !gallery.Contains(path);
                commitAttempted = true;
                if (source.HasValue && !AutoHang) gallery.Collect(path, image);
                else
                {
                    gallery.Add(path, image);
                    if (source.HasValue) { receipts.Record(image.Fingerprint, source.Value, observed); recorded = true; }
                    if (added && announce && Arrived != null)
                    {
                        var cursor = WindowsNative.Cursor();
                        Arrived(gallery.Items[gallery.Items.Count - 1], source.HasValue ? new Point?(new Point(cursor.X, cursor.Y)) : null);
                    }
                }
                if (source.HasValue && !AutoHang) { receipts.Record(image.Fingerprint, source.Value, observed); recorded = true; }
                return true;
            }
            catch (IOException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                // Gallery may have retained the item before Save/Notify failed.
                // Retrying that live item must not create a second cross-source entry.
                if (commitAttempted && !recorded && source.HasValue && image != null && gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)))
                    receipts.Record(image.Fingerprint, source.Value, observed);
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

        public async Task<string> AcceptCapture(BitmapSource picture, Point? origin = null)
        {
            DateTime observed = DateTime.UtcNow;
            byte[] png = await Task.Run(() => ImageContent.EncodeClipboard(picture));
            ImageContent image = await Task.Run(() => ImageContent.Decode(png));
            string path = Path.Combine(gallery.Store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            Directory.CreateDirectory(gallery.Store.Inbox);
            await Task.Run(() => File.WriteAllBytes(path, png));
            if (disposed) return path;

            if (AutoHang) { gallery.Add(path, image); if (Arrived != null) Arrived(gallery.Items.Last(), origin); }
            else gallery.Collect(path, image);
            receipts.Record(image.Fingerprint, CaptureSource.Clipboard, observed);
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
            DateTime observed = clipboardObserved;
            string path = null;
            bool committed = false;
            ImageContent content = null;
            try
            {
                if (!CollectClipboard || sequence == clipboardSequence) return;
                ClipboardPicture picture = await ReadClipboardImage(sequence);
                if (picture == null) { clipboardSequence = sequence; return; }
                if (disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                content = picture.Content ?? await Task.Run(() => ImageContent.Decode(picture.Bytes));
                if (disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                // Fast path avoids writing a second file when the folder already committed.
                if (receipts.IsDuplicate(content.Fingerprint, CaptureSource.Clipboard, observed)) { clipboardSequence = sequence; return; }
                Directory.CreateDirectory(gallery.Store.Inbox);
                path = Path.Combine(gallery.Store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + picture.Extension);
                await Task.Run(() => File.WriteAllBytes(path, picture.Bytes));
                if (disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                if (receipts.IsDuplicate(content.Fingerprint, CaptureSource.Clipboard, observed)) { clipboardSequence = sequence; return; }
                if (!AutoHang) gallery.Collect(path, content);
                else
                {
                    gallery.Add(path, content);
                    committed = true; clipboardSequence = sequence;
                    receipts.Record(content.Fingerprint, CaptureSource.Clipboard, observed);
                    if (Arrived != null) Arrived(gallery.Items[gallery.Items.Count - 1], origin);
                }
                if (!committed)
                {
                    committed = true; clipboardSequence = sequence;
                    receipts.Record(content.Fingerprint, CaptureSource.Clipboard, observed);
                }
            }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                if (!committed && path != null && content != null && gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)))
                {
                    committed = true; clipboardSequence = sequence;
                    receipts.Record(content.Fingerprint, CaptureSource.Clipboard, observed);
                }
                if (!committed && !disposed && CollectClipboard && WindowsNative.GetClipboardSequenceNumber() == sequence && ++clipboardRetries <= 3)
                    clipboardDue = DateTime.UtcNow.AddMilliseconds(200 * clipboardRetries);
                else if (Failed != null) Failed("这张图片暂时无法读取，请重试。");
            }
            finally
            {
                clipboardBusy = false;
                if (!committed && path != null && !gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)))
                {
                    try { File.Delete(path); }
                    catch (IOException error) { gallery.Store.Log(error); }
                    catch (UnauthorizedAccessException error) { gallery.Store.Log(error); }
                }
            }
        }

        internal static Task<ClipboardPicture> ReadClipboardImage(uint sequence)
        {
            return WindowsClipboard.ReadOnSta(delegate
            {
                IDataObject data = null;
                ExternalException last = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                    try { data = Clipboard.GetDataObject(); break; }
                    catch (ExternalException error) { last = error; System.Threading.Thread.Sleep(50); }
                }
                if (WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                if (data == null) { if (last != null) throw last; return null; }
                string source = WindowsNative.ClipboardOwnerName();
                if (WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                return ReadImage(data, source).GetAwaiter().GetResult();
            });
        }

        // Called on the clipboard STA worker. Keep all IDataObject access on that
        // same apartment, including fallback after a delayed/invalid representation.
        internal static Task<ClipboardPicture> ReadImage(IDataObject data, string source)
        {
            return Task.FromResult(ReadImageCore(data, source));
        }

        static ClipboardPicture ReadImageCore(IDataObject data, string source)
        {
            string imageFile;
            if (!ClipboardRules.IsImage(data, source, out imageFile)) return null;
            Exception last = null;
            if (imageFile != null)
            {
                try
                {
                    using (var stream = new FileStream(imageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        return DecodedPicture(WindowsClipboard.ReadImageBytes(stream), Path.GetExtension(imageFile));
                }
                catch (Exception error) { if (!ImageReadFailure(error)) throw; last = error; }
            }
            foreach (var format in ClipboardRules.EncodedFormats)
            {
                try
                {
                    if (!data.GetDataPresent(format.Key, false)) continue;
                    byte[] bytes = WindowsClipboard.ImageBytes(data.GetData(format.Key, false));
                    if (bytes != null) return DecodedPicture(bytes, format.Value);
                }
                catch (Exception error) { if (!ImageReadFailure(error)) throw; last = error; }
            }
            try
            {
                BitmapSource image = data.GetData(DataFormats.Bitmap) as BitmapSource;
                if (image != null)
                {
                    if ((long)image.PixelWidth * image.PixelHeight > 80000000) throw new InvalidDataException("图片尺寸过大");
                    return DecodedPicture(ImageContent.EncodeClipboard(image), ".png");
                }
            }
            catch (Exception error) { if (!ImageReadFailure(error)) throw; last = error; }
            // WPF does not promise a Bitmap conversion for every native DIB/DIBV5.
            foreach (string format in new[] { "Format17", DataFormats.Dib })
            {
                try
                {
                    if (!data.GetDataPresent(format, false)) continue;
                    byte[] dib = WindowsClipboard.ImageBytes(data.GetData(format, false));
                    if (dib != null)
                    {
                        using (var stream = new MemoryStream(WindowsClipboard.DibToBitmap(dib)))
                        {
                            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                            // Match the Bitmap path, including Windows screenshots with an all-zero alpha channel.
                            return DecodedPicture(ImageContent.EncodeClipboard(frame), ".png");
                        }
                    }
                }
                catch (Exception error) { if (!ImageReadFailure(error)) throw; last = error; }
            }
            if (last != null) throw last; // Preserve bounded pipeline retries and diagnostics.
            return null;
        }

        static ClipboardPicture DecodedPicture(byte[] bytes, string extension)
        {
            if (bytes.Length == 0 || bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("图片数据大小无效");
            return new ClipboardPicture { Bytes = bytes, Extension = extension, Content = ImageContent.Decode(bytes) };
        }

        static bool ImageReadFailure(Exception error)
        {
            return error is FormatException || error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is NotSupportedException
                || error is ArgumentException || error is ExternalException || error is InvalidOperationException;
        }

        public void Dispose()
        {
            disposed = true;
            timer.Stop();
            if (watcher != null) watcher.Dispose();
        }
    }
}
