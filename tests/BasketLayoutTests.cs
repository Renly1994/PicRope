using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class BasketLayoutTests
    {
        static int checks;
        static BasketWindow basket;
        static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [STAThread]
        public static void Main(string[] arguments)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Run(arguments.Contains("--footer-only")); Console.WriteLine("图片篮子布局检查：{0} 项通过", checks); }
                catch (Exception error) { Console.WriteLine("图片篮子布局检查失败：" + error); Environment.ExitCode = 1; }
                finally { if (basket != null) basket.Close(); app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static async Task Run(bool footerOnly)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "layout-" + Guid.NewGuid().ToString("N"));
            var store = new StateStore(root); Directory.CreateDirectory(store.Inbox);
            var gallery = new Gallery(store);
            string project = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string[] sources = { "basket-preview-windows.png", "text-preview-windows.png", "overview-windows.png", "pin-preview-windows.png" };
            for (int i = 0; i < 16; i++)
            {
                string path = Path.Combine(store.Inbox, "参考图" + (i + 1) + ".png");
                File.Copy(Path.Combine(project, "docs", "assets", sources[i % sources.Length]), path);
                gallery.Collect(path, ImageContent.Read(path, false));
            }
            basket = new BasketWindow(gallery, delegate(bool value) { store.State.AutoHang = value; }, delegate { }, delegate { });
            basket.Show(); await Task.Delay(200);
            if (footerOnly)
            {
                CheckFooter(); Save("06-左下角全选", WindowsNative.MonitorScale(WindowsNative.ScreenAtPointer()));
                basket.SelectAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(60);
                Check(basket.SelectedCount == 16 && (string)basket.SelectAllButton.Content == "取消全选", "左下角全选更新选择数量和按钮文案");
                CheckFooter(); Save("07-左下角取消全选", WindowsNative.MonitorScale(WindowsNative.ScreenAtPointer()));
                basket.SelectAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(basket.SelectedCount == 0 && (string)basket.SelectAllButton.Content == "全选", "取消全选恢复未选择状态");
                return;
            }
            Save("02-默认尺寸", WindowsNative.MonitorScale(WindowsNative.ScreenAtPointer()));
            CheckLayout();
            var firstScroll = (ScrollViewer)typeof(BasketWindow).GetField("scroll", Fields).GetValue(basket);
            foreach (var item in gallery.Collected)
            {
                var choice = basket.ChoiceFor(item.Path);
                var cardBounds = choice.TransformToAncestor(firstScroll).TransformBounds(new Rect(choice.RenderSize));
                if (cardBounds.Top < firstScroll.ActualHeight && cardBounds.Bottom > 0)
                    Check(cardBounds.Top >= -0.5 && cardBounds.Bottom <= firstScroll.ActualHeight + 0.5, "默认窗口的可见卡片不被底部操作栏截断：" + cardBounds.Top + "～" + cardBounds.Bottom + " / " + firstScroll.ActualHeight);
            }
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                basket.Width = Math.Min(1040, 1920 / scale - 32);
                basket.Height = Math.Min(660, 1040 / scale - 48);
                await Task.Delay(60); basket.UpdateLayout();
                CheckLayout(); Save("03-可用尺寸-" + (int)(scale * 100), scale);
            }
            basket.Width = 720; basket.Height = 460; await Task.Delay(60); basket.UpdateLayout();
            CheckLayout(); Save("04-窄窗口", 1.25);
            basket.SelectAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60); CheckLayout(); Save("05-取消全选状态", 1.25);
            var scroll = (ScrollViewer)typeof(BasketWindow).GetField("scroll", Fields).GetValue(basket);
            scroll.ScrollToEnd(); await Task.Delay(100); basket.UpdateLayout();
            var final = basket.ChoiceFor(gallery.Collected.OrderByDescending(item => item.Written).Last().Path);
            var bounds = final.TransformToAncestor(scroll).TransformBounds(new Rect(final.RenderSize));
            Check(bounds.Top >= -0.5 && bounds.Bottom <= scroll.ActualHeight + 0.5, "滚动到底部时最后一张图片及时间完整可见");
        }

        static void CheckLayout()
        {
            basket.UpdateLayout();
            var scroll = (ScrollViewer)typeof(BasketWindow).GetField("scroll", Fields).GetValue(basket);
            var metadata = (TextBlock)typeof(BasketWindow).GetField("metadata", Fields).GetValue(basket);
            var status = (TextBlock)typeof(BasketWindow).GetField("status", Fields).GetValue(basket);
            Rect select = Bounds(basket.SelectAllButton), list = Bounds(scroll);
            CheckFooter();
            Check(!Bounds(basket.Preview).IntersectsWith(Bounds(metadata)), "预览图片与底部图片信息没有重叠");
            Check(list.Bottom <= Bounds(basket.DeleteButton).Top - 8, "滚动内容不侵入底部操作区");
            Check(Bounds(status).Right <= Bounds(basket.DeleteButton).Left - 8, "选择状态与删除按钮使用独立空间");
            Check(Bounds(basket.DeleteButton).Right <= Bounds(basket.HangButton).Left - 8, "底部两个按钮间距稳定");
        }

        static Rect Bounds(FrameworkElement element) { return element.TransformToAncestor(basket).TransformBounds(new Rect(element.RenderSize)); }

        static void CheckFooter()
        {
            basket.UpdateLayout();
            var status = (TextBlock)typeof(BasketWindow).GetField("status", Fields).GetValue(basket);
            Rect select = Bounds(basket.SelectAllButton), hang = Bounds(basket.HangButton), delete = Bounds(basket.DeleteButton);
            Check(select.Left < basket.ActualWidth / 3 && Math.Abs(select.Top - hang.Top) < 1, "全选位于左下角，与底部操作按钮对齐");
            Check(select.Right <= Bounds(status).Left - 8, "全选按钮与已选数量之间留有间距");
            Check(Bounds(status).Right <= delete.Left - 8, "数量提示不覆盖删除按钮");
            Check(delete.Right <= hang.Left - 8, "删除和挂图按钮不重叠");
        }

        static void Save(string name, double scale)
        {
            basket.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(basket.ActualWidth * scale), (int)Math.Ceiling(basket.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(basket);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout-preview"); Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, name + ".png"), ImageContent.EncodeClipboard(image));
        }

        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
    }
}
