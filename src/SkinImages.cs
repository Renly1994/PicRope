using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    internal static class SkinImages
    {
        public static BitmapSource Crop(string name, Int32Rect bounds)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
                double scale = image.PixelWidth / 1254.0;
                int left = (int)Math.Round(bounds.X * scale), top = (int)Math.Round(bounds.Y * scale);
                var crop = new CroppedBitmap(image, new Int32Rect(left, top,
                    (int)Math.Round((bounds.X + bounds.Width) * scale) - left,
                    (int)Math.Round((bounds.Y + bounds.Height) * scale) - top));
                var detached = new WriteableBitmap(crop); detached.Freeze(); return detached;
            }
        }
    }
}
