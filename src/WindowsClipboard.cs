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
