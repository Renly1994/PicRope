using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gdi = System.Drawing;
using Gdi2D = System.Drawing.Drawing2D;
using GdiImage = System.Drawing.Imaging;

namespace ScreenLine
{
    // 截图飞行使用有界的 CPU 位图，由桌面合成节奏驱动，独立于绳子的布局刷新。
    internal sealed class CaptureFlightRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct LayerSize { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
        {
            public uint Size; public int Width, Height; public ushort Planes, Bits;
            public uint Compression, ImageSize; public int XDensity, YDensity; public uint Colors, Important;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Operation, Flags, Alpha, Format; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr source);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [DllImport("gdi32.dll")] static extern int SelectClipRgn(IntPtr dc, IntPtr region);
        [DllImport("gdi32.dll")] static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint operation);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, Blend blend);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, IntPtr position, IntPtr size, IntPtr source, ref Point offset, uint color, ref Blend blend, uint flags);
        [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)] static extern bool InitializeLayer(IntPtr window, IntPtr destination, ref Point position, ref LayerSize size, IntPtr source, ref Point offset, uint color, ref Blend blend, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint extendedStyle, string type, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool PeekMessage(out System.Windows.Interop.MSG message, IntPtr window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref System.Windows.Interop.MSG message);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref System.Windows.Interop.MSG message);

        readonly object gate = new object();
        IntPtr window;
        readonly IntPtr dc, dib, previous, pixels;
        readonly IntPtr spriteDc, spriteDib, spritePrevious, spritePixels;
        readonly IntPtr rotatedDc, rotatedDib, rotatedPrevious, rotatedPixels;
        readonly IntPtr imageDc, imageDib, imagePrevious, imagePixels;
        readonly int width, height, spriteWidth, spriteHeight;
        readonly int left, top;
        readonly double dpi, tilt;
        readonly Rect origin;
        readonly bool fullscreen;
        Gdi.Rectangle dirty;
        readonly Func<double> seconds;
        readonly Gdi.Bitmap image, sprite, rotated;
        Gdi.Bitmap clip;
        readonly Gdi.Graphics small, rotation;
        readonly GdiImage.ImageAttributes fading = new GdiImage.ImageAttributes();
        readonly GdiImage.ColorMatrix alpha = new GdiImage.ColorMatrix();
        readonly List<double> intervals = new List<double>();
        Thread worker;
        volatile bool stopped;
        bool released, workerDone, initialized;
        static int active;
        volatile Exception failure;
        System.Windows.Point samplePoint;
        Rect target;
        double targetTime, velocityX, velocityY, lastFrame = -1;
        long firstPresentationTicks;
        double drawingMs, updateMs, waitingMs; int samples;
        public Exception Failure { get { return failure; } }
        internal static int ActiveCount { get { return Volatile.Read(ref active); } }
        internal System.Windows.Point SamplePoint { get { lock (gate) return samplePoint; } }
        public long FirstPresentationTicks { get { lock (gate) return firstPresentationTicks; } }
        public double[] Intervals { get { lock (gate) return intervals.ToArray(); } }
        public double[] Timings { get { lock (gate) return new[] { drawingMs / Math.Max(1, samples), updateMs / Math.Max(1, samples), waitingMs / Math.Max(1, samples) }; } }

        public CaptureFlightRenderer(IntPtr window, int width, int height, double dpi, Rect origin, Rect target, double tilt, BitmapSource image, BitmapSource clip, Func<double> seconds)
        {
            try
            {
                WindowsNative.Rectangle bounds; WindowsNative.GetWindowRect(window, out bounds);
                left = bounds.Left; top = bounds.Top;
                this.width = width; this.height = height; this.dpi = dpi;
                this.origin = origin; this.target = target; this.tilt = tilt; this.seconds = seconds;
                fullscreen = Math.Abs(origin.Width * dpi - width) < 1 && Math.Abs(origin.Height * dpi - height) < 1;
                dirty = new Gdi.Rectangle(0, 0, width, height);
                imageDib = Surface(image.PixelWidth, image.PixelHeight, out imagePixels, out imageDc, out imagePrevious);
                var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
                converted.CopyPixels(Int32Rect.Empty, imagePixels, image.PixelWidth * image.PixelHeight * 4, image.PixelWidth * 4);
                this.image = new Gdi.Bitmap(image.PixelWidth, image.PixelHeight, image.PixelWidth * 4, GdiImage.PixelFormat.Format32bppPArgb, imagePixels);
                this.clip = Bitmap(clip);
                spriteWidth = (int)Math.Ceiling(image.PixelWidth * 1.5); spriteHeight = (int)Math.Ceiling(image.PixelHeight * 1.5);
                spriteDib = Surface(spriteWidth + 128, spriteHeight + 128, out spritePixels, out spriteDc, out spritePrevious);
                sprite = new Gdi.Bitmap(spriteWidth + 128, spriteHeight + 128, (spriteWidth + 128) * 4, GdiImage.PixelFormat.Format32bppPArgb, spritePixels);
                small = Gdi.Graphics.FromImage(sprite); small.SmoothingMode = Gdi2D.SmoothingMode.AntiAlias;
                small.InterpolationMode = Gdi2D.InterpolationMode.Bilinear; small.PixelOffsetMode = Gdi2D.PixelOffsetMode.Half;
                rotatedDib = Surface(spriteWidth + 128, spriteHeight + 128, out rotatedPixels, out rotatedDc, out rotatedPrevious);
                rotated = new Gdi.Bitmap(spriteWidth + 128, spriteHeight + 128, (spriteWidth + 128) * 4, GdiImage.PixelFormat.Format32bppPArgb, rotatedPixels);
                rotation = Gdi.Graphics.FromImage(rotated); rotation.InterpolationMode = Gdi2D.InterpolationMode.Bilinear;
                rotation.PixelOffsetMode = Gdi2D.PixelOffsetMode.Half; rotation.CompositingQuality = Gdi2D.CompositingQuality.HighSpeed;
                rotation.CompositingMode = Gdi2D.CompositingMode.SourceCopy;
                dib = Surface(width, height, out pixels, out dc, out previous);
                initialized = true; Interlocked.Increment(ref active);
            }
            catch { Release(); throw; }
        }

        static IntPtr Surface(int width, int height, out IntPtr pixels, out IntPtr dc, out IntPtr previous)
        {
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            dc = CreateCompatibleDC(IntPtr.Zero); IntPtr bitmap = CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (dc != IntPtr.Zero) DeleteDC(dc);
                pixels = dc = previous = IntPtr.Zero; throw new System.ComponentModel.Win32Exception();
            }
            previous = SelectObject(dc, bitmap); return bitmap;
        }

        static Gdi.Bitmap Bitmap(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var bytes = new byte[stride * converted.PixelHeight]; converted.CopyPixels(bytes, stride, 0);
            var result = new Gdi.Bitmap(converted.PixelWidth, converted.PixelHeight, GdiImage.PixelFormat.Format32bppPArgb);
            var bits = result.LockBits(new Gdi.Rectangle(0, 0, result.Width, result.Height), GdiImage.ImageLockMode.WriteOnly, GdiImage.PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(bytes, 0, bits.Scan0, bytes.Length); } finally { result.UnlockBits(bits); }
            return result;
        }

        public void UpdateTarget(Rect value, double now)
        {
            lock (gate)
            {
                double elapsed = now - targetTime;
                velocityX = elapsed > 0 ? (value.X - target.X) / elapsed : 0;
                velocityY = elapsed > 0 ? (value.Y - target.Y) / elapsed : 0;
                target = value; targetTime = now;
            }
        }

        public void UpdateClip(BitmapSource value)
        {
            var replacement = Bitmap(value);
            lock (gate)
            {
                if (stopped || released) { replacement.Dispose(); return; }
                clip.Dispose(); clip = replacement;
            }
        }

        public void Start()
        {
            if (worker != null || stopped) return;
            worker = new Thread(Run) { IsBackground = true, Name = "PicRope capture flight" }; worker.Start();
        }

        void Run()
        {
            try
            {
                window = CreateWindowEx(0x080800A8, "STATIC", "PicRope.Capture.Flight", 0x80000000, left, top, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                WindowsNative.PrepareOverlay(window); WindowsNative.PassMouse(window, true);
                bool shown = false;
                while (!stopped)
                {
                    System.Windows.Interop.MSG message;
                    while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
                    double now = seconds();
                    long began = Stopwatch.GetTimestamp();
                    lock (gate) Draw(now);
                    long drawn = Stopwatch.GetTimestamp();
                    var offset = new Point();
                    var blend = new Blend { Alpha = (byte)(255 * Math.Max(0, 1 - Math.Max(0, now - 0.65) / 0.16)), Format = 1 };
                    var position = new Point { X = left, Y = top }; var size = new LayerSize { Width = width, Height = height };
                    bool updatedLayer = shown ? UpdateLayeredWindow(window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, dc, ref offset, 0, ref blend, 2)
                        : InitializeLayer(window, IntPtr.Zero, ref position, ref size, dc, ref offset, 0, ref blend, 2);
                    if (!updatedLayer)
                    { if (stopped) break; throw new System.ComponentModel.Win32Exception(); }
                    if (!shown) { ShowWindow(window, 4); WindowsNative.SetWindowPos(window, new IntPtr(-1), left, top, width, height, 0x50); shown = true; }
                    long updated = Stopwatch.GetTimestamp();
                    if (DwmFlush() != 0) Thread.Sleep(16);
                    double presented = seconds();
                    lock (gate)
                    {
                        drawingMs += (drawn - began) * 1000.0 / Stopwatch.Frequency;
                        updateMs += (updated - drawn) * 1000.0 / Stopwatch.Frequency;
                        waitingMs += (Stopwatch.GetTimestamp() - updated) * 1000.0 / Stopwatch.Frequency; samples++;
                        if (firstPresentationTicks == 0) firstPresentationTicks = Stopwatch.GetTimestamp();
                        if (now < 0.65)
                        {
                            if (lastFrame >= 0) intervals.Add((presented - lastFrame) * 1000);
                            lastFrame = presented;
                        }
                    }
                    if (now >= 0.81) break;
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (window != IntPtr.Zero) { DestroyWindow(window); window = IntPtr.Zero; }
                lock (gate) { workerDone = true; if (stopped || failure != null) Release(); }
            }
        }

        void Draw(double now)
        {
            double progress = Math.Min(1, now / 0.65);
            double ease = progress * progress * progress * (10 + progress * (-15 + 6 * progress));
            // 全屏截图先缩成卡片，再沿原有轨迹抵达，缩短大面积像素参与缩放的时间。
            double sizeEase = fullscreen ? 1 - Math.Pow(1 - progress, 3) : ease;
            double ahead = Math.Max(0, Math.Min(1.0 / 30, now - targetTime));
            double w = origin.Width + (target.Width - origin.Width) * sizeEase, h = origin.Height + (target.Height - origin.Height) * sizeEase;
            double x = origin.X + origin.Width / 2 + (target.X + velocityX * ahead + target.Width / 2 - origin.X - origin.Width / 2) * ease - w / 2;
            double y = origin.Y + origin.Height / 2 + (target.Y + velocityY * ahead + target.Height / 2 - origin.Y - origin.Height / 2) * ease - h / 2 - 30 * Math.Sin(Math.PI * ease);
            double chrome = Math.Max(0, Math.Min(1, (ease - 0.3) / 0.6)); chrome = chrome * chrome * (3 - 2 * chrome);
            float sx = (float)(spriteWidth / w), sy = (float)(spriteHeight / h);
            small.ResetTransform(); small.ResetClip(); small.Clear(Gdi.Color.Transparent);
            using (var transform = new Gdi2D.Matrix(sx, 0, 0, sy, 64, 64)) small.Transform = transform;
            if (chrome > 0)
            {
                using (var shadow = new Gdi.SolidBrush(Gdi.Color.FromArgb((int)(10 * chrome), 0, 0, 0)))
                    for (int i = 4; i >= 0; i--)
                        using (var path = Rounded(3 - i * 3, 3 - i * 3, w + i * 6, h + i * 6, 12 * ease + i * 3)) small.FillPath(shadow, path);
                using (var path = Rounded(0, 0, w, h, 12 * ease))
                using (var paper = new Gdi.SolidBrush(Gdi.Color.FromArgb((int)(245 * chrome), 255, 255, 255)))
                using (var edge = new Gdi.Pen(Gdi.Color.FromArgb(220, 225, 233), (float)chrome) { Alignment = Gdi2D.PenAlignment.Inset })
                { small.FillPath(paper, path); small.DrawPath(edge, path); }
            }
            double margin = 6 * ease + chrome;
            int innerX = 64 + (int)Math.Round(margin * sx), innerY = 64 + (int)Math.Round(margin * sy);
            int innerWidth = Math.Max(1, spriteWidth - 2 * (innerX - 64)), innerHeight = Math.Max(1, spriteHeight - 2 * (innerY - 64));
            small.Flush(Gdi2D.FlushIntention.Sync);
            IntPtr rounded = CreateRoundRectRgn(innerX, innerY, innerX + innerWidth + 1, innerY + innerHeight + 1,
                Math.Max(1, (int)Math.Round(14 * ease * sx)), Math.Max(1, (int)Math.Round(14 * ease * sy)));
            SelectClipRgn(spriteDc, rounded);
            var blend = new Blend { Alpha = 255, Format = 1 };
            try
            {
                if (!AlphaBlend(spriteDc, innerX, innerY, innerWidth, innerHeight, imageDc, 0, 0, image.Width, image.Height, blend))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally { SelectClipRgn(spriteDc, IntPtr.Zero); DeleteObject(rounded); }
            if (chrome > 0)
            {
                alpha.Matrix33 = (float)chrome; fading.SetColorMatrix(alpha);
                var bounds = new Gdi.Rectangle((int)(w / 2 - 8), -20, 16, 30);
                small.DrawImage(clip, bounds, 0, 0, clip.Width, clip.Height, Gdi.GraphicsUnit.Pixel, fading);
            }
            small.Flush(Gdi2D.FlushIntention.Sync);
            double angle = tilt * ease * Math.PI / 180, cosine = Math.Cos(angle), sine = Math.Sin(angle);
            float a = (float)cosine, b = (float)(sine * sy / sx), c = (float)(-sine * sx / sy);
            float pivotX = 64 + spriteWidth / 2f, pivotY = 64 - 12 * sy;
            rotation.ResetTransform(); rotation.Clear(Gdi.Color.Transparent);
            using (var transform = new Gdi2D.Matrix(a, b, c, a, pivotX - a * pivotX - c * pivotY, pivotY - b * pivotX - a * pivotY)) rotation.Transform = transform;
            rotation.DrawImage(sprite, new Gdi.Rectangle(0, 0, sprite.Width, sprite.Height), 0, 0, sprite.Width, sprite.Height, Gdi.GraphicsUnit.Pixel);
            rotation.Flush(Gdi2D.FlushIntention.Sync);
            PatBlt(dc, dirty.X, dirty.Y, dirty.Width, dirty.Height, 0x42);
            int padX = Math.Min(64, (int)Math.Ceiling((chrome > 0 ? 20 * sx : 0) + Math.Abs(sine) * (spriteHeight + 12 * sy) + 2));
            int padY = Math.Min(64, (int)Math.Ceiling((chrome > 0 ? 20 * sy : 0) + Math.Abs(sine) * spriteWidth / 2 + 2));
            var destination = new Gdi.Rectangle((int)Math.Round((x - padX / sx) * dpi), (int)Math.Round((y - padY / sy) * dpi),
                (int)Math.Round((w + 2 * padX / sx) * dpi), (int)Math.Round((h + 2 * padY / sy) * dpi));
            if (!AlphaBlend(dc, destination.X, destination.Y, destination.Width, destination.Height, rotatedDc,
                64 - padX, 64 - padY, spriteWidth + 2 * padX, spriteHeight + 2 * padY, blend)) throw new System.ComponentModel.Win32Exception();
            dirty = Gdi.Rectangle.Intersect(destination, new Gdi.Rectangle(0, 0, width, height));
            double sampleX = w * 0.8 - w / 2, sampleY = h * 0.65 + 12;
            samplePoint = new System.Windows.Point(x + w / 2 + cosine * sampleX - sine * sampleY, y - 12 + sine * sampleX + cosine * sampleY);
        }

        static Gdi2D.GraphicsPath Rounded(double x, double y, double width, double height, double radius)
        {
            var path = new Gdi2D.GraphicsPath();
            float d = (float)Math.Min(radius * 2, Math.Min(width, height));
            float left = (float)x, top = (float)y, w = (float)width, h = (float)height;
            if (d < 0.1) path.AddRectangle(new Gdi.RectangleF(left, top, w, h));
            else
            {
                path.AddArc(left, top, d, d, 180, 90); path.AddArc(left + w - d, top, d, d, 270, 90);
                path.AddArc(left + w - d, top + h - d, d, d, 0, 90); path.AddArc(left, top + h - d, d, d, 90, 90); path.CloseFigure();
            }
            return path;
        }

        public BitmapSource Snapshot()
        {
            lock (gate)
            {
                if (released) return null;
                Draw(seconds());
                var bitmap = BitmapSource.Create(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, pixels, width * height * 4, width * 4);
                bitmap.Freeze(); return bitmap;
            }
        }

        void Release()
        {
            lock (gate)
            {
                if (released) return; released = true;
                if (small != null) small.Dispose(); if (rotation != null) rotation.Dispose();
                if (sprite != null) sprite.Dispose(); if (rotated != null) rotated.Dispose();
                if (image != null) image.Dispose(); if (clip != null) clip.Dispose(); fading.Dispose();
                if (dc != IntPtr.Zero) { SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc); }
                if (spriteDc != IntPtr.Zero) { SelectObject(spriteDc, spritePrevious); DeleteObject(spriteDib); DeleteDC(spriteDc); }
                if (rotatedDc != IntPtr.Zero) { SelectObject(rotatedDc, rotatedPrevious); DeleteObject(rotatedDib); DeleteDC(rotatedDc); }
                if (imageDc != IntPtr.Zero) { SelectObject(imageDc, imagePrevious); DeleteObject(imageDib); DeleteDC(imageDc); }
                if (initialized) Interlocked.Decrement(ref active);
            }
        }

        public void Dispose() { lock (gate) { stopped = true; if (worker == null || workerDone) Release(); } }
    }
}
