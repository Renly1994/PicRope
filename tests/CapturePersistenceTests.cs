using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLine.Tests
{
    internal static class CapturePersistenceTests
    {
        static int failures;
        static int ownerThread;

        static int Main()
        {
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            Run("stale pending write preserves folder capture", StaleWrite);
            Run("failed write leaves no receipt or partial file", FailedWrite);
            Run("folder during current write publishes exactly once", FolderDuringWrite);
            Run("current write persists and publishes once", CurrentWrite);
            Run("invalidated pending write removes abandoned file", InvalidatedWrite);
            Console.WriteLine("Capture persistence: {0} passed, {1} failed", 5 - failures, failures);
            return failures == 0 ? 0 : 1;
        }

        static void Run(string name, Func<Task> test)
        {
            try
            {
                using (var context = new CallingThreadContext()) context.Run(test);
                Console.WriteLine("PASS: " + name);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL: " + name + " - " + error.Message); }
        }

        static async Task StaleWrite()
        {
            using (var fixture = new PersistenceFixture())
            {
                Task<bool> pending = fixture.Start();
                Check(!pending.IsCompleted, "write must remain pending");
                fixture.Sequence++;
                fixture.FolderArrives();
                fixture.CompleteWrite();
                if (await pending) fixture.Published++;
                Check(fixture.Published == 1, "stale clipboard receipt suppressed the only folder capture");
                Check(!File.Exists(fixture.Path), "stale clipboard file must be removed");
            }
        }

        static async Task FailedWrite()
        {
            using (var fixture = new PersistenceFixture())
            {
                Task<bool> pending = fixture.Start();
                Check(!pending.IsCompleted, "write must remain pending");
                fixture.FailWrite();
                bool failed = false;
                try { await pending; }
                catch (IOException) { failed = true; }
                Check(failed, "write failure must propagate");
                fixture.FolderArrives();
                Check(fixture.Published == 1, "failed write left a receipt that suppressed folder capture");
                Check(!File.Exists(fixture.Path), "failed write must remove its partial file");
            }
        }

        static async Task FolderDuringWrite()
        {
            using (var fixture = new PersistenceFixture())
            {
                Task<bool> pending = fixture.Start();
                Check(!pending.IsCompleted, "write must remain pending");
                fixture.FolderArrives();
                Check(fixture.Published == 1, "folder capture must publish while clipboard persistence is pending");
                fixture.CompleteWrite();
                Check(!await pending, "clipboard copy must pair with the published folder capture");
                Check(fixture.Published == 1, "capture must publish exactly once");
                Check(!File.Exists(fixture.Path), "duplicate clipboard file must be removed");
            }
        }

        static async Task CurrentWrite()
        {
            using (var fixture = new PersistenceFixture())
            {
                Task<bool> pending = fixture.Start();
                fixture.CompleteWrite();
                if (await pending) fixture.Published++;
                Check(fixture.Published == 1 && File.Exists(fixture.Path), "current clipboard capture must persist and publish");
                Check(File.ReadAllBytes(fixture.Path).Length == 3, "persisted bytes must be retained");
                fixture.FolderArrives();
                Check(fixture.Published == 1, "later folder notification must be paired");
            }
        }

        static async Task InvalidatedWrite()
        {
            using (var fixture = new PersistenceFixture())
            {
                Task<bool> pending = fixture.Start();
                fixture.Enabled = false;
                fixture.CompleteWrite();
                Check(!await pending, "disabled or disposed capture must not publish");
                Check(!File.Exists(fixture.Path), "invalidated clipboard file must be removed");
                fixture.FolderArrives();
                Check(fixture.Published == 1, "invalidated write must leave no receipt");
            }
        }

        static void Check(bool value, string message) { if (!value) throw new Exception(message); }

        sealed class PersistenceFixture : IDisposable
        {
            public readonly CaptureReceipts Receipts = new CaptureReceipts();
            public readonly DateTime Time = DateTime.UtcNow;
            public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "picrope-persistence-" + Guid.NewGuid().ToString("N") + ".png");
            readonly TaskCompletionSource<bool> write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public uint Sequence = 10;
            public bool Enabled = true;
            public int Published;

            public Task<bool> Start()
            {
                uint expected = Sequence;
                return CapturePipeline.PersistClipboard(Path, delegate { return write.Task; }, delegate
                {
                    Check(Thread.CurrentThread.ManagedThreadId == ownerThread, "receipt decision left the calling thread");
                    return Enabled && Sequence == expected;
                }, Receipts, "A");
            }

            public void FolderArrives() { if (Receipts.Accept("A", CaptureSource.Folder, Time)) Published++; }
            public void CompleteWrite() { File.WriteAllBytes(Path, new byte[] { 1, 2, 3 }); write.SetResult(true); }
            public void FailWrite() { File.WriteAllBytes(Path, new byte[] { 1 }); write.SetException(new IOException("synthetic write failure")); }
            public void Dispose() { File.Delete(Path); }
        }

        sealed class CallingThreadContext : SynchronizationContext, IDisposable
        {
            readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
            public override void Post(SendOrPostCallback callback, object state) { work.Add(delegate { callback(state); }); }
            public void Run(Func<Task> test)
            {
                SynchronizationContext previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    Task task = test();
                    while (!task.IsCompleted)
                    {
                        Action continuation;
                        if (!work.TryTake(out continuation, 5000)) throw new TimeoutException("synthetic persistence test stalled");
                        continuation();
                    }
                    task.GetAwaiter().GetResult();
                }
                finally { SetSynchronizationContext(previous); }
            }
            public void Dispose() { work.Dispose(); }
        }
    }
}
