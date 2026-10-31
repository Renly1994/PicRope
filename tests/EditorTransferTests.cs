using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class EditorTransferTests
    {
        static int checks;
        static MethodInfo read;
        [STAThread]
        static int Main()
        {
            try
            {
                read = typeof(ImageEditFiles).GetMethod("ReadExport", BindingFlags.Public | BindingFlags.Static);
                Check(read != null, "binary PNG reader exists");
                var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 0, 0, 0, 0, 80, 140, 240, 255 }, 8);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                byte[] png;
                using (var memory = new MemoryStream()) { encoder.Save(memory); png = memory.ToArray(); }
                Check(Read(new MemoryStream(png)).SequenceEqual(png), "valid binary PNG is returned unchanged");
                Check(Read(new Chunks(png, false)).SequenceEqual(png), "non-seekable short reads preserve bytes");
                Reject(null, "missing body rejected");
                Reject(new MemoryStream(), "empty body rejected");
                Reject(new MemoryStream(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "header-only PNG rejected");
                var damaged = (byte[])png.Clone(); damaged[4] = 0;
                Reject(new MemoryStream(damaged), "invalid full PNG signature rejected");
                Reject(new Chunks(png, true), "body exceeding 64 MiB is rejected while streaming");
                Console.WriteLine("Editor transfer: {0} passed", checks);
                return 0;
            }
            catch (Exception error) { Console.WriteLine(error); return 1; }
        }
        static byte[] Read(Stream stream)
        {
            try { return (byte[])read.Invoke(null, new object[] { stream }); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        static void Reject(Stream stream, string name)
        {
            try { Read(stream); }
            catch (InvalidDataException) { Check(true, name); return; }
            catch (NotSupportedException) { Check(true, name); return; }
            catch (FileFormatException) { Check(true, name); return; }
            throw new Exception(name + ": body was accepted");
        }
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        sealed class Chunks : Stream
        {
            readonly byte[] data; readonly bool endless; long position;
            public Chunks(byte[] data, bool endless) { this.data = data; this.endless = endless; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int size = endless ? count : Math.Min(3, Math.Min(count, data.Length - (int)position));
                if (endless) Array.Clear(buffer, offset, size);
                else Array.Copy(data, (int)position, buffer, offset, size);
                position += size; return size;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
