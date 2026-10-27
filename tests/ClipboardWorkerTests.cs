using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenLine.Tests
{
    internal static class ClipboardWorkerTests
    {
        [STAThread]
        static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("剪贴板后台读取验证通过。"); }
                catch (Exception error) { Console.WriteLine("后台读取验证失败：" + error.Message); Environment.ExitCode = 1; }
                finally { app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Run()
        {
            int uiThread = Thread.CurrentThread.ManagedThreadId;
            using (var gate = new ManualResetEvent(false))
            {
                var clock = Stopwatch.StartNew();
                Task<bool> operation = WindowsClipboard.ReadOnSta(delegate
                {
                    bool separateSta = Thread.CurrentThread.ManagedThreadId != uiThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                    gate.WaitOne(2000); return separateSta;
                });
                if (clock.ElapsedMilliseconds > 500) throw new Exception("缓慢的剪贴板读取阻塞界面线程");
                await Task.Delay(120);
                if (operation.IsCompleted) throw new Exception("测试没有模拟缓慢的剪贴板读取");
                Task<int> following = WindowsClipboard.ReadOnSta(delegate { return 42; });
                await Task.Delay(100);
                if (following.IsCompleted) throw new Exception("读取被并发执行");
                gate.Set();
                if (!await operation) throw new Exception("读取没有在独立的 STA 线程上执行");
                if (await following != 42) throw new Exception("等待中的读取没有完成");
            }
            bool errorReceived = false;
            try { await WindowsClipboard.ReadOnSta<int>(delegate { throw new InvalidOperationException(); }); }
            catch (InvalidOperationException) { errorReceived = true; }
            if (!errorReceived) throw new Exception("读取异常没有返回调用方");
            uint current = WindowsNative.GetClipboardSequenceNumber();
            if (await WindowsClipboard.ReadText(current + 1) != null || await CapturePipeline.ReadClipboardImage(current + 1) != null)
                throw new Exception("过期的剪贴板内容不应被读取");
        }
    }
}
