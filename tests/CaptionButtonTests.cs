using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal sealed class SnapshotWindow
    {
        public IntPtr Handle;
        public string ClassName = "FixtureWindow";
        public WindowsNative.Rectangle Rectangle;
        public bool Visible = true, Iconic, HasRectangle = true;
        public int ExStyle, Cloaked, DwmResult;
        public int RegionKind;
        public readonly List<Rectangle> RegionRectangles = new List<Rectangle>();
    }

    internal static class Snapshot
    {
        public static readonly List<SnapshotWindow> Windows = new List<SnapshotWindow>();
        public static readonly Dictionary<string, double> Scales = new Dictionary<string, double>();
        public static readonly List<IntPtr> Enumerated = new List<IntPtr>();
        public static IntPtr Foreground, Selected;
        public static readonly IntPtr Shell = new IntPtr(7777), Overlay = new IntPtr(99);
        static readonly Dictionary<IntPtr, SnapshotWindow> Regions = new Dictionary<IntPtr, SnapshotWindow>();
        static int nextRegion = 100000;
        public static int Created, Deleted, CreateAttempts, InvalidDeletes;
        public static bool FailCreateRegion, ThrowOnGetRegion, ThrowOnPointInRegion;
        public static int LiveRegions { get { return Regions.Count; } }

        public static void Reset(params SnapshotWindow[] windows)
        {
            if (Regions.Count != 0) throw new InvalidOperationException("Previous fixture leaked GDI regions");
            Created = Deleted = CreateAttempts = InvalidDeletes = 0;
            FailCreateRegion = ThrowOnGetRegion = ThrowOnPointInRegion = false;
            Windows.Clear(); Windows.AddRange(windows); Enumerated.Clear(); Selected = IntPtr.Zero;
            Foreground = windows.Length == 0 ? IntPtr.Zero : windows[0].Handle;
        }
        static SnapshotWindow Find(IntPtr handle) { return Windows.FirstOrDefault(item => item.Handle == handle); }
        public static bool GetRectangle(IntPtr handle, out WindowsNative.Rectangle rectangle)
        {
            var item = Find(handle); rectangle = item == null ? new WindowsNative.Rectangle() : item.Rectangle;
            return item != null && item.HasRectangle;
        }
        public static int GetClass(IntPtr handle, StringBuilder text, int size)
        {
            var item = Find(handle); string value = item == null ? "" : item.ClassName;
            value = value.Substring(0, Math.Min(value.Length, Math.Max(0, size - 1))); text.Append(value); return value.Length;
        }
        public static bool Visible(IntPtr handle) { var item = Find(handle); return item != null && item.Visible; }
        public static bool Iconic(IntPtr handle) { var item = Find(handle); return item != null && item.Iconic; }
        public static int Style(IntPtr handle) { var item = Find(handle); return item == null ? 0 : item.ExStyle; }
        public static int GetCloaked(IntPtr handle, out int value)
        {
            var item = Find(handle); value = item == null ? 0 : item.Cloaked; return item == null ? -1 : item.DwmResult;
        }
        public static IntPtr CreateRegion(int left, int top, int right, int bottom)
        {
            CreateAttempts++; if (FailCreateRegion) return IntPtr.Zero;
            var handle = new IntPtr(nextRegion++); Regions.Add(handle, null); Created++; return handle;
        }
        public static int GetRegion(IntPtr window, IntPtr region)
        {
            if (!Regions.ContainsKey(region)) throw new InvalidOperationException("GetWindowRgn received invalid HRGN");
            if (ThrowOnGetRegion) throw new InvalidOperationException("injected GetWindowRgn failure");
            var item = Find(window); Regions[region] = item; return item == null ? 0 : item.RegionKind;
        }
        public static bool PointInRegion(IntPtr region, int x, int y)
        {
            if (!Regions.ContainsKey(region)) throw new InvalidOperationException("PtInRegion received invalid HRGN");
            if (ThrowOnPointInRegion) throw new InvalidOperationException("injected PtInRegion failure");
            var item = Regions[region];
            return item != null && item.RegionKind != 1 && item.RegionRectangles.Any(rect => rect.Contains(x, y));
        }
        public static bool DeleteRegion(IntPtr region)
        {
            if (!Regions.Remove(region)) { InvalidDeletes++; return false; }
            Deleted++; return true;
        }
        public static string Describe()
        {
            return string.Join(";", Windows.Select(item => string.Format(CultureInfo.InvariantCulture,
                "hwnd={0},class={1},rect={2}/{3}/{4}/{5},visible={6},iconic={7},exstyle=0x{8:x},cloaked={9},rectOK={10},regionKind={11}",
                item.Handle, item.ClassName, item.Rectangle.Left, item.Rectangle.Top, item.Rectangle.Right, item.Rectangle.Bottom,
                item.Visible, item.Iconic, item.ExStyle, item.Cloaked, item.HasRectangle, item.RegionKind)).ToArray());
        }
    }

    internal static class CaptionButtonTests
    {
        static int checks, failures;
        static string scenario;
        static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        // Screen.WorkingArea normally subscribes to SystemEvents on first use. Set the isolated
        // process generation field directly, so fixture reads never create that message window.
        static Forms.Screen Screen(string name, Rectangle bounds, Rectangle work)
        {
            var type = typeof(Forms.Screen);
            var generation = type.GetField("desktopChangedCount", BindingFlags.Static | BindingFlags.NonPublic);
            if (generation == null) throw new InvalidOperationException("Expected .NET Framework Screen generation field is missing");
            generation.SetValue(null, 0);
            var screen = (Forms.Screen)FormatterServices.GetUninitializedObject(type);
            type.GetField("bounds", Instance).SetValue(screen, bounds);
            type.GetField("workingArea", Instance).SetValue(screen, work);
            type.GetField("deviceName", Instance).SetValue(screen, name);
            type.GetField("primary", Instance).SetValue(screen, true);
            type.GetField("currentDesktopChangedCount", Instance).SetValue(screen, 0);
            return screen;
        }
        static SnapshotWindow Window(int handle, Rectangle rect)
        {
            return new SnapshotWindow { Handle = new IntPtr(handle), Rectangle = new WindowsNative.Rectangle {
                Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom } };
        }
        static WindowsNative.Point Point(int x, int y) { return new WindowsNative.Point { X = x, Y = y }; }
        static void Caption(Forms.Screen screen, WindowsNative.Point point, bool expected, string label, int? selected = null, int? expectedAllocations = null)
        {
            Snapshot.Enumerated.Clear(); Snapshot.Selected = IntPtr.Zero;
            bool actual = WindowsNative.OverCaptionButtons(screen, point, Snapshot.Overlay);
            bool passed = actual == expected && (!selected.HasValue || Snapshot.Selected == new IntPtr(selected.Value))
                && Snapshot.Created == Snapshot.Deleted && Snapshot.LiveRegions == 0 && Snapshot.InvalidDeletes == 0
                && (!expectedAllocations.HasValue || Snapshot.Created == expectedAllocations.Value);
            checks++; if (!passed) failures++;
            Console.WriteLine("{0} {1:000} {2} | {3} | caption expected={4} actual={5} point={6},{7} selected={8} enumerated=[{9}] GDI={10}/{11}/live:{12}/invalid-delete:{13}",
                passed ? "PASS" : "FAIL", checks, scenario, label, expected, actual, point.X, point.Y, Snapshot.Selected,
                string.Join(",", Snapshot.Enumerated.Select(value => value.ToString()).ToArray()),
                Snapshot.Created, Snapshot.Deleted, Snapshot.LiveRegions, Snapshot.InvalidDeletes);
            if (!passed) Console.WriteLine("FIXTURE " + Snapshot.Describe());
        }
        static void Full(Forms.Screen screen, bool expected, string label)
        {
            bool actual = WindowsNative.FullScreen(screen, Snapshot.Overlay);
            checks++; if (actual != expected) failures++;
            Console.WriteLine("{0} {1:000} {2} | {3} | fullscreen expected={4} actual={5} foreground={6}",
                actual == expected ? "PASS" : "FAIL", checks, scenario, label, expected, actual, Snapshot.Foreground);
            if (actual != expected) Console.WriteLine("FIXTURE " + Snapshot.Describe());
        }

        static void Scenario(Rectangle bounds, double scale)
        {
            scenario = string.Format(CultureInfo.InvariantCulture, "origin={0},{1} scale={2}", bounds.Left, bounds.Top, scale);
            var work = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - 40);
            var screen = Screen("FIXTURE-" + scenario, bounds, work); Snapshot.Scales[screen.DeviceName] = scale;
            int border = (int)Math.Round(8 * scale);
            var maxRect = Rectangle.FromLTRB(work.Left - border, work.Top - border, work.Right + border, work.Bottom + border);
            var point = Point(work.Right - (int)Math.Round(23 * scale), work.Top + (int)Math.Round(16 * scale));
            var max = Window(10, maxRect);
            var coverRect = new Rectangle(work.Right - 400, work.Top, 400, 300);

            Snapshot.Reset(max); Caption(screen, point, true, "maximized unobstructed", 10);
            Full(screen, false, "work-area maximized excludes bottom taskbar");
            Snapshot.Reset(Window(20, coverRect), max); Caption(screen, point, false, "ordinary top window occludes maximized", 20);
            Snapshot.Reset(Window(20, new Rectangle(work.Left + 100, work.Top + 100, 400, 300)), max);
            Caption(screen, point, true, "unrelated ordinary foreground does not hide backdrop caption", 10);
            Full(screen, false, "fullscreen depends on foreground, not maximized window behind it");
            Snapshot.Reset(Window(99, coverRect), max); Caption(screen, point, true, "shelf overlay skipped", 10);
            var transparent = Window(20, coverRect); transparent.ExStyle = 0x20;
            Snapshot.Reset(transparent, max); Caption(screen, point, true, "click-through window skipped", 10);
            var cloaked = Window(20, coverRect); cloaked.Cloaked = 1;
            Snapshot.Reset(cloaked, max); Caption(screen, point, true, "cloaked window skipped", 10);
            var hidden = Window(20, coverRect); hidden.Visible = false;
            Snapshot.Reset(hidden, max); Caption(screen, point, true, "hidden window skipped", 10);
            var iconic = Window(20, coverRect); iconic.Iconic = true;
            Snapshot.Reset(iconic, max); Caption(screen, point, true, "minimized window skipped", 10);
            var invalid = Window(20, coverRect); invalid.HasRectangle = false;
            Snapshot.Reset(invalid, max); Caption(screen, point, true, "unreadable rectangle skipped", 10);
            foreach (string className in new[] { "WorkerW", "Progman", "Shell_TrayWnd" })
            {
                var shellClass = Window(20, coverRect); shellClass.ClassName = className;
                Snapshot.Reset(shellClass, max); Caption(screen, point, true, className + " skipped", 10);
            }
            Snapshot.Reset(Window(7777, coverRect), max); Caption(screen, point, true, "shell HWND skipped", 10);
            Snapshot.Reset(); Caption(screen, point, false, "no eligible window", 0);

            var full = Window(30, bounds);
            Snapshot.Reset(full); Full(screen, true, "foreground covers full monitor Bounds");
            Snapshot.Foreground = IntPtr.Zero; Full(screen, false, "no foreground");
            Snapshot.Foreground = Snapshot.Overlay; Full(screen, false, "shelf foreground ignored");
            Snapshot.Foreground = Snapshot.Shell; Full(screen, false, "shell foreground ignored");
            Snapshot.Foreground = new IntPtr(123456); Full(screen, false, "unreadable foreground rectangle");
            foreach (string className in new[] { "WorkerW", "Progman", "Shell_TrayWnd" })
            {
                full.ClassName = className; Snapshot.Reset(full); Full(screen, false, "fullscreen ignores " + className);
            }
            full.ClassName = "FixtureWindow";

            var exact = Window(10, work); Snapshot.Reset(exact);
            int insideX = (int)Math.Ceiling(work.Right - 150 * scale);
            int outsideY = (int)Math.Ceiling(work.Top + 40 * scale);
            Caption(screen, Point(insideX, point.Y), true, "150*DPI strip first integral x inside", 10);
            Caption(screen, Point(insideX - 1, point.Y), false, "150*DPI strip one pixel outside", 0);
            Caption(screen, Point(point.X, outsideY - 1), true, "40*DPI strip last integral y inside", 10);
            Caption(screen, Point(point.X, outsideY), false, "40*DPI strip first y outside", 0);
            Caption(screen, Point(work.Right - 1, point.Y), true, "right edge last pixel inside", 10);
            Caption(screen, Point(work.Right, point.Y), false, "right edge exclusive", 0);
            Caption(screen, Point(point.X, bounds.Top - 1), false, "above monitor excluded", 0);
            int allowedRight = bounds.Right + (int)Math.Round(16 * scale);
            Snapshot.Reset(Window(10, Rectangle.FromLTRB(work.Left, work.Top, allowedRight, work.Bottom)));
            Caption(screen, point, true, "16*DPI frame overshoot inclusive", 10);
            Snapshot.Reset(Window(10, Rectangle.FromLTRB(work.Left, work.Top, allowedRight + 1, work.Bottom)));
            Caption(screen, point, false, "frame overshoot one pixel too far", 10);
            foreach (string edge in new[] { "left", "top", "right", "bottom" })
            {
                var gap = Rectangle.FromLTRB(work.Left + (edge == "left" ? 1 : 0), work.Top + (edge == "top" ? 1 : 0),
                    work.Right - (edge == "right" ? 1 : 0), work.Bottom - (edge == "bottom" ? 1 : 0));
                Snapshot.Reset(Window(10, gap)); Caption(screen, point, false, "one-pixel work coverage gap at " + edge, 10);
            }

            var shaped = Window(20, coverRect); shaped.RegionKind = 2;
            shaped.RegionRectangles.Add(new Rectangle(0, (int)Math.Round(80 * scale), 400, 200));
            Snapshot.Reset(shaped, max);
            Caption(screen, point, true, "bounding-box cover but point outside simple window region", 10, 2);
            shaped.RegionRectangles.Clear(); shaped.RegionRectangles.Add(new Rectangle(0, 0, 400, 300));
            Snapshot.Reset(shaped, max);
            Caption(screen, point, false, "point inside simple region remains real obstruction", 20, 1);
            shaped.RegionKind = 1; shaped.RegionRectangles.Clear(); Snapshot.Reset(shaped, max);
            Caption(screen, point, true, "NULLREGION is successful empty region and must be skipped", 10, 2);
            shaped.RegionKind = 3;
            shaped.RegionRectangles.Add(new Rectangle(0, 0, 10, 10)); shaped.RegionRectangles.Add(new Rectangle(0, 160, 400, 140));
            Snapshot.Reset(shaped, max);
            Caption(screen, point, true, "point in complex region hole is not obstruction", 10, 2);
            shaped.RegionKind = 0; Snapshot.Reset(shaped, max);
            Caption(screen, point, false, "no region or failed region query falls back to rectangle", 20, 1);
            Snapshot.Reset(shaped, max); Snapshot.FailCreateRegion = true;
            Caption(screen, point, false, "failed region allocation falls back without invalid delete", 20, 0);
            shaped.RegionKind = 2; shaped.RegionRectangles.Clear(); shaped.RegionRectangles.Add(new Rectangle(0, 0, 400, 300));
            foreach (bool duringPoint in new[] { false, true })
            {
                Snapshot.Reset(shaped, max); Snapshot.ThrowOnGetRegion = !duringPoint; Snapshot.ThrowOnPointInRegion = duringPoint;
                bool thrown = false;
                try { WindowsNative.OverCaptionButtons(screen, point, Snapshot.Overlay); }
                catch (InvalidOperationException) { thrown = true; }
                bool released = thrown && Snapshot.Created == 1 && Snapshot.Deleted == 1 && Snapshot.LiveRegions == 0 && Snapshot.InvalidDeletes == 0;
                checks++; if (!released) failures++;
                Console.WriteLine("{0} {1:000} {2} | HRGN released on injected {3} exception | thrown={4} GDI={5}/{6}/live:{7}",
                    released ? "PASS" : "FAIL", checks, scenario, duringPoint ? "PtInRegion" : "GetWindowRgn", thrown,
                    Snapshot.Created, Snapshot.Deleted, Snapshot.LiveRegions);
            }

            var topWork = Rectangle.FromLTRB(bounds.Left, bounds.Top + 48, bounds.Right, bounds.Bottom);
            var topScreen = Screen("TOP-" + scenario, bounds, topWork); Snapshot.Scales[topScreen.DeviceName] = scale;
            var topMax = Window(10, Rectangle.FromLTRB(topWork.Left - border, topWork.Top - border, topWork.Right + border, topWork.Bottom + border));
            Snapshot.Reset(topMax);
            Caption(topScreen, Point(topWork.Right - (int)Math.Round(23 * scale), topWork.Top + (int)Math.Round(16 * scale)), true, "top-taskbar maximized caption", 10);
            Full(topScreen, false, "top-taskbar maximized still excludes full Bounds");
            var noTaskbar = Screen("NO-TASKBAR-" + scenario, bounds, bounds); Snapshot.Scales[noTaskbar.DeviceName] = scale;
            Snapshot.Reset(Window(10, bounds));
            Caption(noTaskbar, Point(bounds.Right - (int)Math.Round(23 * scale), bounds.Top + (int)Math.Round(16 * scale)), true, "equal work and Bounds caption");
            Full(noTaskbar, true, "equal work and Bounds classified fullscreen");
        }

        static void ShapedTopmostOverlay()
        {
            scenario = "shaped topmost overlay fixture";
            var bounds = new Rectangle(0, 0, 1920, 1200);
            var work = new Rectangle(0, 0, 1920, 1140);
            var screen = Screen("SHAPED-OVERLAY", bounds, work); Snapshot.Scales[screen.DeviceName] = 1.25;
            var blocker = Window(4242, Rectangle.FromLTRB(560, 0, 2160, 338));
            blocker.ClassName = "FixtureShapedTopmost"; blocker.ExStyle = unchecked((int)0x8000088);
            var maximized = Window(10, Rectangle.FromLTRB(-10, -10, 1930, 1150));
            var unrelated = Window(30, new Rectangle(560, 350, 800, 700)); unrelated.ClassName = "FixtureForeground";
            Console.WriteLine("FIXTURE bounds=0/0/1920/1200 work=0/0/1920/1140 scale=1.25 blocker=" +
                "4242,FixtureShapedTopmost,560/0/2160/338,exstyle=0x8000088. Generic clipped topmost overlay fixture.");
            foreach (int x in new[] { 1891, 1834, 1776 })
            {
                blocker.RegionKind = 0;
                Snapshot.Reset(blocker, unrelated, maximized); Snapshot.Foreground = unrelated.Handle;
                Caption(screen, Point(x, 20), false, "ordinary topmost bounding-box blocker first", 4242);
                Snapshot.Reset(maximized, blocker, unrelated); Snapshot.Foreground = unrelated.Handle;
                Caption(screen, Point(x, 20), true, "same rectangles, maximized fixture first", 10);
                // A clipped overlay can cover points in its bounding box but outside its drawing region.
                blocker.RegionKind = 2; blocker.RegionRectangles.Clear(); blocker.RegionRectangles.Add(new Rectangle(0, 80, 1600, 258));
                Snapshot.Reset(blocker, unrelated, maximized); Snapshot.Foreground = unrelated.Handle;
                Caption(screen, Point(x, 20), true, "outside drawing region ignores bounding-box blocker", 10, 2);
                blocker.RegionRectangles.Clear(); blocker.RegionRectangles.Add(new Rectangle(0, 0, 1600, 338));
                Snapshot.Reset(blocker, unrelated, maximized); Snapshot.Foreground = unrelated.Handle;
                Caption(screen, Point(x, 20), false, "same blocker with genuinely occupied region remains obstruction", 4242, 1);
            }
        }

        public static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8; CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Console.WriteLine("Pure fixture regression. No Application, Window, cursor, clipboard, or focus operations.");
            try
            {
                foreach (var origin in new[] { new System.Drawing.Point(0, 0), new System.Drawing.Point(-2560, -300), new System.Drawing.Point(1920, -1080) })
                    foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                        Scenario(new Rectangle(origin.X, origin.Y, 1920, 1080), scale);
                ShapedTopmostOverlay();
            }
            catch (Exception error) { failures++; Console.WriteLine("ERROR " + error); }
            Console.WriteLine("RESULT scenarios=13 assertions={0} passed={1} failed={2}", checks, checks - failures, failures);
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }
    }
}
