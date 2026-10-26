using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace ScreenLine
{
    internal sealed class ScreenshotSelection
    {
        public readonly Rectangle Bounds;

        public ScreenshotSelection(Rectangle bounds)
        {
            Bounds = bounds;
        }
    }

    internal static class ScreenshotCapture
    {
        public static Rectangle ToPhysicalBounds(Rectangle screen, double scale, Rect selection)
        {
            if (scale <= 0) scale = 1;
            double limitWidth = screen.Width / scale;
            double limitHeight = screen.Height / scale;
            double left = Math.Max(0, Math.Min(limitWidth, selection.Left));
            double top = Math.Max(0, Math.Min(limitHeight, selection.Top));
            double right = Math.Max(left, Math.Min(limitWidth, selection.Right));
            double bottom = Math.Max(top, Math.Min(limitHeight, selection.Bottom));
            int x = screen.Left + (int)Math.Round(left * scale);
            int y = screen.Top + (int)Math.Round(top * scale);
            int rightEdge = screen.Left + (int)Math.Round(right * scale);
            int bottomEdge = screen.Top + (int)Math.Round(bottom * scale);
            return new Rectangle(x, y, Math.Max(1, rightEdge - x), Math.Max(1, bottomEdge - y));
        }

        public static byte[] Capture(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException("bounds");
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        public static string CreatePath(StateStore store)
        {
            Directory.CreateDirectory(store.Inbox);
            return Path.Combine(store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
        }
    }
}
