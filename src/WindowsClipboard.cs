using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenLine
{
    internal static class WindowsClipboard
    {
        static readonly SemaphoreSlim readGate = new SemaphoreSlim(1, 1);

        internal static async Task<T> ReadOnSta<T>(Func<T> read)
        {
            await readGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                var worker = new Thread(delegate()
                {
                    try { completion.SetResult(read()); }
                    catch (Exception error) { completion.SetException(error); }
                });
                worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
                return await completion.Task.ConfigureAwait(false);
            }
            finally { readGate.Release(); }
        }

        internal static byte[] ReadEncodedImage(IDataObject data, out string extension)
        {
            extension = null;
            object raw = null;
            foreach (var format in ClipboardRules.EncodedFormats)
            {
                if (!data.GetDataPresent(format.Key, false)) continue;
                object candidate = data.GetData(format.Key, false);
                if (!(candidate is byte[]) && !(candidate is Stream)) continue;
                raw = candidate; extension = format.Value; break;
            }
            var bytes = raw as byte[];
            if (bytes != null) return bytes;
            var stream = raw as Stream;
            if (stream == null) return null;
            return ReadImageBytes(stream);
        }

        internal static byte[] ImageBytes(object value)
        {
            var bytes = value as byte[];
            if (bytes != null)
            {
                if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("图片数据过大");
                return bytes;
            }
            var stream = value as Stream;
            return stream == null ? null : ReadImageBytes(stream);
        }

        // CF_DIB/CF_DIBV5 omit the 14-byte BMP file header. Validate offsets before
        // supplying that header to WIC; never trust dimensions/palette sizes blindly.
        internal static byte[] DibToBitmap(byte[] dib)
        {
            if (dib.Length < 40 || dib.Length > 64 * 1024 * 1024 - 14) throw new InvalidDataException("DIB 大小无效");
            uint header = BitConverter.ToUInt32(dib, 0);
            if (header != 40 && header != 52 && header != 56 && header != 108 && header != 124) throw new NotSupportedException("DIB 头不支持");
            if (header > dib.Length) throw new InvalidDataException("DIB 头不完整");
            int width = BitConverter.ToInt32(dib, 4), height = BitConverter.ToInt32(dib, 8);
            ushort bits = BitConverter.ToUInt16(dib, 14);
            uint compression = BitConverter.ToUInt32(dib, 16), colors = BitConverter.ToUInt32(dib, 32);
            long rows = Math.Abs((long)height);
            if (width <= 0 || rows == 0 || width * rows > 80000000 || BitConverter.ToUInt16(dib, 12) != 1)
                throw new InvalidDataException("DIB 尺寸无效");
            if ((bits != 1 && bits != 4 && bits != 8 && bits != 16 && bits != 24 && bits != 32)
                || (compression != 0 && compression != 3 && compression != 6)
                || (compression != 0 && bits != 16 && bits != 32)) throw new NotSupportedException("DIB 编码不支持");
            if (bits <= 8 && colors > (1u << bits)) throw new InvalidDataException("DIB 调色板无效");
            long palette = colors != 0 ? colors : bits <= 8 ? 1L << bits : 0;
            long offset = header + palette * 4 + (header == 40 && compression != 0 ? (compression == 6 ? 16 : 12) : 0);
            long stride = ((long)width * bits + 31) / 32 * 4;
            // Packed DIBV5 may put an embedded profile before the pixels.
            if (header == 124)
            {
                uint profile = BitConverter.ToUInt32(dib, 112), size = BitConverter.ToUInt32(dib, 116);
                if (size != 0)
                {
                    if (profile < offset || (long)profile + size > dib.Length) throw new InvalidDataException("DIB 色彩配置无效");
                    if (profile == offset) offset += size;
                }
            }
            if (offset + stride * rows > dib.Length) throw new InvalidDataException("DIB 像素不完整");
            byte[] bmp = new byte[dib.Length + 14];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            Buffer.BlockCopy(BitConverter.GetBytes(bmp.Length), 0, bmp, 2, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)offset + 14), 0, bmp, 10, 4);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
            return bmp;
        }

        internal static byte[] ReadImageBytes(Stream stream)
        {
            const int limit = 64 * 1024 * 1024;
            if (stream.CanSeek)
            {
                long length = stream.Length;
                if (length > limit) throw new InvalidDataException("图片数据过大");
                stream.Position = 0; var result = new byte[(int)length]; int offset = 0;
                while (offset < result.Length)
                {
                    int count = stream.Read(result, offset, result.Length - offset);
                    if (count == 0) throw new EndOfStreamException("图片数据尚未完整读取");
                    offset += count;
                }
                return result;
            }
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + count > limit) throw new InvalidDataException("图片数据过大");
                    memory.Write(buffer, 0, count);
                }
                return memory.ToArray();
            }
        }

        public static Task<string> ReadText(uint sequence)
        {
            return ReadOnSta(delegate
            {
                ExternalException last = null;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (WindowsNative.GetClipboardSequenceNumber() != sequence) return null;
                    try
                    {
                        var data = Clipboard.GetDataObject();
                        if (data == null || data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap)
                            || data.GetDataPresent("PNG") || data.GetDataPresent("image/png")) return null;
                        if (data.GetDataPresent(DataFormats.UnicodeText)) return data.GetData(DataFormats.UnicodeText) as string;
                        return data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
                    }
                    catch (ExternalException error) { last = error; }
                    Thread.Sleep(60);
                }
                throw last;
            });
        }

        public static async Task Write(object data)
        {
            // 使用 WinForms 的写入路径，避开 WPF 刷新剪贴板失败的问题，并保留原有数据格式。
            ExternalException last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try { System.Windows.Forms.Clipboard.SetDataObject(data, true, 1, 0); return; }
                catch (ExternalException error) { last = error; }
                await Task.Delay(60);
            }
            throw last;
        }
    }
}
