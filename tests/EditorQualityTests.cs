using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class EditorQualityTests
    {
        [STAThread]
        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async delegate
            {
                try { await Run(); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                finally { app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        static int passed, failed;

        static async Task Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "editor-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine("Synthetic fixtures and saved results: " + root);
            const int width = 1600, height = 1000;
            byte[] grid = Pixels(width, height, (x, y, channel) => channel == 3 ? (byte)255 : (byte)((x + y) % 2 == 0 ? 0 : 255));
            await Fixture(root, "pixel-grid", width, height, grid, async (window, path) =>
            {
                await Check("single-pixel grid / actual save", async delegate
                {
                    await Save(window);
                    AssertPixels(path, grid, width, height, 0, 0, width, height, 0, false, false);
                });
                await Check("single-pixel grid / identity-filter cache", async delegate
                {
                    await Script(window, IdentityFilter);
                    string data = Json.Deserialize<string>(await window.Browser.ExecuteScriptAsync("editor.getCurrentImgData({extension:'png'},1).imageData.imageBase64"));
                    string filteredPath = Path.Combine(root, "grid-filtered.png");
                    File.WriteAllBytes(filteredPath, ImageEditFiles.Decode(data));
                    AssertPixels(filteredPath, grid, width, height, 0, 0, width, height, 0, false, false);
                });
                await ExportContract(window, "grid / export canvas and PNG agree with one render");
            });

            // Coordinate-dependent, asymmetric colors catch wrong offsets, direction and resampling.
            byte[] colors = Pixels(width, height, (x, y, channel) => channel == 3 ? (byte)255 :
                (byte)((x * (channel * 4 + 3) + y * (channel * 6 + 5) + channel * 71) % 256));
            await Fixture(root, "geometry", width, height, colors, async (window, path) =>
            {
                await Geometry(window, path, colors, width, height, "crop / offset source rectangle", 137, 83, 613, 397, 0, false, false, false);
                await Geometry(window, path, colors, width, height, "crop + clockwise 90 degrees", 137, 83, 613, 397, 90, false, false, false);
                await Geometry(window, path, colors, width, height, "full image / clockwise 90 degrees", 0, 0, width, height, 90, false, false, false);
                await Geometry(window, path, colors, width, height, "full image / 180 degrees", 0, 0, width, height, 180, false, false, false);
            });

            // These alpha/RGB values have exact premultiplied representations.
            // Hidden RGB at alpha=0 deliberately differs from black; canvas may discard it.
            byte[] alpha = Pixels(width, height, (x, y, channel) => channel == 3 ? (byte)(((x + y * 3) % 4) * 85) :
                (byte)(((x * (channel + 1) + y * (channel + 3) + channel + 1) % 6) * 51));
            await Fixture(root, "transparent", width, height, alpha, async (window, path) =>
            {
                await Geometry(window, path, alpha, width, height, "alpha / actual save", 0, 0, width, height, 0, false, false, false);
                await Geometry(window, path, alpha, width, height, "alpha / identity-filter cache + actual save", 0, 0, width, height, 0, false, false, true);
                await Geometry(window, path, alpha, width, height, "alpha / cached horizontal flip + actual save", 0, 0, width, height, 0, true, false, true);
                await Geometry(window, path, alpha, width, height, "alpha / cached vertical flip + actual save", 0, 0, width, height, 0, false, true, true);
                await Geometry(window, path, alpha, width, height, "alpha / cached crop + clockwise 90 degrees", 137, 83, 613, 397, 90, false, false, true);
                await ExportContract(window, "alpha / cached crop and rotation export contract");
            });

            const int emptyWidth = 257, emptyHeight = 129;
            byte[] empty = Pixels(emptyWidth, emptyHeight, (x, y, channel) => channel == 3 ? (byte)0 : (byte)(51 * ((x + y + channel + 1) % 6)));
            await Fixture(root, "fully-transparent", emptyWidth, emptyHeight, empty, async (window, path) =>
            {
                await Check("fully transparent PNG / alpha survives actual save", async delegate
                {
                    await Save(window);
                    AssertPixels(path, empty, emptyWidth, emptyHeight, 0, 0, emptyWidth, emptyHeight, 0, false, false);
                });
                await ExportContract(window, "fully transparent / export contract");
            });
            Console.WriteLine("Editor quality: {0} passed, {1} failed.", passed, failed);
            if (failed != 0) Environment.ExitCode = 1;
        }

        const string IdentityFilter = "const node=canvasStage.findOne('#FIE_original-image');" +
            "node.filters([function qualityIdentityFilter(){}]);" +
            "if(node.filters().length!==1)throw new Error('Identity filter was not installed');";

        static byte[] Pixels(int width, int height, Func<int, int, int, byte> value)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    for (int channel = 0; channel < 4; channel++)
                        pixels[(y * width + x) * 4 + channel] = value(x, y, channel);
            return pixels;
        }

        static async Task Fixture(string root, string name, int width, int height, byte[] pixels, Func<ImageEditorWindow, string, Task> tests)
        {
            string path = Path.Combine(root, name + ".png");
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            // Encode directly: clipboard screenshot normalization makes all-zero alpha opaque.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var window = new ImageEditorWindow(path, new StateStore(root), p => Task.FromResult(0), delegate { });
            window.ShowActivated = false;
            window.Show();
            try
            {
                if (await Task.WhenAny(window.Ready, Task.Delay(20000)) != window.Ready) throw new Exception(name + ": editor ready timed out");
                await window.Ready;
                await tests(window, path);
            }
            finally { window.CloseWithoutPrompt(); }
        }

        static async Task Check(string name, Func<Task> test)
        {
            try { await test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        static async Task Geometry(ImageEditorWindow window, string path, byte[] source, int sourceWidth, int sourceHeight,
            string name, int left, int top, int width, int height, int rotation, bool flipX, bool flipY, bool cache)
        {
            await Check(name, async delegate
            {
                // Filerobot stores crop coordinates in displayed-image units.
                string adjustment = Json.Serialize(new { left, top, width, height, rotation, flipX, flipY, sourceWidth, sourceHeight });
                await Script(window, "const p=" + adjustment + ";const state=await readState();" +
                    "const sx=state.shownImageDimensions.width/p.sourceWidth,sy=state.shownImageDimensions.height/p.sourceHeight;" +
                    "editor.updateState({adjustments:{crop:{x:p.left*sx,y:p.top*sy,width:p.width*sx,height:p.height*sy,ratio:null,noEffect:false}," +
                    "rotation:p.rotation,isFlippedX:p.flipX,isFlippedY:p.flipY},isDesignState:true});" +
                    "await nextFrame();await nextFrame();const actual=(await readState()).adjustments;" +
                    "if(actual.rotation!==p.rotation||actual.isFlippedX!==p.flipX||actual.isFlippedY!==p.flipY||" +
                    "Math.abs(actual.crop.x-p.left*sx)>0.0001||Math.abs(actual.crop.y-p.top*sy)>0.0001||" +
                    "Math.abs(actual.crop.width-p.width*sx)>0.0001||Math.abs(actual.crop.height-p.height*sy)>0.0001)" +
                    "throw new Error('Geometry state did not apply: '+JSON.stringify(actual));" +
                    (cache ? IdentityFilter : "canvasStage.findOne('#FIE_original-image').filters([]);"));
                await Save(window);
                AssertPixels(path, source, sourceWidth, sourceHeight, left, top, width, height, rotation, flipX, flipY);
            });
        }

        static async Task Script(ImageEditorWindow window, string body)
        {
            // ExecuteScriptAsync returns before JS promises settle. Keep the WPF event loop pumping.
            await window.Browser.ExecuteScriptAsync("window.qualityDone=false;window.qualityError=null;" +
                "(async()=>{try{" + body + "}catch(error){window.qualityError=String(error.stack||error);}" +
                "finally{window.qualityDone=true;}})();");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (await window.Browser.ExecuteScriptAsync("window.qualityDone===true") != "true")
            {
                if (timer.ElapsedMilliseconds >= 15000) throw new TimeoutException("JavaScript operation timed out");
                await Task.Delay(50);
            }
            string error = await window.Browser.ExecuteScriptAsync("window.qualityError");
            if (error != "null") throw new Exception(Json.Deserialize<string>(error));
        }

        static Task ExportContract(ImageEditorWindow window, string name)
        {
            return Check(name, () => Script(window,
                "const proto=Object.getPrototypeOf(canvasStage),render=proto._toKonvaCanvas;let count=0;" +
                "if(typeof render!=='function')throw new Error('Stage renderer not found');" +
                "proto._toKonvaCanvas=function(...args){count++;return render.apply(this,args);};" +
                "let output;try{output=editor.getCurrentImgData({extension:'png'},1).imageData;}" +
                "finally{proto._toKonvaCanvas=render;}" +
                "if(count!==1)throw new Error('Expected one stage render, got '+count);" +
                "const image=new Image();await new Promise((resolve,reject)=>{image.onload=resolve;image.onerror=reject;image.src=output.imageBase64;});" +
                "const canvas=output.imageCanvas,decoded=document.createElement('canvas');" +
                "if(image.naturalWidth!==canvas.width||image.naturalHeight!==canvas.height)throw new Error('Canvas and PNG dimensions differ');" +
                "decoded.width=canvas.width;decoded.height=canvas.height;const context=decoded.getContext('2d');context.drawImage(image,0,0);" +
                "const a=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data," +
                "b=context.getImageData(0,0,canvas.width,canvas.height).data;" +
                "for(let i=0;i<a.length;i++)if(a[i]!==b[i])throw new Error('Canvas and PNG pixels differ at channel '+i);"));
        }

        static Task Save(ImageEditorWindow window)
        {
            // Wait for the real .NET save acknowledgement, not just submit's promise or !busy.
            return Script(window, "await new Promise((resolve,reject)=>{" +
                "const complete=window.completeAction;window.completeAction=result=>{" +
                "window.completeAction=complete;complete(result);" +
                "if(result.success&&result.saved)resolve();else reject(new Error(JSON.stringify(result)));};" +
                "window.submit('save').catch(reject);});" +
                "if(busy||document.getElementById('status').textContent!=='已保存')throw new Error('Save did not complete');");
        }

        static void AssertPixels(string path, byte[] source, int sourceWidth, int sourceHeight,
            int left, int top, int width, int height, int rotation, bool flipX, bool flipY)
        {
            BitmapSource result;
            using (var stream = File.OpenRead(path))
                result = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            int outputWidth = rotation == 90 ? height : width, outputHeight = rotation == 90 ? width : height;
            if (result.PixelWidth != outputWidth || result.PixelHeight != outputHeight)
                throw new Exception(String.Format("Expected {0}x{1}, got {2}x{3}", outputWidth, outputHeight, result.PixelWidth, result.PixelHeight));
            byte[] actual = new byte[outputWidth * outputHeight * 4];
            new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0).CopyPixels(actual, outputWidth * 4, 0);
            int changedAlpha = 0, changedColor = 0, transparent = 0, partial = 0;
            string first = null;
            for (int y = 0; y < outputHeight; y++)
                for (int x = 0; x < outputWidth; x++)
                {
                    // Inverse clockwise rotation, crop translation, then full-source flip.
                    int cropX = rotation == 90 ? y : rotation == 180 ? width - 1 - x : x;
                    int cropY = rotation == 90 ? height - 1 - x : rotation == 180 ? height - 1 - y : y;
                    int sourceX = left + cropX, sourceY = top + cropY;
                    if (flipX) sourceX = sourceWidth - 1 - sourceX;
                    if (flipY) sourceY = sourceHeight - 1 - sourceY;
                    int expectedOffset = (sourceY * sourceWidth + sourceX) * 4, offset = (y * outputWidth + x) * 4;
                    byte alpha = source[expectedOffset + 3];
                    if (alpha == 0) transparent++;
                    else if (alpha != 255) partial++;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        // Straight RGB beneath zero alpha is invisible and may be normalized by canvas.
                        if (channel != 3 && alpha == 0) continue;
                        if (actual[offset + channel] == source[expectedOffset + channel]) continue;
                        if (channel == 3) changedAlpha++; else changedColor++;
                        if (first == null) first = String.Format("output ({0},{1}), source ({2},{3}), BGRA[{4}] expected {5}, got {6}",
                            x, y, sourceX, sourceY, channel, source[expectedOffset + channel], actual[offset + channel]);
                    }
                }
            Console.WriteLine("  {0}x{1}: alpha mismatches={2}, visible RGB mismatches={3}; transparent={4}, partial={5}",
                outputWidth, outputHeight, changedAlpha, changedColor, transparent, partial);
            if (first != null) throw new Exception(first);
        }
    }
}
