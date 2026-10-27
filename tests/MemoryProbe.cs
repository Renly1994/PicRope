using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace ScreenLine.Tests
{
    internal static class MemoryProbe
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static object controller;
        static object Get(object value, string name)
        {
            var type = value.GetType(); var field = type.GetField(name, Flags);
            return field != null ? field.GetValue(value) : type.GetProperty(name, Flags).GetValue(value, null);
        }
        static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, Flags).Invoke(value, args); }
        static object Sample()
        {
            using (var process = Process.GetCurrentProcess())
                return new { workingSetMB = Math.Round(process.WorkingSet64 / 1048576.0, 1), privateMB = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1), managedMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1), peakWorkingSetMB = Math.Round(process.PeakWorkingSet64 / 1048576.0, 1) };
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Contains("software")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async delegate
            {
                try
                {
                    var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
                    assembly.GetType("ScreenLine.LightTheme").GetMethod("Install").Invoke(null, new object[] { app });
                    string profile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "performance", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(profile);
                    int count = args.Length > 2 ? int.Parse(args[2]) : 27;
                    var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
                    var paths = Directory.GetFiles(args[1]).Where(path => extensions.Contains(Path.GetExtension(path).ToLowerInvariant())).OrderBy(path => path).Take(count).ToArray();
                    File.WriteAllText(Path.Combine(profile, "state.json"), json.Serialize(new { Images = paths.Take(22), Collection = paths, Theme = "porcelain", CollectClipboard = true, CollectTextClipboard = true, AutoHang = false }));
                    object before = Sample();
                    var store = Activator.CreateInstance(assembly.GetType("ScreenLine.StateStore"), Flags, null, new object[] { profile }, null);
                    controller = Activator.CreateInstance(assembly.GetType("ScreenLine.DesktopController"), Flags, null, new object[] { store, Path.Combine(profile, "watched"), false }, null);
                    object constructed = Sample();
                    await (Task)Call(controller, "Start");
                    var shelf = (Window)Get(controller, "Shelf"); Call(shelf, "Reveal", false, false, 0.0); await Task.Delay(600);
                    object startup = Sample();
                    double hiddenCpu = await Cpu();
                    shelf.GetType().GetField("nextGust", Flags).SetValue(shelf, DateTime.MaxValue);
                    Call(shelf, "Reveal", true, true, 60.0); await Task.Delay(10000);
                    double openCpu = await Cpu();
                    bool idleRendering = (bool)Get(shelf, "rendering");
                    Call(shelf, "Reveal", false, false, 0.0); await Task.Delay(400);
                    object afterFive = null;
                    for (int i = 0; i < 20; i++)
                    {
                        Call(controller, "ShowBasket"); await Task.Delay(100);
                        ((Window)Get(controller, "Basket")).Close();
                        Call(shelf, "Reveal", false, false, 0.0); await Task.Delay(250);
                        if (i == 4) afterFive = Sample();
                    }
                    object afterCycles = Sample();
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(200);
                    var report = new { version = FileVersionInfo.GetVersionInfo(args[0]).ProductVersion, images = paths.Length,
                        software = System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly,
                        before, constructed, startup, hiddenCpuPercent = hiddenCpu, openIdleCpuPercent = openCpu, idleRendering,
                        afterFiveBasketCycles = afterFive, afterTwentyBasketCycles = afterCycles, afterDiagnosticGC = Sample() };
                    string reportText = json.Serialize(report);
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "performance", (args.Length > 3 ? args[3] : report.version) + ".json"), reportText);
                    Console.WriteLine(reportText);
                }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                finally { if (controller != null) ((IDisposable)controller).Dispose(); app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task<double> Cpu()
        {
            using (var process = Process.GetCurrentProcess())
            {
                TimeSpan before = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
                await Task.Delay(2000); process.Refresh();
                return Math.Round((process.TotalProcessorTime - before).TotalMilliseconds / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 2);
            }
        }
    }
}
